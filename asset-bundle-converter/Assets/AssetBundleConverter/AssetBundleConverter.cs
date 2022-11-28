﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetBundleConverter.Editor;
using AssetBundleConverter.Wrappers.Implementations.Default;
using DCL.GLTFast.Wrappers;
using GLTFast;
using GLTFast.Editor;
using GLTFast.Logging;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Networking;

namespace DCL.ABConverter
{
    public class AssetBundleConverter
    {
        public enum ErrorCodes
        {
            SUCCESS,
            UNDEFINED,
            SCENE_LIST_NULL,
            ASSET_BUNDLE_BUILD_FAIL,
            VISUAL_TEST_FAILED
        }

        public class State
        {
            public enum Step
            {
                IDLE,
                DUMPING_ASSETS,
                BUILDING_ASSET_BUNDLES,
                FINISHED,
            }

            public Step step { get; internal set; }
            public ErrorCodes lastErrorCode { get; internal set; }
        }

        private const float MAX_TEXTURE_SIZE = 512f;
        private const string VERSION = "3.0";
        private const string MAIN_SHADER_AB_NAME = "MainShader_Delete_Me";

        private static Logger log = new Logger("[AssetBundleConverter]");
        private readonly Dictionary<string, string> lowerCaseHashes = new Dictionary<string, string>();
        public State CurrentState { get; } = new State();
        private Environment env;
        private ClientSettings settings;
        private readonly string finalDownloadedPath;
        private readonly string finalDownloadedAssetDbPath;
        private List<AssetPath> assetsToMark = new List<AssetPath>();
        private Dictionary<string, GltfImport> gltfToWait = new Dictionary<string, GltfImport>();
        private Dictionary<string, string> contentTable = new Dictionary<string, string>();
        private Dictionary<string, string> gltfOriginalNames = new Dictionary<string, string>();
        private string logBuffer;
        private int totalAssets;
        private float startTime;
        private int skippedAssets;

        public AssetBundleConverter(Environment env, ClientSettings settings)
        {
            this.settings = settings;
            this.env = env;

            finalDownloadedPath = PathUtils.FixDirectorySeparator(Config.DOWNLOADED_PATH_ROOT + Config.DASH);
            finalDownloadedAssetDbPath = PathUtils.FixDirectorySeparator(Config.ASSET_BUNDLES_PATH_ROOT + Config.DASH);

            log.verboseEnabled = true;
        }

        /// <summary>
        /// Entry point of the AssetBundleConverter
        /// </summary>
        /// <param name="rawContents"></param>
        /// <returns></returns>
        public async Task<State> Convert(ContentServerUtils.MappingPair[] rawContents)
        {
            startTime = Time.realtimeSinceStartup;
            log.Info("Starting a new conversion");

            // First step: initialize directories to download the original assets and to store the results
            InitializeDirectoryPaths(settings.clearDirectoriesOnStart, settings.clearDirectoriesOnStart);
            env.assetDatabase.Refresh();

            await Task.Delay(TimeSpan.FromSeconds(1));

            // Second step: we download all assets
            PopulateLowercaseMappings(rawContents);

            if (!DownloadAssets(rawContents))
            {
                log.Info("All assets are already converted");
                OnFinish();

                return CurrentState;
            }

            // Third step: we import gltfs
            if (await ImportAllGltf())
            {
                OnFinish();

                return CurrentState;
            }

            EditorUtility.ClearProgressBar();

            if (settings.createAssetBundle)
            {
                // Fourth step: we mark all assets for bundling
                MarkAllAssetBundles(assetsToMark);
                MarkShaderAssetBundle();

                // Fifth step: we build the Asset Bundles
                env.assetDatabase.Refresh();
                env.assetDatabase.SaveAssets();
                CurrentState.step = State.Step.BUILDING_ASSET_BUNDLES;

                if (BuildAssetBundles(out var manifest))
                {
                    CleanAssetBundleFolder(manifest.GetAllAssetBundles());

                    CurrentState.lastErrorCode = ErrorCodes.SUCCESS;
                    CurrentState.step = State.Step.FINISHED;
                }
                else
                {
                    CurrentState.lastErrorCode = ErrorCodes.ASSET_BUNDLE_BUILD_FAIL;
                    CurrentState.step = State.Step.FINISHED;
                }

                if (settings.visualTest)
                {
                    bool result = await VisualTests.TestConvertedAssets(env: env);

                    if (!result)
                    {
                        log.Error("Visual tests failed");
                        CurrentState.lastErrorCode = ErrorCodes.VISUAL_TEST_FAILED;
                    }
                }
            }

            OnFinish();

            return CurrentState;
        }

