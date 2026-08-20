using System;
using System.Threading;
using DiscordRPC;

namespace GlassMusicPlayer.Services;

/// <summary>
/// Discord Rich Presence integration. Shows the currently playing track
/// (title/artist) and playback state in the user's Discord profile.
/// </summary>
public class DiscordPresenceService : IDisposable
{
    // Application ID for the "Glass Music Player" Discord application
    // (https://discord.com/developers/applications).
    // An image asset named "glass_logo" is used for the large icon.
    private const string ApplicationId = "1539987361708900445";

    private DiscordRpcClient? _client;
    private bool _enabled;
    private readonly object _lock = new();
    private System.Threading.Timer? _reconnectTimer;
    private string? _lastTitle;
    private string? _lastArtist;
    private bool _lastPlaying;
    private double _lastPosition;
    private DateTime _lastPushUtc;
    private bool _everConnected;

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;
            _lastTitle = null;
            _lastArtist = null;
            _lastPlaying = false;
            _lastPosition = 0;
            _lastPushUtc = DateTime.UtcNow;

            if (enabled && _client == null)
            {
                CreateClient();
                if (_reconnectTimer == null)
                {
                    _reconnectTimer = new System.Threading.Timer(_ => TryReconnect(), null, 5000, 5000);
                }
            }
            else if (!enabled)
            {
                _reconnectTimer?.Dispose();
                _reconnectTimer = null;
                DisposeClient();
            }
        }
    }

    private void CreateClient()
    {
        try
        {
            var client = new DiscordRpcClient(ApplicationId);
            client.OnReady += (_, e) =>
            {
                _everConnected = true;
                AudioEngineService.Log("DISCORD", $"connected as {e.User?.Username}");
                // The pipe may have become ready after presence was pushed once and
                // dropped, so re-push the last known state now.
                lock (_lock)
                {
                    if (_enabled && _client != null)
                    {
                        PushPresence(_lastTitle, _lastArtist, _lastPlaying, _lastPosition, _client);
                    }
                }
            };
            client.OnConnectionFailed += (_, _) =>
                AudioEngineService.Log("DISCORD", "connection failed (Discord not running?)");
            client.OnError += (_, e) =>
                AudioEngineService.Log("DISCORD", "error " + e.Message);
            client.OnClose += (_, _) =>
                AudioEngineService.Log("DISCORD", "pipe closed");
            client.Initialize();
            _client = client;
        }
        catch (Exception ex)
        {
            AudioEngineService.Log("DISCORD", "init exception " + ex.Message);
            _client = null;
        }
    }

    private void TryReconnect()
    {
        lock (_lock)
        {
            if (!_enabled) return;
            if (_client == null)
            {
                AudioEngineService.Log("DISCORD", "retrying connection...");
                CreateClient();
                return;
            }
            if (!_everConnected)
            {
                AudioEngineService.Log("DISCORD", "no connection yet, recreating client...");
                DisposeClient();
                CreateClient();
            }
        }
    }

    private void DisposeClient()
    {
        try { _client?.Deinitialize(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _everConnected = false;
    }

    /// <summary>
    /// Pushes the current playback state to Discord. No-op when nothing changed
    /// (the state timer fires every ~250ms, so we only send on actual changes).
    /// </summary>
    public void UpdatePresence(string? title, string? artist, bool isPlaying, double positionSeconds)
    {
        lock (_lock)
        {
            if (!_enabled || _client == null) return;

            title = Truncate(title, 128);
            artist = Truncate(artist, 128);

            var changed = _lastTitle != title || _lastArtist != artist || _lastPlaying != isPlaying;
            if (!changed)
            {
                // If we pushed at _lastPosition and the track kept playing,
                // Discord's own clock should now show _lastPosition + elapsed.
                var expected = _lastPosition + (isPlaying ? (DateTime.UtcNow - _lastPushUtc).TotalSeconds : 0);
                // Seek, crossfade, or a track that started slightly earlier cause a
                // deviation here -> resend so the timestamp is exact again.
                if (Math.Abs(positionSeconds - expected) <= 3.0) return;
            }

            _lastTitle = title;
            _lastArtist = artist;
            _lastPlaying = isPlaying;
            _lastPosition = positionSeconds;
            _lastPushUtc = DateTime.UtcNow;

            PushPresence(title, artist, isPlaying, positionSeconds, _client);
        }
    }

    private static void PushPresence(string? title, string? artist, bool isPlaying, double positionSeconds, DiscordRpcClient client)
    {
        try
        {
            if (string.IsNullOrEmpty(title))
            {
                client.ClearPresence();
                client.Invoke();
                return;
            }

            var presence = new RichPresence
            {
                Details = title,
                State = string.IsNullOrEmpty(artist) ? "Glass Music Player" : artist,
                Assets = new Assets
                {
                    LargeImageKey = "glass_logo",
                    LargeImageText = "Glass Music Player"
                }
            };

            if (isPlaying)
            {
                // Start time = now minus current position, so Discord's elapsed
                // counter matches the real playback position.
                presence.Timestamps = new Timestamps(DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, positionSeconds)));
            }
            else
            {
                presence.Timestamps = null;
            }

            client.SetPresence(presence);
            client.Invoke();
            AudioEngineService.Log("DISCORD", "pushed: " + (isPlaying ? "playing" : "paused") + " \"" + title + "\" - " + artist);
        }
        catch (Exception ex)
        {
            AudioEngineService.Log("DISCORD", "push exception " + ex.Message);
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value.Substring(0, max);
    }

    public void Dispose()
    {
        SetEnabled(false);
    }
}