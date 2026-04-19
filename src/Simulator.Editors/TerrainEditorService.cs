using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simulator.Assets;
using Simulator.Core;

namespace Simulator.Editors;

public sealed class TerrainEditorService
{
    private readonly ConfigurationService _configurationService;
    private readonly AssetCatalogService _assetCatalogService;

    public TerrainEditorService(ConfigurationService configurationService, AssetCatalogService assetCatalogService)
    {
        _configurationService = configurationService;
        _assetCatalogService = assetCatalogService;
    }

    public IReadOnlyList<string> ListMapPresets(ProjectLayout layout)
    {
        return _assetCatalogService.ListMapPresets(layout);
    }

    public IReadOnlyList<string> SetActiveMapPreset(ProjectLayout layout, string presetName)
    {
        IReadOnlyList<string> presets = ListMapPresets(layout);
        if (!presets.Contains(presetName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Map preset '{presetName}' was not found in map_presets/ or maps/.");
        }

        var writtenPaths = new List<string>();
        foreach (string configPath in _configurationService.ExistingConfigPaths(layout))
        {
            var config = _configurationService.LoadConfig(configPath);
            _configurationService.SetMapPreset(config, presetName);
            _configurationService.SaveConfig(configPath, config);
            writtenPaths.Add(Path.GetRelativePath(layout.RootPath, configPath));
        }

        if (writtenPaths.Count == 0)
        {
            string fallbackPath = layout.CommonSettingPath;
            var config = _configurationService.LoadConfig(fallbackPath);
            _configurationService.SetMapPreset(config, presetName);
            _configurationService.SaveConfig(fallbackPath, config);
            writtenPaths.Add(Path.GetRelativePath(layout.RootPath, fallbackPath));
        }

        return writtenPaths;
    }
}