        /// <summary>
        /// During this step we import gltfs into the scene and then we import the gltf so it can be marked for AB conversion
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ImportAllGltf()
        {
            var totalGltfToLoad = gltfToWait.Count;
            var loadedGltf = 0;

            // Its expected to have errors here since unity will try to import gltf's without our custom settings
            // this step is required to grab the gltfImporter and change its values
            Debug.unityLogger.logEnabled = false;
            AssetDatabase.Refresh();
            Debug.unityLogger.logEnabled = true;

            foreach (KeyValuePair<string, GltfImport> keyValuePair in gltfToWait)
            {
                var gltfUrl = keyValuePair.Key;
                var gltfImport = keyValuePair.Value;

                EditorUtility.DisplayProgressBar("Asset Bundle Converter", $"Loading GLTF {gltfUrl}",
                    loadedGltf / (float)totalGltfToLoad);

                loadedGltf++;
                log.Verbose($"Starting to import gltf {gltfUrl}");

                try { await gltfImport.Load(gltfUrl); }
                catch (Exception)
                {
                    EditorUtility.ClearProgressBar();

                    throw;
                }

                var loadingSuccess = gltfImport.LoadingDone && !gltfImport.LoadingError;
                var color = loadingSuccess ? "green" : "red";

                if (!loadingSuccess)
                {
                    log.Error("Importing gltf Failed");
                    EditorUtility.ClearProgressBar();

                    return true;
                }

                //var name = gltfOriginalNames[gltfUrl];
                var go = new GameObject(gltfUrl);

                if (settings.visualTest || !settings.createAssetBundle)
                {
                    try { await gltfImport.InstantiateSceneAsync(go.transform); }
                    catch (Exception)
                    {
                        EditorUtility.ClearProgressBar();

                        throw;
                    }
                }

                var renderers = go.GetComponentsInChildren<Renderer>(true);

                foreach (Renderer renderer in renderers)
                {
                    if (renderer.name.ToLower().Contains("_collider")) { renderer.enabled = false; }
                }

                //File.WriteAllText($"{gltfFilePath.Directory.FullName}/metrics.json", JsonUtility.ToJson(metrics, true));

                var relativePath = PathUtils.FullPathToAssetPath(gltfUrl);

                // create dummy textures

                var textures = new List<Texture2D>();

                for (int i = 0; i < gltfImport.textureCount; i++)
                {
                    textures.Add(gltfImport.GetTexture(i));
                }

                var directory = Path.GetDirectoryName(relativePath);
                CreateTextureAssets(textures, directory);

                await Task.Delay(TimeSpan.FromSeconds(1));

                // end create dummy textures

                var gltfImporter = AssetImporter.GetAtPath(relativePath) as CustomGltfImporter;

                if (gltfImporter != null)
                {
                    gltfImporter.importSettings.animationMethod = ImportSettings.AnimationMethod.Legacy;
                    gltfImporter.importSettings.generateMipMaps = false;
                    gltfImporter.contentMaps = contentTable.Select(kvp => new ContentMap(kvp.Key, kvp.Value)).ToArray();

                    EditorUtility.SetDirty(gltfImporter);
                    gltfImporter.SaveAndReimport();
                }
                else
                {
                    log.Error($"Failed to get the gltf importer for {gltfUrl}");
                    EditorUtility.ClearProgressBar();

                    return true;
                }

                log.Verbose($"<color={color}>Ended loading gltf {gltfUrl} with result <b>{loadingSuccess}</b></color>");
            }

            EditorUtility.ClearProgressBar();

            log.Info("Assets Loaded successfully");

            return false;
        }

