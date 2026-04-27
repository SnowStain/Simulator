using System.ComponentModel;
using System.Windows.Forms;

namespace Simulator.ThreeD;

internal sealed class EmbeddedSimulatorPreviewHost : UserControl
{
    private readonly Panel _viewportPanel = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 180 };
    private Simulator3dForm? _previewForm;
    private Func<Simulator3dOptions?>? _optionsFactory;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Simulator3dOptions?>? OptionsFactory
    {
        get => _optionsFactory;
        set => _optionsFactory = value;
    }

    [DefaultValue("GPU preview is waiting for data.")]
    public string EmptyText { get; set; } = "GPU preview is waiting for data.";

    public EmbeddedSimulatorPreviewHost()
    {
        BackColor = Color.FromArgb(16, 20, 28);

        _viewportPanel.Dock = DockStyle.Fill;
        _viewportPanel.BackColor = BackColor;

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 24;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(8, 0, 0, 0);
        _statusLabel.ForeColor = Color.FromArgb(186, 198, 214);
        _statusLabel.BackColor = Color.FromArgb(24, 30, 40);
        _statusLabel.Text = EmptyText;

        Controls.Add(_viewportPanel);
        Controls.Add(_statusLabel);

        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            ReloadNow();
        };
    }

    public void QueueReload()
    {
        if (IsDisposed)
        {
            return;
        }

        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    public void ReloadNow()
    {
        if (IsDisposed)
        {
            return;
        }

        _reloadTimer.Stop();
        CloseCurrentPreview();

        if (!IsHandleCreated || !Visible)
        {
            _statusLabel.Text = EmptyText;
            return;
        }

        Simulator3dOptions? options;
        try
        {
            options = _optionsFactory?.Invoke();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"GPU preview setup failed: {ex.Message}";
            return;
        }

        if (options is null)
        {
            _statusLabel.Text = EmptyText;
            return;
        }

        try
        {
            var preview = new Simulator3dForm(options)
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill,
                ShowInTaskbar = false,
            };
            _viewportPanel.Controls.Clear();
            _viewportPanel.Controls.Add(preview);
            _previewForm = preview;
            preview.Show();
            _statusLabel.Text = "GPU preview follows the runtime renderer.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"GPU preview failed: {ex.Message}";
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BeginInvoke((MethodInvoker)QueueReload);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            QueueReload();
        }
        else
        {
            CloseCurrentPreview();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reloadTimer.Stop();
            _reloadTimer.Dispose();
            CloseCurrentPreview();
        }

        base.Dispose(disposing);
    }

    private void CloseCurrentPreview()
    {
        if (_previewForm is null)
        {
            _viewportPanel.Controls.Clear();
            return;
        }

        try
        {
            _viewportPanel.Controls.Remove(_previewForm);
            _previewForm.Close();
            _previewForm.Dispose();
        }
        catch
        {
        }
        finally
        {
            _previewForm = null;
            _viewportPanel.Controls.Clear();
        }
    }
}
