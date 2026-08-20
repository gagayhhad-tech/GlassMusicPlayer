using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassMusicPlayer.Models;
using GlassMusicPlayer.Services;

namespace GlassMusicPlayer;

public partial class MainWindow : Window
{
    private readonly AudioEngineService _audioEngine;
    private readonly IpcBridge _bridge;
    private GlobalHotkeys? _hotkeys;
    private TrayIconService? _tray;
    private TaskbarService? _taskbar;
    private bool _allowExit;
    private bool _autoScanned;

    public MainWindow()
    {
        InitializeComponent();
        Icon = IconProvider.LoadWindowIconSource();

        StateChanged += (_, _) => OnWindowStateChanged();
        SizeChanged += (_, _) => { UpdateWindowClip(RootBorder.CornerRadius.TopLeft); ApplyWindowRegion(); };
        SourceInitialized += (_, _) => ApplyWindowRegion();

        _audioEngine = new AudioEngineService();
            _bridge = new IpcBridge(_audioEngine, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            _bridge.SetMainWindow(this);

            Loaded += async (_, _) => await InitializeWebView();

        Closing += (_, e) =>
        {
            if (!_allowExit)
            {
                e.Cancel = true;
                Hide();
                _tray?.ShowBalloon("Glass Music Player", "Плеер свёрнут в трей. Нажмите дважды на иконку, чтобы открыть.");
                return;
            }
            _audioEngine.Dispose();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _taskbar?.Dispose();
        };
    }

    private async Task SendToEngine(string type, string payload = "")
    {
        var msg = new IpcMessage
        {
            Type = type,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload)
        };
        await _audioEngine.HandleIpcMessage(msg);
    }