        private void CreateTextureAssets(List<Texture2D> textures, string folderName)
        {
            if (textures.Count > 0)
            {
                var texturesRoot = $"{folderName}/Textures/";

                for (int i = 0; i < textures.Count; i++)
                {
                    var tex = textures[i];
                    var ext = ".png";
                    var texPath = string.Concat(texturesRoot, tex.name, ext);
                    var absolutePath = $"{Application.dataPath}/../{texPath}";
                    var texturePath = AssetDatabase.GetAssetPath(tex);

                    if (File.Exists(absolutePath) || !string.IsNullOrEmpty(texturePath))
                        continue;

                    if (!Directory.Exists(texturesRoot))
                        Directory.CreateDirectory(texturesRoot);

                    if (tex.isReadable)
                    {
                        File.WriteAllBytes(texPath, tex.EncodeToPNG());
                    }
                    else
                    {
                        RenderTexture tmp = RenderTexture.GetTemporary(
                            tex.width,
                            tex.height,
                            0,
                            RenderTextureFormat.Default,
                            RenderTextureReadWrite.Linear);

                        Graphics.Blit(tex, tmp);
                        RenderTexture previous = RenderTexture.active;
                        RenderTexture.active = tmp;
                        Texture2D readableTexture = new Texture2D(tex.width, tex.height);
                        readableTexture.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                        readableTexture.Apply();
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(tmp);

                        File.WriteAllBytes(texPath, readableTexture.EncodeToPNG());
                    }
                    texPath = texPath.Replace('\\', '/');
                    AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    Debug.Log($"[AB Converter]created texture at {texPath}");
                }
            }
        }

        private AssetBundleConverterMaterialGenerator GetNewMaterialGenerator()
        {
            return new AssetBundleConverterMaterialGenerator();
        }

        private void OnFinish()
        {
            if (settings.cleanAndExitOnFinish) { CleanAndExit(CurrentState.lastErrorCode); }
        }

        /// <summary>
        /// Mark all the given assetPaths to be built as asset bundles by Unity's BuildPipeline.
        /// </summary>
        /// <param name="assetPaths">The paths to be built.</param>
        private void MarkAllAssetBundles(List<AssetPath> assetPaths)
        {
            foreach (var assetPath in assetPaths) { Utils.MarkFolderForAssetBundleBuild(assetPath.finalPath, assetPath.hash); }
        }

        /// <summary>
        /// Clean all working folders and end the batch process.
        /// </summary>
        /// <param name="errorCode">final errorCode of the conversion process</param>
        public void CleanAndExit(ErrorCodes errorCode)
        {
            foreach (KeyValuePair<string, GltfImport> keyValuePair in gltfToWait) { keyValuePair.Value.Dispose(); }

            float conversionTime = Time.realtimeSinceStartup - startTime;
            logBuffer = $"Conversion finished!. last error code = {errorCode}";

            logBuffer += "\n";
            logBuffer += $"Converted {totalAssets - skippedAssets} of {totalAssets}. (Skipped {skippedAssets})\n";
            logBuffer += $"Total time: {conversionTime}";

            if (totalAssets > 0) { logBuffer += $"... Time per asset: {conversionTime / totalAssets}\n"; }

            logBuffer += "\n";
            logBuffer += logBuffer;

            log.Info(logBuffer);

            //CleanupWorkingFolders();

            Utils.Exit((int)errorCode);
        }

