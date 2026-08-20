using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using GlassMusicPlayer.Models;
using Microsoft.Web.WebView2.Core;

namespace GlassMusicPlayer.Services;

public class IpcBridge
{
    private readonly AudioEngineService _audioEngine;
    private readonly Dispatcher _dispatcher;
    private CoreWebView2? _webView;
    private MainWindow? _mainWindow;

    public Func<Task<string?>>? OnOpenFolderDialog { get; set; }

    public void SetMainWindow(MainWindow window)
    {
        _mainWindow = window;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public IpcBridge(AudioEngineService audioEngine, Dispatcher dispatcher)
    {
        _audioEngine = audioEngine;
        _dispatcher = dispatcher;
        _audioEngine.OnStateChanged += state => PostMessage("stateChanged", state);
        _audioEngine.OnTrackChanged += track => PostMessage("trackChanged", track);
        _audioEngine.OnVisualization += data => PostMessage("visualization", data);
        _audioEngine.OnLibraryChanged += tracks => PostMessage("libraryChanged", tracks);
        _audioEngine.OnPlaylistsChanged += playlists => PostMessage("playlistsChanged", playlists);
        _audioEngine.OnScanStatus += status => PostMessage("scanStatus", status);
        _audioEngine.OnFavoritesChanged += favs => PostMessage("favoritesChanged", favs.ToList());

        // Hook the folder dialog callback
        _audioEngine.OnOpenFolderDialogRequest += async () =>
        {
            if (OnOpenFolderDialog != null)
            {
                var path = await OnOpenFolderDialog();
                return path ?? "";
            }
            return "";
        };
    }

    public void SetWebView(CoreWebView2 webView)
    {
        _webView = webView;
        _webView.WebMessageReceived += OnWebMessageReceived;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            var msg = JsonSerializer.Deserialize<IpcMessage>(raw, JsonOptions);
            if (msg == null) return;

            // Handle window control messages directly (no need to go through audio engine)
            switch (msg.Type)
            {
                case "minimizeWindow":
                    _mainWindow?.MinimizeWindow();
                    return;
                case "maximizeWindow":
                    _mainWindow?.MaximizeWindow();
                    return;
                case "closeWindow":
                    _mainWindow?.CloseWindow();
                    return;
                case "getWindowState":
                    if (_mainWindow != null)
                    {
                        PostMessage("windowState", new { isMaximized = _mainWindow.WindowState == WindowState.Maximized });
                    }
                    return;
            }

            var result = await _audioEngine.HandleIpcMessage(msg);

            // For getEqualizerPresets, respond with the original type so JS can handle it
            string responseType = msg.Type switch
            {
                "getEqualizerPresets" => "getEqualizerPresets",
                "setEqualizerPreset" => "setEqualizerPreset",
                "toggleEqualizer" => "toggleEqualizer",
                "setEqualizerGains" => "setEqualizerGains",
                _ => "ipcResponse"
            };

            // Try to parse result as JSON object, fall back to raw string
            try
            {
                var resultObj = JsonSerializer.Deserialize<object>(result);
                PostMessage(responseType, resultObj);
            }
            catch
            {
                PostMessage(responseType, result);
            }
        }
        catch (Exception ex)
        {
            PostMessage("ipcError", new { error = ex.Message });
        }
    }

    private void PostMessage(string type, object? data)
    {
        if (_webView == null) return;

        // Must execute on UI thread for WebView2. Use BeginInvoke so background threads
        // (audio timers) never block waiting on the UI thread.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => PostMessage(type, data));
            return;
        }

        try
        {
            var msg = new IpcMessage
            {
                Type = type,
                Payload = JsonSerializer.SerializeToElement(data, JsonOptions)
            };

            var json = JsonSerializer.Serialize(msg, JsonOptions);
            
            try
            {
                _webView.PostWebMessageAsJson(json);
            }
            catch
            {
                // WebView might be navigating or not ready
            }
        }
        catch
        {
            // Silently fail if serialization fails
        }
    }
}