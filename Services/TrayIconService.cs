using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace GlassMusicPlayer.Services;

/// <summary>
/// System tray icon. Runs on its own STA thread with a dedicated WinForms
/// message loop so the icon reliably receives clicks inside a WPF host.
/// The actual context menu is a WPF glass-styled popup (TrayMenuWindow)
/// shown by MainWindow when RightClicked fires.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private Thread? _thread;
    private NotifyIcon? _notifyIcon;
    private Control? _marshal;
    private volatile bool _disposed;

    public event Action? OpenWindow;
    public event Action? RightClicked;

    public TrayIconService()
    {
        _thread = new Thread(RunTrayThread)
        {
            IsBackground = true,
            Name = "GlassTrayThread"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunTrayThread()
    {
        try
        {
            // Tiny hidden control owning a handle on this thread, used later to
            // marshal SetTrackTitle/ShowBalloon/Dispose calls back onto it.
            _marshal = new Control { Visible = false };
            _marshal.CreateControl();

            _notifyIcon = new NotifyIcon
            {
                Icon = IconProvider.LoadTrayIcon(),
                Text = "Glass Music Player",
                Visible = true
            };
            _notifyIcon.DoubleClick += (_, _) => OpenWindow?.Invoke();
            _notifyIcon.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) RightClicked?.Invoke();
            };

            // Blocks until Application.ExitThread() is called from Dispose().
            Application.Run();
        }
        catch
        {
        }
        finally
        {
            try { _notifyIcon?.Dispose(); } catch { }
            _notifyIcon = null;
        }
    }

    public void SetTrackTitle(string title)
    {
        Post(() =>
        {
            if (_notifyIcon != null && !string.IsNullOrWhiteSpace(title))
                _notifyIcon.Text = title.Length > 60 ? title[..60] : title;
        });
    }

    public void ShowBalloon(string title, string text)
    {
        Post(() => _notifyIcon?.ShowBalloonTip(2000, title, text, ToolTipIcon.Info));
    }

    private void Post(Action action)
    {
        var c = _marshal;
        if (c == null || _disposed) return;
        try { c.BeginInvoke(action); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Post(() =>
        {
            try { if (_notifyIcon != null) _notifyIcon.Visible = false; } catch { }
            try { Application.ExitThread(); } catch { }
        });
        if (_thread != null && _thread.IsAlive)
        {
            try { _thread.Join(1500); } catch { }
        }
        try { _marshal?.Dispose(); } catch { }
        _notifyIcon = null;
    }
}