using System.Text.Json;
using Simulator.Core.Map;

namespace Simulator.ThreeD;

internal static class TerrainCacheActorComponentFilter
{
    private static readonly Dictionary<string, HashSet<int>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static HashSet<int> LoadExcludedComponentIds(MapPresetDefinition preset)
    {
        string annotationPath = preset.AnnotationPath;
        if (string.IsNullOrWhiteSpace(annotationPath) || !File.Exists(annotationPath))
        {
            return new HashSet<int>();
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(annotationPath, out HashSet<int>? cached))
            {
                return cached;
            }
        }

        var result = new HashSet<int>();
        using FileStream stream = File.OpenRead(annotationPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        if (document.RootElement.TryGetProperty("Composites", out JsonElement composites)
            && composites.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement composite in composites.EnumerateArray())
            {
                bool actorComposite = true;
                if (composite.TryGetProperty("Role", out JsonElement roleElement)
                    && roleElement.ValueKind == JsonValueKind.String)
                {
                    string role = roleElement.GetString() ?? string.Empty;
                    actorComposite = role.Equals("actor", StringComparison.OrdinalIgnoreCase)
                        || role.Equals("interactive", StringComparison.OrdinalIgnoreCase)
                        || role.Equals("dynamic", StringComparison.OrdinalIgnoreCase);
                }

                if (!actorComposite)
                {
                    continue;
                }

                // Dynamic arena composites must be rendered and animated by the
                // actor layer as a whole. If the static terrain stream keeps any
                // part of the same composite, the body and interaction units end
                // up on different transform chains and visually split apart.
                if (composite.TryGetProperty("ComponentIds", out JsonElement componentIds)
                    && componentIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in componentIds.EnumerateArray())
                    {
                        if (item.TryGetInt32(out int componentId))
                        {
                            result.Add(componentId);
                        }
                    }
                }

                if (!composite.TryGetProperty("InteractionUnits", out JsonElement interactionUnits)
                    || interactionUnits.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement unit in interactionUnits.EnumerateArray())
                {
                    if (!unit.TryGetProperty("ComponentIds", out JsonElement unitComponentIds)
                        || unitComponentIds.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement item in unitComponentIds.EnumerateArray())
                    {
                        if (item.TryGetInt32(out int componentId))
                        {
                            result.Add(componentId);
                        }
                    }
                }
            }
        }

        lock (Gate)
        {
            Cache[annotationPath] = result;
        }

        return result;
    }
}