        internal void CleanupWorkingFolders()
        {
            env.file.Delete(settings.finalAssetBundlePath + Config.ASSET_BUNDLE_FOLDER_NAME);
            env.file.Delete(settings.finalAssetBundlePath + Config.ASSET_BUNDLE_FOLDER_NAME + ".manifest");

            if (settings.deleteDownloadPathAfterFinished)
            {
                env.directory.Delete(finalDownloadedPath);
                env.assetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        /// <summary>
        /// Sync version of the GetOrDownloadDependenciesPaths method
        /// </summary>
        /// <param name="url">Relative path to the asset</param>
        /// <returns></returns>
        private Uri GetDependenciesPaths(Uri url)
        {
            try
            {
                var normalizedString = url.OriginalString.Replace('\\', '/');
                string fileName = normalizedString.Substring(normalizedString.LastIndexOf('/') + 1);

                return !contentTable.ContainsKey(fileName) ? url : new Uri(contentTable[fileName], UriKind.Relative);
            }
            catch (Exception)
            {
                log.Exception($"Failed to transform path: {url.OriginalString}");

                return url;
            }
        }

        /// <summary>
        /// Build all marked paths as asset bundles using Unity's BuildPipeline and generate their metadata file (dependencies, version, timestamp)
        /// </summary>
        /// <param name="manifest">AssetBundleManifest generated by the build.</param>
        /// <returns>true is build was successful</returns>
        public virtual bool BuildAssetBundles(out AssetBundleManifest manifest)
        {
            logBuffer = "";

            env.assetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate |
                                      ImportAssetOptions.ImportRecursive);

            env.assetDatabase.SaveAssets();

            env.assetDatabase.MoveAsset(finalDownloadedPath, Config.DOWNLOADED_PATH_ROOT);

            // 1. Convert flagged folders to asset bundles only to automatically get dependencies for the metadata
            manifest = env.buildPipeline.BuildAssetBundles(settings.finalAssetBundlePath,
                BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.WebGL);

            if (manifest == null)
            {
                log.Error("Error generating asset bundle!");

                return false;
            }

            // 2. Create metadata (dependencies, version, timestamp) and store in the target folders to be converted again later with the metadata inside
            AssetBundleMetadataBuilder.Generate(env.file, finalDownloadedPath, lowerCaseHashes, manifest, VERSION,
                MAIN_SHADER_AB_NAME);

            env.assetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate |
                                      ImportAssetOptions.ImportRecursive);

            env.assetDatabase.SaveAssets();

            // 3. Convert flagged folders to asset bundles again but this time they have the metadata file inside
            manifest = env.buildPipeline.BuildAssetBundles(settings.finalAssetBundlePath,
                BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.WebGL);

            logBuffer += $"Generating asset bundles at path: {settings.finalAssetBundlePath}\n";

            string[] assetBundles = manifest.GetAllAssetBundles();

            logBuffer += $"Total generated asset bundles: {assetBundles.Length}\n";

            for (int i = 0; i < assetBundles.Length; i++)
            {
                if (string.IsNullOrEmpty(assetBundles[i]))
                    continue;

                logBuffer += $"#{i} Generated asset bundle name: {assetBundles[i]}\n";
            }

            logBuffer += $"\nFree disk space after conv: {PathUtils.GetFreeSpace()}";

            log.Verbose(logBuffer);

            return true;
        }

        private void CleanAssetBundleFolder(string[] assetBundles)
        {
            Utils.CleanAssetBundleFolder(env.file, settings.finalAssetBundlePath, assetBundles, lowerCaseHashes);
        }

        void InitializeDirectoryPaths(bool deleteDownloadDirIfExists, bool deleteABsDireIfExists)
        {
            log.Info("Initializing directory -- " + finalDownloadedPath);
            env.directory.InitializeDirectory(finalDownloadedPath, deleteDownloadDirIfExists);
            log.Info("Initializing directory -- " + settings.finalAssetBundlePath);
            env.directory.InitializeDirectory(settings.finalAssetBundlePath, deleteABsDireIfExists);
        }

        void PopulateLowercaseMappings(ContentServerUtils.MappingPair[] pairs)
        {
            foreach (var content in pairs)
            {
                string hashLower = content.hash.ToLowerInvariant();

                if (!lowerCaseHashes.ContainsKey(hashLower))
                    lowerCaseHashes.Add(hashLower, content.hash);
            }
        }

        /// <summary>
        /// Download all assets and tag them for asset bundle building.
        /// </summary>
        /// <param name="rawContents">An array containing all the assets to be dumped.</param>
        /// <returns>true if succeeded</returns>
        private bool DownloadAssets(ContentServerUtils.MappingPair[] rawContents)
        {
            List<AssetPath> gltfPaths =
                Utils.GetPathsFromPairs(finalDownloadedPath, rawContents, Config.gltfExtensions);

            List<AssetPath> bufferPaths =
                Utils.GetPathsFromPairs(finalDownloadedPath, rawContents, Config.bufferExtensions);

            List<AssetPath> texturePaths =
                Utils.GetPathsFromPairs(finalDownloadedPath, rawContents, Config.textureExtensions);

            if (!FilterDumpList(ref gltfPaths))
                return false;

            //NOTE(Brian): Prepare textures and buffers. We should prepare all the dependencies in this phase.
            assetsToMark.AddRange(DownloadImportableAssets(texturePaths));
            assetsToMark.AddRange(DownloadRawAssets(bufferPaths));

            AddAssetPathsToContentTable(texturePaths);
            AddAssetPathsToContentTable(bufferPaths);

            foreach (var gltfPath in gltfPaths) { assetsToMark.Add(DownloadGltf(gltfPath)); }

            return true;
        }

