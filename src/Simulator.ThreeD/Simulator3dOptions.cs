using System.Globalization;

namespace Simulator.ThreeD;

internal sealed class Simulator3dOptions
{
    private static readonly Dictionary<string, string> RendererModeByBackend = new(StringComparer.OrdinalIgnoreCase)
    {
        ["editor_opengl"] = "opengl",
        ["opengl_grid"] = "opengl",
        ["terrain_editor_opengl"] = "opengl",
        ["terrain_editor_gl"] = "opengl",
        ["moderngl"] = "moderngl",
        ["pyglet_moderngl"] = "moderngl",
        ["pyglet-moderngl"] = "moderngl",
        ["gpu"] = "gpu",
        ["opengl_gpu"] = "gpu",
        ["wgl"] = "gpu",
        ["wgl_opengl"] = "gpu",
        ["native_cpp"] = "native_cpp",
        ["cpp"] = "native_cpp",
        ["opengl_cpp"] = "native_cpp",
        ["rm26_native"] = "native_cpp",
    };

    private static readonly string[] OrderedSingleUnitEntityKeys =
    {
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_7",
    };

    private static readonly HashSet<string> SingleUnitEntityKeys =
        new(OrderedSingleUnitEntityKeys, StringComparer.OrdinalIgnoreCase);

    public string? MapPreset { get; init; }

    public string? RendererMode { get; init; }

    public string? MatchMode { get; init; }

    public double DeltaTimeSec { get; init; } = 0.016;

    public string? SelectedTeam { get; init; }

    public string? SelectedEntityId { get; init; }

    public string? SingleUnitTestTeam { get; init; }

    public string? SingleUnitTestEntityKey { get; init; }

    public bool? RicochetEnabled { get; init; }

    public bool StartInMatch { get; init; }

    public string? OpenEditor { get; init; }

    public string? AppearancePath { get; init; }

    public bool PreviewOnly { get; init; }

    public string? PreviewStructure { get; init; }

    public string? PreviewTeam { get; init; }

    public static IReadOnlyList<string> SingleUnitEntityKeyOrder => OrderedSingleUnitEntityKeys;

