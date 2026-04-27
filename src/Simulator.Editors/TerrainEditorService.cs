using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Core.Map;

namespace Simulator.Editors;

public sealed class TerrainEditorService
{
    private readonly ConfigurationService _configurationService;
    private readonly AssetCatalogService _assetCatalogService;
    private readonly MapPresetService _mapPresetService;

    public TerrainEditorService(ConfigurationService configurationService, AssetCatalogService assetCatalogService)
    {
        _configurationService = configurationService;
        _assetCatalogService = assetCatalogService;
        _mapPresetService = new MapPresetService();
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

    public string GetActiveMapPreset(ProjectLayout layout)
    {
        string configPath = _configurationService.ResolvePrimaryConfigPath(layout);
        JsonObject config = _configurationService.LoadConfig(configPath);
        return _configurationService.GetMapPreset(config);
    }

    public MapPresetEditorSettings LoadPresetDocument(ProjectLayout layout, string presetName)
    {
        MapPresetDocument documentSource = _mapPresetService.LoadPresetDocument(layout, presetName);
        JsonObject root = documentSource.Root;
        JsonObject map = documentSource.Map;
        MapPresetDefinition preset = _mapPresetService.LoadPreset(layout, presetName);
        TerrainSurfaceDefinition terrainSurface = preset.TerrainSurface ?? new TerrainSurfaceDefinition();

        return new MapPresetEditorSettings
        {
            PresetName = presetName,
            DisplayName = root["name"]?.ToString() ?? preset.Name,
            SourcePath = documentSource.Source.FullPath,
            Width = preset.Width,
            Height = preset.Height,
            FieldLengthM = preset.FieldLengthM,
            FieldWidthM = preset.FieldWidthM,
            ImagePath = preset.ImagePath,
            RawRoot = root,
            RawMap = map,
            FacilitiesNormalizedToTopDownWorld = false,
            TerrainSurface = new TerrainSurfaceEditorSettings
            {
                MapType = terrainSurface.MapType,
                DescriptorPath = terrainSurface.DescriptorPath,
                StorageKind = terrainSurface.StorageKind,
                Topology = terrainSurface.Topology,
                MergeMode = terrainSurface.MergeMode,
                SplitMode = terrainSurface.SplitMode,
                BaseColorImagePath = terrainSurface.BaseColorImagePath,
                RenderProfile = terrainSurface.RenderProfile,
                TopFaceMode = terrainSurface.TopFaceMode,
                SideFaceMode = terrainSurface.SideFaceMode,
                SideColorHex = terrainSurface.SideColorHex,
                TopNormalThreshold = terrainSurface.TopNormalThreshold,
                SideNormalThreshold = terrainSurface.SideNormalThreshold,
                ResolutionM = terrainSurface.ResolutionM,
                HeightCells = terrainSurface.HeightCells,
                WidthCells = terrainSurface.WidthCells,
                HeightScaleBakedIn = terrainSurface.HeightScaleBakedIn,
                Channels = new Dictionary<string, string>(terrainSurface.Channels, StringComparer.OrdinalIgnoreCase),
            },
            Facilities = new BindingList<FacilityRegionEditorModel>(preset.Facilities.Select(FacilityRegionEditorModel.FromFacility).ToList()),
            TerrainFacets = new BindingList<TerrainFacetEditorModel>(terrainSurface.Facets.Select(TerrainFacetEditorModel.FromFacet).ToList()),
        };
    }

    public void SavePresetDocument(MapPresetEditorSettings document)
    {
        if (document.RawRoot is null || document.RawMap is null)
        {
            throw new InvalidOperationException("Preset document was not loaded from a valid JSON source.");
        }

        JsonObject root = document.RawRoot;
        JsonObject map = document.RawMap;
        root["name"] = document.DisplayName;
        string mapType = map["map_type"]?.ToString() ?? document.TerrainSurface.MapType;
        bool terrainCachePreset = string.Equals(mapType, "terrain_cache_map", StringComparison.OrdinalIgnoreCase);
        map["map_type"] = mapType;
        map["image_path"] = document.ImagePath;
        map["width"] = document.Width;
        map["height"] = document.Height;

        JsonObject coordinateSystem = ConfigurationService.EnsureObject(map, "coordinate_system");
        coordinateSystem["field_length_m"] = document.FieldLengthM;
        coordinateSystem["field_width_m"] = document.FieldWidthM;
        if (terrainCachePreset)
        {
            map.Remove("field_length_m");
            map.Remove("field_width_m");
            map.Remove("terrain_surface");
        }
        else
        {
            map["field_length_m"] = document.FieldLengthM;
            map["field_width_m"] = document.FieldWidthM;

            JsonObject terrainSurface = ConfigurationService.EnsureObject(map, "terrain_surface");
            terrainSurface["map_type"] = document.TerrainSurface.MapType;
            terrainSurface["descriptor_path"] = document.TerrainSurface.DescriptorPath;
            terrainSurface["storage_kind"] = document.TerrainSurface.StorageKind;
            terrainSurface["topology"] = document.TerrainSurface.Topology;
            terrainSurface["merge_mode"] = document.TerrainSurface.MergeMode;
            terrainSurface["split_mode"] = document.TerrainSurface.SplitMode;
            terrainSurface["base_color_image_path"] = document.TerrainSurface.BaseColorImagePath;
            terrainSurface["render_profile"] = document.TerrainSurface.RenderProfile;
            terrainSurface["top_face_mode"] = document.TerrainSurface.TopFaceMode;
            terrainSurface["side_face_mode"] = document.TerrainSurface.SideFaceMode;
            terrainSurface["side_color"] = document.TerrainSurface.SideColorHex;
            terrainSurface["top_normal_threshold"] = document.TerrainSurface.TopNormalThreshold;
            terrainSurface["side_normal_threshold"] = document.TerrainSurface.SideNormalThreshold;

            JsonArray facets = new();
            foreach (TerrainFacetEditorModel facet in document.TerrainFacets)
            {
                IReadOnlyList<Simulator.Core.Map.Point2D> points = facet.ParsePoints();
                if (points.Count < 3)
                {
                    continue;
                }

                IReadOnlyList<double> heights = facet.ParseHeights();
                var pointArray = new JsonArray();
                foreach (Simulator.Core.Map.Point2D point in points)
                {
                    pointArray.Add(new JsonArray(point.X, point.Y));
                }

                var heightArray = new JsonArray();
                for (int index = 0; index < points.Count; index++)
                {
                    double height = index < heights.Count
                        ? heights[index]
                        : (heights.Count == 0 ? 0.0 : heights[^1]);
                    heightArray.Add(height);
                }

                facets.Add(new JsonObject
                {
                    ["id"] = facet.Id,
                    ["type"] = facet.Type,
                    ["team"] = facet.Team,
                    ["top_color"] = facet.TopColorHex,
                    ["side_color"] = facet.SideColorHex,
                    ["collision_enabled"] = facet.CollisionEnabled,
                    ["collision_expand_m"] = facet.CollisionExpandM,
                    ["collision_height_offset_m"] = facet.CollisionHeightOffsetM,
                    ["points"] = pointArray,
                    ["heights_m"] = heightArray,
                });
            }

            terrainSurface["facets"] = facets;
        }

        JsonArray facilities = new();
        foreach (FacilityRegionEditorModel facility in document.Facilities)
        {
            var node = new JsonObject
            {
                ["id"] = facility.Id,
                ["type"] = facility.Type,
                ["team"] = facility.Team,
                ["shape"] = facility.Shape,
                ["x1"] = facility.X1,
                ["y1"] = facility.Y1,
                ["x2"] = facility.X2,
                ["y2"] = facility.Y2,
                ["thickness"] = facility.Thickness,
                ["height_m"] = facility.HeightM,
            };

            IReadOnlyList<Simulator.Core.Map.Point2D> points = facility.ParsePoints();
            if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase) && points.Count > 0)
            {
                var pointsArray = new JsonArray();
                foreach (Simulator.Core.Map.Point2D point in points)
                {
                    pointsArray.Add(new JsonArray(point.X, point.Y));
                }

                node["points"] = pointsArray;
            }

            if (facility.AdditionalProperties is not null)
            {
                foreach ((string key, System.Text.Json.JsonElement value) in facility.AdditionalProperties)
                {
                    if (node.ContainsKey(key))
                    {
                        continue;
                    }

                    node[key] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
                }
            }

            facilities.Add(node);
        }

        map["facilities"] = facilities;
        _mapPresetService.SaveDocument(document.SourcePath, root);
    }
}