        private void AddAssetPathsToContentTable(List<AssetPath> assetPaths)
        {
            foreach (AssetPath assetPath in assetPaths)
            {
                string fileName = assetPath.file.Substring(assetPath.file.LastIndexOf('/') + 1);
                var relativeFinalPath = "Assets" + assetPath.finalPath.Substring(Application.dataPath.Length);
                contentTable[fileName] = relativeFinalPath;
            }
        }

        /// <summary>
        /// Trims off existing asset bundles from the given AssetPath array,
        /// if none exists and shouldAbortBecauseAllBundlesExist is true, it will return false.
        /// </summary>
        /// <param name="gltfPaths">paths to be checked for existence</param>
        /// <returns>false if all paths are already converted to asset bundles, true if the conversion makes sense</returns>
        internal bool FilterDumpList(ref List<AssetPath> gltfPaths)
        {
            bool shouldBuildAssetBundles;

            totalAssets = gltfPaths.Count;

            if (settings.skipAlreadyBuiltBundles)
            {
                int gltfCount = gltfPaths.Count;

                gltfPaths = gltfPaths.Where(
                                          assetPath =>
                                              !env.file.Exists(settings.finalAssetBundlePath + assetPath.hash))
                                     .ToList();

                int skippedCount = gltfCount - gltfPaths.Count;
                skippedAssets = skippedCount;
                shouldBuildAssetBundles = gltfPaths.Count == 0;
            }
            else { shouldBuildAssetBundles = false; }

            if (shouldBuildAssetBundles)
            {
                log.Info("All assets in this scene were already generated!. Skipping.");

                return false;
            }

            return true;
        }

        /// <summary>
        /// This will download all assets contained in the AssetPath list using the baseUrl + hash.
        ///
        /// After the assets are downloaded, they will be imported using Unity's AssetDatabase and
        /// their guids will be normalized using the asset's cid.
        ///
        /// The guid normalization will ensure the guids remain consistent and the same asset will
        /// always have the asset guid. If we don't normalize the guids, Unity will chose a random one,
        /// and this can break the Asset Bundles dependencies as they are resolved by guid.
        /// </summary>
        /// <param name="assetPaths">List of assetPaths to be dumped</param>
        /// <returns>A list with assetPaths that were successfully dumped. This list will be empty if all dumps failed.</returns>
        internal List<AssetPath> DownloadImportableAssets(List<AssetPath> assetPaths)
        {
            List<AssetPath> result = new List<AssetPath>(assetPaths);

            for (var i = 0; i < assetPaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Asset Bundle Converter", "Downloading Importable Assets",
                    i / (float)assetPaths.Count);

                var assetPath = assetPaths[i];

                if (env.file.Exists(assetPath.finalPath))
                    continue;

                //NOTE(Brian): try to get an AB before getting the original texture, so we bind the dependencies correctly
                string fullPathToTag = DownloadAsset(assetPath);

                if (fullPathToTag == null)
                {
                    result.Remove(assetPath);
                    log.Error("Failed to get texture dependencies! failing asset: " + assetPath.hash);

                    continue;
                }

                var importer = env.assetDatabase.GetImporterAtPath(assetPath.finalPath);

                if (importer is TextureImporter texImporter)
                {
                    texImporter.crunchedCompression = true;
                    texImporter.textureCompression = TextureImporterCompression.CompressedHQ;
                    texImporter.isReadable = true;

                    ReduceTextureSizeIfNeeded(assetPath.hash + "/" + assetPath.hash + Path.GetExtension(assetPath.file),
                        MAX_TEXTURE_SIZE);
                }
                else
                {
                    env.assetDatabase.ImportAsset(assetPath.finalPath, ImportAssetOptions.ForceUpdate);
                    env.assetDatabase.SaveAssets();
                }

                SetDeterministicAssetDatabaseGuid(assetPath);

                log.Verbose($"Downloaded <b>file</b> -> {assetPath}");
            }

