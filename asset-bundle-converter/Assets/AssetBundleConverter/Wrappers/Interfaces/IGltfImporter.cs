﻿using AssetBundleConverter.Editor;
using DCL.ABConverter;
using System.Collections.Generic;

namespace AssetBundleConverter.Wrappers.Interfaces
{
    public interface IGltfImporter
    {
        IGltfImport GetImporter(AssetPath filePath, Dictionary<string, string> contentTable, ShaderType shaderType);

        bool ConfigureImporter(string relativePath, ContentMap[] contentMap, string fileRootPath, string hash, ShaderType shaderType);
    }
}
