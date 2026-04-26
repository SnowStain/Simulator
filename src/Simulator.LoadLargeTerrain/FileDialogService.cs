using System.Windows.Forms;

namespace LoadLargeTerrain;

internal static class FileDialogService
{
    public static string? PickJsonFile(string? initialPath)
    {
        return RunInStaThread(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择要读取的 JSON 文件",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            var directory = ResolveInitialDirectory(initialPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                dialog.InitialDirectory = directory;
            }

            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                dialog.FileName = Path.GetFileName(initialPath);
            }

            return dialog.ShowDialog() == DialogResult.OK
                ? dialog.FileName
                : null;
        });
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

    private static T? RunInStaThread<T>(Func<T?> action)
    {
        T? result = default;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            throw error;
        }

        return result;
    }
}