            EditorUtility.ClearProgressBar();

            return result;
        }

        /// <summary>
        /// in asset bundles, all dependencies are resolved by their guid (and not the AB hash nor CRC)
        /// So to ensure dependencies are being kept in subsequent editor runs we normalize the asset guid using
        /// the CID.
        ///
        /// This method:
        /// - Looks for the meta file of the given assetPath.
        /// - Changes the .meta guid using the assetPath's cid as seed.
        /// - Does some file system gymnastics to make sure the new guid is imported to our AssetDatabase.
        /// </summary>
        /// <param name="assetPath">AssetPath of the target asset to modify</param>
        private void SetDeterministicAssetDatabaseGuid(AssetPath assetPath)
        {
            string metaPath = env.assetDatabase.GetTextMetaFilePathFromAssetPath(assetPath.finalPath);

            env.assetDatabase.ReleaseCachedFileHandles();

            string metaContent = env.file.ReadAllText(metaPath);
            string guid = Utils.CidToGuid(assetPath.hash);
            string newMetaContent = Regex.Replace(metaContent, @"guid: \w+?\n", $"guid: {guid}\n");

            //NOTE(Brian): We must do this hack in order to the new guid to be added to the AssetDatabase.
            //             on windows, an AssetImporter.SaveAndReimport call makes the trick, but this won't work
            //             on Unix based OSes for some reason.
            env.file.Delete(metaPath);

            env.file.Copy(assetPath.finalPath, finalDownloadedPath + "tmp");
            env.assetDatabase.DeleteAsset(assetPath.finalPath);
            env.file.Delete(assetPath.finalPath);

            env.assetDatabase.Refresh();
            env.assetDatabase.SaveAssets();

            env.file.Copy(finalDownloadedPath + "tmp", assetPath.finalPath);
            env.file.WriteAllText(metaPath, newMetaContent);
            env.file.Delete(finalDownloadedPath + "tmp");
            env.file.Delete(finalDownloadedPath + "tmp.meta");

            env.assetDatabase.Refresh();
            env.assetDatabase.SaveAssets();
        }

        /// <summary>
        /// This will download a single asset referenced by an AssetPath.
        /// The download target is baseUrl + hash.
        /// </summary>
        /// <param name="assetPath">The AssetPath object referencing the asset to be downloaded</param>
        /// <param name="isGltf"></param>
        /// <returns>The file output path. Null if download failed.</returns>
        internal string DownloadAsset(AssetPath assetPath, bool isGltf = false)
        {
            string outputPath = assetPath.finalPath;
            string outputPathDir = Path.GetDirectoryName(outputPath);
            string finalUrl = settings.baseUrl + assetPath.hash;

            void FinallyImportAsset()
            {
                if (isGltf)
                {
                    gltfToWait[outputPath] = CreateGltfImport(outputPath);
                    gltfOriginalNames[outputPath] = assetPath.file;
                }
                else
                {
                    env.assetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
                }
            }

            if (env.file.Exists(outputPath))
            {
                FinallyImportAsset();

                return outputPath;
            }

            DownloadHandler downloadHandler = null;

            try
            {
                downloadHandler = env.webRequest.Get(finalUrl);

                if (downloadHandler == null)
                {
                    log.Error($"Download failed! {finalUrl} -- null DownloadHandler");

                    return null;
                }
            }
            catch (HttpRequestException e)
            {
                log.Error($"Download failed! {finalUrl} -- {e.Message}");

                return null;
            }

            byte[] assetData = downloadHandler.data;
            downloadHandler.Dispose();

            log.Verbose($"Downloaded asset = {finalUrl} to {outputPath}");

            if (!env.directory.Exists(outputPathDir))
                env.directory.CreateDirectory(outputPathDir);

            env.file.WriteAllBytes(outputPath, assetData);

            FinallyImportAsset();

            return outputPath;
        }

