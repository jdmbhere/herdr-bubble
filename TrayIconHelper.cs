using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HerdrBubble;

internal sealed class TrayIconHelper : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly bool _ownsTrayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startupItem;
    private bool _disposed;

    public TrayIconHelper(
        Action onShowBubble,
        Action onResetPosition,
        Action onLaunchTerminal,
        Action onExit)
    {
        (_trayIcon, _ownsTrayIcon) = LoadAppIcon();

        _startupItem = new ToolStripMenuItem("Start with Windows");
        _startupItem.Click += (_, _) =>
        {
            bool next = !StartupHelper.IsStartupEnabled();
            StartupHelper.SetStartupEnabled(next);
            _startupItem.Checked = StartupHelper.IsStartupEnabled();
        };

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => _startupItem.Checked = StartupHelper.IsStartupEnabled();
        _menu.Items.Add("Show Herdr Bubble", null, (_, _) => onShowBubble());
        _menu.Items.Add("Reset Position (Primary Screen)", null, (_, _) => onResetPosition());
        _menu.Items.Add("Launch New Herdr Terminal", null, (_, _) => onLaunchTerminal());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => onExit());

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Herdr Bubble (Ctrl+Alt+H)",
            ContextMenuStrip = _menu,
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                onShowBubble();
            }
        };
    }

    private static (Icon icon, bool ownsIcon) LoadAppIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null)
                {
                    return (extracted, true);
                }
            }
        }
        catch
        {
            // Fall back to system icon below.
        }

        return (SystemIcons.Application, false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        if (_ownsTrayIcon)
        {
            _trayIcon.Dispose();
        }
    }
}
