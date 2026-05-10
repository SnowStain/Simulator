using System.Diagnostics;

namespace LoadLargeTerrain;

internal static class FileDialogService
{
    public static string? PickJsonFile(string? initialPath)
    {
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
        {
            return Path.GetFullPath(initialPath);
        }

        string? nativeSelection = TryPickJsonFileWithNativeDialog(initialPath);
        if (!string.IsNullOrWhiteSpace(nativeSelection) && File.Exists(nativeSelection))
        {
            return Path.GetFullPath(nativeSelection);
        }

        return FindJsonCandidates(initialPath, maxCount: 1).FirstOrDefault();
    }

    public static IReadOnlyList<string> FindJsonCandidates(string? initialPath, int maxCount = 8)
    {
        string? directory = ResolveInitialDirectory(initialPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxCount))
                .Select(Path.GetFullPath)
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolveInitialDirectory(string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath))
        {
            return null;
        }

        if (Directory.Exists(initialPath))
        {
            return initialPath;
        }

        var directory = Path.GetDirectoryName(initialPath);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : null;
    }

    private static string? TryPickJsonFileWithNativeDialog(string? initialPath)
    {
        string directory = ResolveInitialDirectory(initialPath) ?? Directory.GetCurrentDirectory();
        if (OperatingSystem.IsLinux())
        {
            string? selected = TryRunDialogTool("zenity", ["--file-selection", "--title=选择要读取的 JSON 文件", "--file-filter=JSON files | *.json"], directory);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            return TryRunDialogTool("kdialog", ["--getopenfilename", directory, "*.json"], directory);
        }

        if (OperatingSystem.IsMacOS())
        {
            string script = "POSIX path of (choose file of type {\"json\"} with prompt \"选择要读取的 JSON 文件\")";
            return TryRunDialogTool("osascript", ["-e", script], directory);
        }

        return null;
    }

    private static string? TryRunDialogTool(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (!IsExecutableOnPath(executable))
        {
            return null;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30_000);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? output
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsExecutableOnPath(string executable)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