    public static Simulator3dOptions Parse(string[] args)
    {
        string? mapPreset = null;
        string? rendererMode = null;
        string? matchMode = null;
        double dt = 0.016;
        string? selectedTeam = null;
        string? selectedEntityId = null;
        string? singleUnitTestTeam = null;
        string? singleUnitTestEntityKey = null;
        bool? ricochetEnabled = null;
        bool startInMatch = false;
        string? openEditor = null;
        string? appearancePath = null;
        bool previewOnly = false;
        string? previewStructure = null;
        string? previewTeam = null;

        for (int index = 0; index < args.Length; index++)
        {
            string current = args[index];
            if ((current.Equals("--map", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--preset", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                mapPreset = args[++index];
                continue;
            }

            if ((current.Equals("--backend", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--renderer", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                rendererMode = NormalizeRendererMode(args[++index]);
                continue;
            }

            if ((current.Equals("--match-mode", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--mode", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                matchMode = NormalizeMatchMode(args[++index]);
                continue;
            }

            if ((current.Equals("--dt", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--delta", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                string raw = args[++index];
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    dt = parsed;
                }

                continue;
            }

            if (current.Equals("--team", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                selectedTeam = NormalizeTeam(args[++index]);
                continue;
            }

            if (current.Equals("--entity", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                selectedEntityId = args[++index].Trim();
                continue;
            }

            if ((current.Equals("--focus-team", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--test-team", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                singleUnitTestTeam = NormalizeTeam(args[++index]);
                continue;
            }

            if ((current.Equals("--focus-entity", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--focus-role", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--test-entity", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                singleUnitTestEntityKey = NormalizeSingleUnitEntityKey(args[++index]);
                continue;
            }

            if (current.Equals("--ricochet", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                ricochetEnabled = ParseBoolean(args[++index]);
                continue;
            }

            if (current.Equals("--start-match", StringComparison.OrdinalIgnoreCase)
                || current.Equals("--auto-start", StringComparison.OrdinalIgnoreCase))
            {
                startInMatch = true;
                continue;
            }

            if ((current.Equals("--open-editor", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--editor", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                openEditor = NormalizeEditorName(args[++index]);
                continue;
            }

            if ((current.Equals("--appearance-path", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--appearance", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                appearancePath = args[++index];
                continue;
            }

            if (current.Equals("--preview-only", StringComparison.OrdinalIgnoreCase))
            {
                previewOnly = true;
                continue;
            }

            if ((current.Equals("--preview-structure", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--structure-preview", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                previewStructure = NormalizePreviewStructure(args[++index]);
                continue;
            }

            if ((current.Equals("--preview-team", StringComparison.OrdinalIgnoreCase)
                    || current.Equals("--structure-team", StringComparison.OrdinalIgnoreCase))
                && index + 1 < args.Length)
            {
                previewTeam = NormalizePreviewTeam(args[++index]);
                continue;
            }
        }

        return new Simulator3dOptions
        {
            MapPreset = string.IsNullOrWhiteSpace(mapPreset) ? null : mapPreset,
            RendererMode = string.IsNullOrWhiteSpace(rendererMode) ? null : NormalizeRendererMode(rendererMode),
            MatchMode = string.IsNullOrWhiteSpace(matchMode) ? null : NormalizeMatchMode(matchMode),
            DeltaTimeSec = Math.Clamp(dt, 0.008, 0.25),
            SelectedTeam = selectedTeam,
            SelectedEntityId = string.IsNullOrWhiteSpace(selectedEntityId) ? null : selectedEntityId,
            SingleUnitTestTeam = singleUnitTestTeam,
            SingleUnitTestEntityKey = singleUnitTestEntityKey,
            RicochetEnabled = ricochetEnabled,
            StartInMatch = startInMatch,
            OpenEditor = openEditor,
            AppearancePath = string.IsNullOrWhiteSpace(appearancePath) ? null : appearancePath,
            PreviewOnly = previewOnly,
            PreviewStructure = previewStructure,
            PreviewTeam = previewTeam,
        };
    }

    public static string NormalizeRendererMode(string? selected)
    {
        string mode = (selected ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is "opengl" or "moderngl" or "native_cpp" or "gpu")
        {
            return mode;
        }

        if (RendererModeByBackend.TryGetValue(mode, out string? normalized))
        {
            return normalized;
        }

        return "gpu";
    }

    public static string NormalizeMatchMode(string? selected)
    {
        string mode = (selected ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is "single" or "single_unit_test" or "single-unit-test" or "singletest")
        {
            return "single_unit_test";
        }

        return "full";
    }

    public static string NormalizeTeam(string? selected)
    {
        string team = (selected ?? string.Empty).Trim().ToLowerInvariant();
        return team == "blue" ? "blue" : "red";
    }

    public static string NormalizeSingleUnitEntityKey(string? selected)
    {
        string key = (selected ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
        {
            return "robot_1";
        }

        key = key switch
        {
            "hero" => "robot_1",
            "engineer" => "robot_2",
            "infantry" => "robot_3",
            "infantry_1" => "robot_3",
            "infantry_2" => "robot_4",
            "sentry" => "robot_7",
            _ => key,
        };

        return SingleUnitEntityKeys.Contains(key) ? key : "robot_1";
    }

    private static bool? ParseBoolean(string? raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value is "1" or "true" or "yes" or "on")
        {
            return true;
        }

        if (value is "0" or "false" or "no" or "off")
        {
            return false;
        }

        return null;
    }

    public static string? NormalizeEditorName(string? raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value switch
        {
            "appearance" or "appearance_editor" => "appearance",
            "terrain" or "terrain_editor" => "terrain",
            "rules" or "rule" or "rule_editor" => "rules",
            "behavior" or "behaviour" or "behavior_editor" => "behavior",
            "functional" or "functional_editor" => "functional",
            _ => null,
        };
    }

    public static string? NormalizePreviewStructure(string? raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value switch
        {
            "base" => "base",
            "outpost" => "outpost",
            "energy" or "energy_mechanism" or "mechanism" => "energy_mechanism",
            _ => null,
        };
    }

    public static string? NormalizePreviewTeam(string? raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value switch
        {
            "red" => "red",
            "blue" => "blue",
            "neutral" => "neutral",
            _ => null,
        };
    }
}
