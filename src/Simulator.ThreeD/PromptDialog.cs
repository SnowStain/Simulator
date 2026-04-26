using System.Windows.Forms;

namespace Simulator.ThreeD;

internal static class PromptDialog
{
    public static string? Show(string prompt, string title)
    {
        using Form form = new()
        {
            Width = 460,
            Height = 160,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        Label label = new()
        {
            Left = 12,
            Top = 12,
            Width = 420,
            Height = 22,
            Text = prompt,
        };

        TextBox textBox = new()
        {
            Left = 12,
            Top = 40,
            Width = 420,
        };

        Button ok = new()
        {
            Text = "OK",
            Left = 268,
            Width = 78,
            Top = 74,
            DialogResult = DialogResult.OK,
        };

        Button cancel = new()
        {
            Text = "Cancel",
            Left = 354,
            Width = 78,
            Top = 74,
            DialogResult = DialogResult.Cancel,
        };

        form.Controls.Add(label);
        form.Controls.Add(textBox);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }
}
