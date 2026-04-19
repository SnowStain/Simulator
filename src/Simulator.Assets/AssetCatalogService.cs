using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simulator.Core;

namespace Simulator.Assets;

public sealed record AssetCategoryStatus(string Name, string RelativePath, bool Exists, int FileCount);

public sealed record AssetCatalog(IReadOnlyList<AssetCategoryStatus> Categories)
{
    public bool IsComplete => Categories.All(item => item.Exists && item.FileCount > 0);

    public int TotalFileCount => Categories.Sum(item => item.FileCount);
}

public sealed class AssetCatalogService
{
    private static readonly string[] RequiredFolders =
    {
        "appearance_presets",
        "map",
        "map_presets",
        "maps",
        "robot_venue_map_asset",
        "rules",
        "规则",
        "simulator3d",
        "Engine",
        "cpp",
    };

    public AssetCatalog BuildCatalog(ProjectLayout layout)
    {
        var rows = new List<AssetCategoryStatus>(RequiredFolders.Length);
        foreach (string folder in RequiredFolders)
        {
            string absolute = layout.ResolvePath(folder);
            bool exists = Directory.Exists(absolute);
            int count = exists
                ? Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories).Count()
                : 0;
            rows.Add(new AssetCategoryStatus(folder, folder, exists, count));
        }

        return new AssetCatalog(rows);
    }

    public IReadOnlyList<string> ListMapPresets(ProjectLayout layout)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string mapPresetsPath = layout.ResolvePath("map_presets");
        if (Directory.Exists(mapPresetsPath))
        {
            foreach (string file in Directory.EnumerateFiles(mapPresetsPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        string mapsPath = layout.ResolvePath("maps");
        if (Directory.Exists(mapsPath))
        {
            foreach (string dir in Directory.EnumerateDirectories(mapsPath))
            {
                names.Add(Path.GetFileName(dir));
            }
        }

        return names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> ListRuleFiles(ProjectLayout layout)
    {
        var files = new List<string>();
        foreach (string folder in new[] { "rules", "规则" })
        {
            string absolute = layout.ResolvePath(folder);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(layout.RootPath, path));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
