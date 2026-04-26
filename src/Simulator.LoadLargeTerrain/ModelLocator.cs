namespace LoadLargeTerrain;

internal static class ModelLocator
{
    public static string ResolveModelPath(string[] args, string defaultFileName)
    {
        string? explicitPath = ResolveExplicitModelPath(args);
        if (explicitPath is not null && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        IEnumerable<string> probeDirectories = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        }
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in probeDirectories)
        {
            string directCandidate = Path.Combine(directory, defaultFileName);
            if (File.Exists(directCandidate))
            {
                return Path.GetFullPath(directCandidate);
            }

            foreach (string relativePath in EnumerateCommonModelRelativePaths(defaultFileName))
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, relativePath));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"找不到模型文件 \"{defaultFileName}\"。请把 .glb 路径作为第一个命令行参数传入。");
    }

    private static IEnumerable<string> EnumerateCommonModelRelativePaths(string defaultFileName)
    {
        yield return Path.Combine("maps", "rmuc26map", defaultFileName);
        yield return Path.Combine("maps", "rmuc2026", defaultFileName);
        yield return Path.Combine("maps", "rmuc2026TerrainCache", defaultFileName);
    }

    private static string? ResolveExplicitModelPath(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (string.Equals(arg, "--annotations", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (string.Equals(arg, "--map-preset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--map-json", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (arg.StartsWith("--annotations=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--map-preset=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--map-json=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            return arg;
        }

        return null;
    }
}