    private void SetupGlobalControls()
    {
        try { InstallHotkeys(); } catch { }
        try { SetupTray(); } catch { }
        try { SetupTaskbar(); } catch { }

        _audioEngine.OnStateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _taskbar?.SetProgress(state.CurrentTime, state.Duration, state.IsPlaying);
                _taskbar?.SetThumbnailButtons(state.IsPlaying, state.CurrentTrack != null);
            }
            catch { }
        });
        _audioEngine.OnTrackChanged += track => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _tray?.SetTrackTitle($"{track.Artist} - {track.Title}");
            }
            catch { }
        });
    }

    private void InstallHotkeys()
    {
        _hotkeys = new GlobalHotkeys();
        _hotkeys.MediaPlayPause += () => _ = SendToEngine("playPause");
        _hotkeys.MediaNext += () => _ = SendToEngine("next");
        _hotkeys.MediaPrev += () => _ = SendToEngine("previous");
        _hotkeys.MediaStop += () => _ = SendToEngine("stop");
        _hotkeys.HotkeyPlayPause += () => _ = SendToEngine("playPause");
        _hotkeys.HotkeyNext += () => _ = SendToEngine("next");
        _hotkeys.HotkeyPrev += () => _ = SendToEngine("previous");
        _hotkeys.HotkeyMute += () => _ = SendToEngine("toggleMute");
        _hotkeys.HotkeyToggleShuffle += () => _ = SendToEngine("toggleShuffle");
        _hotkeys.HotkeyToggleLoop += () => _ = SendToEngine("toggleRepeat");
        _hotkeys.HotkeyVolumeUp += () => _ = SendToEngine("adjustVolume", "0.05");
        _hotkeys.HotkeyVolumeDown += () => _ = SendToEngine("adjustVolume", "-0.05");
        _hotkeys.Install();
    }

    private void SetupTray()
    {
        _tray = new TrayIconService();
        _tray.OpenWindow += () => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
        _tray.PlayPause += () => _ = SendToEngine("playPause");
        _tray.Next += () => _ = SendToEngine("next");
        _tray.Prev += () => _ = SendToEngine("previous");
        _tray.Exit += () => Dispatcher.Invoke(() => { _allowExit = true; Close(); });
        _tray.SetTrackTitle("Glass Music Player");
    }

    private void SetupTaskbar()
    {
        _taskbar = new TaskbarService();
        _taskbar.Attach(this);
        _taskbar.PlayPauseClicked += () => _ = SendToEngine("playPause");
        _taskbar.NextClicked += () => _ = SendToEngine("next");
        _taskbar.PrevClicked += () => _ = SendToEngine("previous");
        _taskbar.SetThumbnailButtons(false, false);
    }

    public void MinimizeWindow()
    {
        Dispatcher.Invoke(async () =>
        {
            if (WindowState == WindowState.Minimized) return;
            await FadeWindowAsync(true);
            WindowState = WindowState.Minimized;
            SetWindowAlpha(255);
        });
    }

    public void MaximizeWindow()
    {
        Dispatcher.Invoke(() =>
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        });
    }

    public void CloseWindow()
    {
        Dispatcher.Invoke(() => Close());
    }

    private const double WindowCornerRadius = 14d;
    private const int AnimSteps = 14;
    private const int AnimDelayMs = 14;
    private bool _wasMinimized;

    private async void OnWindowStateChanged()
    {
        var radius = WindowState == WindowState.Maximized ? 0d : WindowCornerRadius;
        RootBorder.CornerRadius = new CornerRadius(radius);
        UpdateWindowClip(radius);
        ApplyWindowRegion();

        if (_wasMinimized && WindowState == WindowState.Normal)
        {
            _wasMinimized = false;
            SetWindowAlpha(0);
            await FadeWindowAsync(false);
        }
        else if (WindowState == WindowState.Minimized)
        {
            _wasMinimized = true;
        }
    }

    private async Task FadeWindowAsync(bool fadeOut)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureLayered(hwnd);
        for (int i = 1; i <= AnimSteps; i++)
        {
            var alpha = fadeOut ? 255 - 255 * i / AnimSteps : 255 * i / AnimSteps;
            SetLayeredWindowAttributes(hwnd, 0, (byte)alpha, LWA_ALPHA);
            await Task.Delay(AnimDelayMs);
        }
        if (!fadeOut) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
    }

    private void SetWindowAlpha(byte alpha)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureLayered(hwnd);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    private static void EnsureLayered(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt32();
        if ((ex & (int)WS_EX_LAYERED) == 0)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | (int)WS_EX_LAYERED));
    }

    private void UpdateWindowClip(double radius)
    {
        if (RootBorder == null) return;
        var rect = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        RootBorder.Clip = new RectangleGeometry(rect, radius, radius);
    }

    private void ApplyWindowRegion()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (WindowState == WindowState.Maximized)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }
        var r = (int)Math.Round(WindowCornerRadius);
        var w = Math.Max(1, (int)ActualWidth);
        var h = Math.Max(1, (int)ActualHeight);
        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
        if (region == IntPtr.Zero) return;
        if (!SetWindowRgn(hwnd, region, true))
            DeleteObject(region);
    }

    // ===== Win32 helpers =====
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int wEllipse, int hEllipse);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    private void OnWebViewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWebViewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
        if (paths == null || paths.Length == 0) return;
        var msg = new IpcMessage
        {
            Type = "importFiles",
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(paths)
        };
        _ = _audioEngine.HandleIpcMessage(msg);
    }

    private async Task InitializeWebView()
    {
        await MusicPlayerWebView.EnsureCoreWebView2Async();
        MusicPlayerWebView.AllowExternalDrop = true;
        MusicPlayerWebView.AllowDrop = true;
        MusicPlayerWebView.DragOver += OnWebViewDragOver;
        MusicPlayerWebView.Drop += OnWebViewDrop;
        _bridge.SetWebView(MusicPlayerWebView.CoreWebView2);
        MusicPlayerWebView.CoreWebView2.Settings.IsScriptEnabled = true;
        MusicPlayerWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
        MusicPlayerWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
        MusicPlayerWebView.CoreWebView2.Settings.AreHostObjectsAllowed = true;
            MusicPlayerWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            
            // Map covers temp folder to virtual hostname so WebView2 can load local images
            var coversDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlassMusicPlayer", "covers");
            System.IO.Directory.CreateDirectory(coversDir);
            MusicPlayerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "covers.localhost", coversDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

        SetupGlobalControls();
        
        // Setup folder dialog handler
        _bridge.OnOpenFolderDialog = async () =>
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog();
                dialog.Description = "Выберите папку с музыкой";
                dialog.ShowNewFolderButton = false;
                
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    return dialog.SelectedPath;
                return null;
            }).Task;
        };
        
        var html = LoadHtml();
        MusicPlayerWebView.NavigationCompleted += (s, e) =>
        {
            // Auto-scan only after the page has fully loaded so the JS message
            // listener is registered and won't miss the libraryChanged push.
            if (_autoScanned) return;
            _autoScanned = true;
            _ = AutoScanLibraryAsync();
        };
        MusicPlayerWebView.NavigateToString(html);
    }

    private async Task AutoScanLibraryAsync()
    {
        // Wait for the page to load
        await Task.Delay(500);

        var scanMsg = new Models.IpcMessage
        {
            Type = "rescanAll",
            Payload = System.Text.Json.JsonSerializer.SerializeToElement("")
        };
        await _audioEngine.HandleIpcMessage(scanMsg);
    }

    private static string LoadHtml()
    {
        var basePath = System.AppContext.BaseDirectory;
        var filePath = System.IO.Path.Combine(basePath, "wwwroot", "index.html");
        
        if (System.IO.File.Exists(filePath))
            return System.IO.File.ReadAllText(filePath);
        
        // Fallback: check alongside the project directory (for development)
        var projectPath = System.IO.Path.Combine(
            basePath, "..", "..", "..", "wwwroot", "index.html");
        
        if (System.IO.File.Exists(projectPath))
            return System.IO.File.ReadAllText(System.IO.Path.GetFullPath(projectPath));
        
        return "<html><body><h2>Error: UI file not found</h2></body></html>";
    }
}