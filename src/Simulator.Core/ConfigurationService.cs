using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Simulator.Core;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public IReadOnlyList<string> ExistingConfigPaths(ProjectLayout layout)
    {
        string[] ordered =
        {
            layout.CommonSettingPath,
            layout.ConfigPath,
            layout.SettingsPath,
        };

        return ordered.Where(File.Exists).ToArray();
    }

    public string ResolvePrimaryConfigPath(ProjectLayout layout)
    {
        IReadOnlyList<string> candidates = ExistingConfigPaths(layout);
        if (candidates.Count > 0)
        {
            return candidates[0];
        }

        return layout.CommonSettingPath;
    }

    public JsonObject LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new JsonObject();
        }

        string json = File.ReadAllText(configPath);
        JsonNode? node = JsonNode.Parse(json);
        return node as JsonObject ?? new JsonObject();
    }

    public void SaveConfig(string configPath, JsonObject config)
    {
        string parent = Path.GetDirectoryName(configPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(configPath, config.ToJsonString(JsonOptions));
    }

    public string GetMapPreset(JsonObject config)
    {
        JsonObject map = EnsureObject(config, "map");
        return map["preset"]?.GetValue<string>() ?? "basicMap";
    }

    public void SetMapPreset(JsonObject config, string presetName)
    {
        JsonObject map = EnsureObject(config, "map");
        JsonObject simulator = EnsureObject(config, "simulator");
        map["preset"] = presetName;
        simulator["sim3d_map_preset"] = presetName;
    }

    public static JsonObject EnsureObject(JsonObject root, string key)
    {
        JsonNode? existing = root[key];
        if (existing is JsonObject objectNode)
        {
            return objectNode;
        }

        JsonObject created = new();
        root[key] = created;
        return created;
    }
}
