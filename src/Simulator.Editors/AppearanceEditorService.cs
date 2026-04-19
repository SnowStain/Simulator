using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simulator.Core;

namespace Simulator.Editors;

public sealed class AppearanceEditorService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonObject LoadLatestAppearance(ProjectLayout layout)
    {
        string path = layout.AppearancePresetPath;
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        return node as JsonObject ?? new JsonObject();
    }

    public void SaveLatestAppearance(ProjectLayout layout, JsonObject json)
    {
        string path = layout.AppearancePresetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? layout.RootPath);
        File.WriteAllText(path, json.ToJsonString(JsonOptions));
    }

    public JsonObject SetTopLevelValue(ProjectLayout layout, string key, string jsonLiteral)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Top-level key cannot be empty.", nameof(key));
        }

        JsonObject appearance = LoadLatestAppearance(layout);
        JsonNode? valueNode;
        try
        {
            valueNode = JsonNode.Parse(jsonLiteral);
        }
        catch (JsonException)
        {
            valueNode = JsonValue.Create(jsonLiteral);
        }

        appearance[key] = valueNode;
        SaveLatestAppearance(layout, appearance);
        return appearance;
    }
}
