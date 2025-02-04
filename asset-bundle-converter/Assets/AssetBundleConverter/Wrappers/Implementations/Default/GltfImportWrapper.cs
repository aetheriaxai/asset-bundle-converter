﻿using AssetBundleConverter.Wrappers.Interfaces;
using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace AssetBundleConverter.Wrappers.Implementations.Default
{
    public class GltfImportWrapper : IGltfImport
    {
        private GltfImport importer;
        private ConsoleLogger logger;

        public GltfImportWrapper(GltFastFileProvider gltFastFileProvider, UninterruptedDeferAgent uninterruptedDeferAgent, IMaterialGenerator getNewMaterialGenerator, ConsoleLogger consoleLogger)
        {
            logger = consoleLogger;
            importer = new GltfImport(gltFastFileProvider, uninterruptedDeferAgent, getNewMaterialGenerator, logger);
        }

        public async Task Load(string gltfUrl, ImportSettings importSettings) =>
            await importer.Load(gltfUrl, importSettings);

        public bool LoadingDone => importer.LoadingDone;
        public bool LoadingError => importer.LoadingError;
        public LogCode LastErrorCode => logger.LastErrorCode;
        public int TextureCount => importer.TextureCount;
        public int MaterialCount => importer.MaterialCount;

        public Texture2D GetTexture(int index) =>
            importer.GetTexture(index);

        public Material GetMaterial(int index) =>
            importer.GetMaterial(index);

        public void Dispose()
        {
            importer.Dispose();
        }
    }
}