        private GltfImport CreateGltfImport(string filePath)
        {
            return new GltfImport(
                new GltFastFileProvider(GetDependenciesPaths),
                new UninterruptedDeferAgent(),
                GetNewMaterialGenerator(),
                new ConsoleLogger());
        }

        private void ReduceTextureSizeIfNeeded(string texturePath, float maxSize)
        {
            string finalTexturePath = finalDownloadedPath + texturePath;

            byte[] image = File.ReadAllBytes(finalTexturePath);

            var tmpTex = new Texture2D(1, 1);

            if (!tmpTex.LoadImage(image))
                return;

            float factor = 1.0f;
            int width = tmpTex.width;
            int height = tmpTex.height;

            float maxTextureSize = maxSize;

            if (width < maxTextureSize && height < maxTextureSize)
                return;

            if (width >= height) { factor = (float)maxTextureSize / width; }
            else { factor = (float)maxTextureSize / height; }

            Texture2D dstTex = Utils.ResizeTexture(tmpTex, (int)(width * factor), (int)(height * factor));
            byte[] endTex = dstTex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tmpTex);

            File.WriteAllBytes(finalTexturePath, endTex);

            AssetDatabase.ImportAsset(PathUtils.GetRelativePathTo(Application.dataPath, finalTexturePath), ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Download assets and put them in the working folder.
        /// </summary>
        /// <param name="bufferPaths">AssetPath list containing all the desired paths to be downloaded</param>
        /// <returns>List of the successfully downloaded assets.</returns>
        internal List<AssetPath> DownloadRawAssets(List<AssetPath> bufferPaths)
        {
            List<AssetPath> result = new List<AssetPath>(bufferPaths);

            if (bufferPaths.Count == 0 || bufferPaths == null)
                return result;

            for (var i = 0; i < bufferPaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Asset Bundle Converter", "Downloading Raw Assets",
                    i / (float)bufferPaths.Count);

                var assetPath = bufferPaths[i];

                if (env.file.Exists(assetPath.finalPath))
                    continue;

                var finalDlPath = DownloadAsset(assetPath);

                if (string.IsNullOrEmpty(finalDlPath))
                {
                    result.Remove(assetPath);
                    log.Error("Failed to get buffer dependencies! failing asset: " + assetPath.hash);
                }
            }

            EditorUtility.ClearProgressBar();

            return result;
        }

        /// <summary>
        /// Download a single gltf asset injecting the proper external references
        /// </summary>
        /// <param name="gltfPath">GLTF to be downloaded</param>
        /// <returns>gltf AssetPath if dump succeeded, null if don't</returns>
        internal AssetPath DownloadGltf(AssetPath gltfPath)
        {
            string path = DownloadAsset(gltfPath, true);

            return path != null ? gltfPath : null;
        }

        /// <summary>
        /// This method tags the main shader, so all the asset bundles don't contain repeated shader assets.
        /// This way we save the big Shader.Parse and gpu compiling performance overhead and make
        /// the bundles a bit lighter.
        /// </summary>
        private void MarkShaderAssetBundle()
        {
            //NOTE(Brian): The shader asset bundle that's going to be generated doesn't need to be really used,
            //             as we are going to use the embedded one, so we are going to just delete it after the
            //             generation ended.
            var mainShader = Shader.Find("DCL/Universal Render Pipeline/Lit");
            var result = Utils.MarkAssetForAssetBundleBuild(env.assetDatabase, mainShader, MAIN_SHADER_AB_NAME);

            if (!result)
            {
                log.Error(
                    $"Failed to mark {PathUtils.GetRelativePathTo(Application.dataPath, env.assetDatabase.GetAssetPath(mainShader))} as asset bundle");
            }
        }

        private void ProcessSkippedAssets(int skippedAssetsCount)
        {
            if (skippedAssetsCount <= 0)
                return;

            skippedAssets = skippedAssetsCount;

            CurrentState.lastErrorCode = skippedAssets >= totalAssets
                ? ErrorCodes.ASSET_BUNDLE_BUILD_FAIL
                : ErrorCodes.VISUAL_TEST_FAILED;
        }
    }
}