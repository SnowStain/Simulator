using System.Text.Json;
using System.Text.Json.Nodes;
using Simulator.Core;

namespace Simulator.Editors;

public sealed class RuleEditorService
{
    private readonly ConfigurationService _configurationService;

    public RuleEditorService(ConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public JsonObject LoadRules(ProjectLayout layout)
    {
        string configPath = _configurationService.ResolvePrimaryConfigPath(layout);
        JsonObject config = _configurationService.LoadConfig(configPath);
        return ConfigurationService.EnsureObject(config, "rules");
    }

    public IReadOnlyList<string> SetRuleValue(ProjectLayout layout, string dottedPath, string jsonLiteral)
    {
        if (string.IsNullOrWhiteSpace(dottedPath))
        {
            throw new ArgumentException("Rule path cannot be empty.", nameof(dottedPath));
        }

        IReadOnlyList<string> pathSegments = BuildRulePathSegments(dottedPath);
        JsonNode? valueNode = ParseJsonLiteral(jsonLiteral);

        var written = new List<string>();
        foreach (string configPath in _configurationService.ExistingConfigPaths(layout))
        {
            JsonObject config = _configurationService.LoadConfig(configPath);
            _configurationService.SetValueByPath(config, pathSegments, valueNode?.DeepClone());
            _configurationService.SaveConfig(configPath, config);
            written.Add(Path.GetRelativePath(layout.RootPath, configPath));
        }

        if (written.Count == 0)
        {
            string fallbackPath = layout.CommonSettingPath;
            JsonObject config = _configurationService.LoadConfig(fallbackPath);
            _configurationService.SetValueByPath(config, pathSegments, valueNode);
            _configurationService.SaveConfig(fallbackPath, config);
            written.Add(Path.GetRelativePath(layout.RootPath, fallbackPath));
        }

        return written;
    }

    private static IReadOnlyList<string> BuildRulePathSegments(string dottedPath)
    {
        string[] rawSegments = dottedPath
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            throw new ArgumentException("Rule path is invalid.", nameof(dottedPath));
        }

        if (!string.Equals(rawSegments[0], "rules", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "rules" }.Concat(rawSegments).ToArray();
        }

        return rawSegments;
    }

    private static JsonNode? ParseJsonLiteral(string jsonLiteral)
    {
        try
        {
            return JsonNode.Parse(jsonLiteral);
        }
        catch (JsonException)
        {
            return JsonValue.Create(jsonLiteral);
        }
    }
}
