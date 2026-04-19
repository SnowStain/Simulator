using System;
using System.IO;

namespace Simulator.Core;

public sealed class ProjectLayout
{
    public string RootPath { get; }

    public string SourceRoot => Path.Combine(RootPath, "src");
    public string CommonSettingPath => Path.Combine(RootPath, "CommonSetting.json");
    public string ConfigPath => Path.Combine(RootPath, "config.json");
    public string SettingsPath => Path.Combine(RootPath, "settings.json");
    public string AppearancePresetPath => Path.Combine(RootPath, "appearance_presets", "latest_appearance.json");

    public ProjectLayout(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    public string ResolvePath(params string[] parts)
    {
        if (parts is null || parts.Length == 0)
        {
            return RootPath;
        }

        string path = RootPath;
        foreach (string part in parts)
        {
            path = Path.Combine(path, part);
        }
        return path;
    }

    public static ProjectLayout Discover(string? startPath = null)
    {
        string current = Path.GetFullPath(startPath ?? Directory.GetCurrentDirectory());

        if (File.Exists(Path.Combine(current, "Simulator.sln")))
        {
            return new ProjectLayout(current);
        }

        DirectoryInfo? cursor = new DirectoryInfo(current);
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "Simulator.sln")))
            {
                return new ProjectLayout(cursor.FullName);
            }
            cursor = cursor.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate Simulator.sln starting from '{current}'. Please run inside the migrated Simulator project.");
    }
}
