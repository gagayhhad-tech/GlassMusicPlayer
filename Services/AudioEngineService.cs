using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Drawing2D;
using GlassMusicPlayer.Models;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;
using TagLib;

namespace GlassMusicPlayer.Services;

public class AudioEngineService : IDisposable
{
    private IWavePlayer? _outputDevice;
    private AudioFileReader? _audioFile;
    private VisualizingSampleProvider? _visualizingProvider;
    private EqualizerService? _equalizerService;
    public EqualizerSettings EqualizerSettings { get; private set; } = new();
    private readonly Dictionary<string, PlaylistData> _playlists = new();
    private PlaylistData _currentPlaylist = new() { Id = "default", Name = "Default" };
    private int _currentTrackIndex = -1;
    private LoopMode _loopMode = LoopMode.None;
    private bool _isShuffled;
    private bool _isPlaying;
    private bool _isMuted;
    private bool _isTransitioning;
    private double _volume = 1.0;
    private readonly Random _random = new();
    private List<int> _shuffleOrder = new();
    private int _shuffleIndex;
    private string _shuffleQueueKey = "";
    private int _crossfadeFromIndex = -1;
    private int _crossfadeFromShuffleIndex = -1;
    private int _vizMode = 1; // 0=off, 1=bars, 2=radial, 3=wave
    private readonly Dictionary<string, CachedAnalysis> _analysisCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _analysisLock = new();
    private bool _analysisRunning;
    private bool _replayGain;
    private bool _discordRpc;
    private bool _flowActive;
    private HashSet<string> _flowUsed = new(StringComparer.OrdinalIgnoreCase);
    private TrackInfo? _flowBase;
    private readonly object _playbackLock = new();
    private readonly List<TrackInfo> _library = new();
    private readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".mp3", ".flac", ".alac", ".wav", ".aiff", ".aif", ".ogg", ".wma", ".m4a", ".aac", ".opus"
    };
    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string PlaylistsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlassMusicPlayer", "playlists.json");

    private static readonly string FavoritesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlassMusicPlayer", "favorites.json");

    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlassMusicPlayer", "settings.json");

    private static readonly string AnalysisFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlassMusicPlayer", "analysis.json");

    private string _theme = "default";
    private string _lastTrackPath = "";
    private double _lastPosition;
    private DateTime _lastPositionPersist = DateTime.MinValue;
    private bool _resumeAttempted;
    private double _resumeStartPosition;
    private readonly List<string> _libraryDirectories = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _pendingFilesLock = new();
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pendingRetries = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxPendingRetries = 10;
    private System.Threading.Timer? _scanDebounceTimer;
    private System.Timers.Timer? _sleepTimer;
    private double _crossfadeDuration = 3.0;
    private bool _crossfadeActive;
    private DateTime _crossfadeStart;
    private float _crossfadeTargetVolume = 1f;
    private AudioFileReader? _incomingReader;
    private VisualizingSampleProvider? _incomingViz;
    private EqualizerService? _incomingEq;
    private IWavePlayer? _incomingDevice;
    private AudioFileReader? _fadeOutReader;
    private readonly System.Timers.Timer _crossfadeTimer;

    private static readonly HttpClient _http = new();

    public static void Log(string tag, string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassMusicPlayer");
            Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                Path.Combine(dir, "ipc.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public event Action<PlayerState>? OnStateChanged;
    public event Action<TrackInfo>? OnTrackChanged;
    public event Action<AudioVisualizationData>? OnVisualization;
    public event Action<List<TrackInfo>>? OnLibraryChanged;
    public event Action<List<PlaylistData>>? OnPlaylistsChanged;
    public event Action<ScanStatusData>? OnScanStatus;
    public event Action<HashSet<string>>? OnFavoritesChanged;
    public Func<Task<string>>? OnOpenFolderDialogRequest { get; set; }

    private readonly System.Timers.Timer _positionTimer;
    private readonly System.Timers.Timer _visualizationTimer;
    private const int FftSize = 1024;
    private const int BandCount = 32;
    private readonly float[] _smoothedBands = new float[BandCount];
    private const float SmoothingFactor = 0.4f; // EMA smoothing (lower = smoother)

    public AudioEngineService()
    {
        _positionTimer = new System.Timers.Timer(250);
        _positionTimer.Elapsed += OnPositionTimer;
        _positionTimer.AutoReset = true;
        _positionTimer.Start();

        _visualizationTimer = new System.Timers.Timer(33); // ~30fps for viz
        _visualizationTimer.Elapsed += OnVisualizationTimer;
        _visualizationTimer.AutoReset = true;
        _visualizationTimer.Start();

        _crossfadeTimer = new System.Timers.Timer(25);
        _crossfadeTimer.Elapsed += OnCrossfadeTimer;
        _crossfadeTimer.AutoReset = true;
        _crossfadeTimer.Start();

        LoadPlaylists();
        LoadFavorites();
        LoadSettings();
        LoadAnalysisCache();
    }

    private void OnPositionTimer(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_isTransitioning || !_isPlaying || _audioFile == null || _outputDevice == null)
            return;

        NotifyState();

        // Periodically persist the playback position (throttled)
        if ((DateTime.UtcNow - _lastPositionPersist).TotalSeconds >= 5)
        {
            _lastPositionPersist = DateTime.UtcNow;
            PersistPlaybackState();
        }

        // Start crossfade into the next track before the current one ends
        if (_crossfadeDuration > 0 && !_crossfadeActive)
        {
            var remaining = _audioFile.TotalTime - _audioFile.CurrentTime;
            if (remaining <= TimeSpan.FromSeconds(_crossfadeDuration) && remaining > TimeSpan.FromMilliseconds(100))
            {
                TryStartCrossfade();
            }
        }

        if (_crossfadeActive || _isTransitioning)
            return;

        // Auto-advance to next track when current ends
        if (_audioFile.CurrentTime < _audioFile.TotalTime - TimeSpan.FromMilliseconds(100))
            return;

        _isTransitioning = true;

        lock (_playbackLock)
        {
            if (_loopMode == LoopMode.One)
            {
                _audioFile.CurrentTime = TimeSpan.Zero;
                _isTransitioning = false;
                NotifyState();
            }
            else
            {
                EnsureFlowQueue();
            }
            if (_loopMode == LoopMode.All ||
                     (_loopMode == LoopMode.None && _currentTrackIndex < _currentPlaylist.Tracks.Count - 1))
            {
                // Advance to next track (respecting shuffle order)
                int nextIndex;
                if (_isShuffled && _shuffleOrder.Count > 0)
                {
                    if (_loopMode == LoopMode.None && _shuffleIndex >= _shuffleOrder.Count - 1)
                    {
                        Stop();
                        _currentTrackIndex = -1;
                        NotifyState();
                        _isTransitioning = false;
                        return;
                    }
                    _shuffleIndex = (_shuffleIndex + 1) % _shuffleOrder.Count;
                    nextIndex = _shuffleOrder[_shuffleIndex];
                }
                else
                {
                    int trackCount = _currentPlaylist.Tracks.Count;
                    nextIndex = trackCount > 0 ? (_currentTrackIndex + 1) % trackCount : 0;
                }
                _currentTrackIndex = nextIndex;
                var nextTrack = _currentPlaylist.Tracks[nextIndex];

                if (!TryPlayTrack(nextTrack))
                    NotifyState();
            }
            else
            {
                // LoopMode.None and at end of playlist: stop
                Stop();
                _currentTrackIndex = -1;
                NotifyState();
            }
        }

        _isTransitioning = false;
    }

    private void OnVisualizationTimer(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_vizMode == 0)
            return;
        if (!_isPlaying || _audioFile == null)
        {
            // Fade out smoothed bands gradually when paused
            bool anyActive = false;
            for (int i = 0; i < BandCount; i++)
            {
                _smoothedBands[i] *= 0.92f;
                if (_smoothedBands[i] > 0.001f) anyActive = true;
            }
            
            OnVisualization?.Invoke(new AudioVisualizationData
            {
                Bands = (float[])_smoothedBands.Clone(),
                Amplitude = anyActive ? _smoothedBands.Max() : 0f,
                IsActive = anyActive
            });
            return;
        }

        try
        {
            // Get samples from visualizing provider (non-destructive read)
            float[] sampleBuffer;
            int samplesRead;
            if (_visualizingProvider != null)
            {
                _visualizingProvider.GetSamples(out sampleBuffer, out samplesRead);
            }
            else
            {
                sampleBuffer = new float[FftSize];
                samplesRead = 0;
            }
            
            if (samplesRead <= 0)
            {
                OnVisualization?.Invoke(new AudioVisualizationData
                {
                    Bands = new float[32],
                    Amplitude = 0f,
                    IsActive = false
                });
                return;
            }

            int actualRead = Math.Min(samplesRead, FftSize);
            if (actualRead < FftSize)
            {
                // Zero-pad to FFT size
                Array.Resize(ref sampleBuffer, FftSize);
                for (int i = actualRead; i < FftSize; i++)
                    sampleBuffer[i] = 0f;
            }

            // Apply Hann window to reduce spectral leakage
            for (int i = 0; i < FftSize; i++)
            {
                double hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
                sampleBuffer[i] *= (float)hann;
            }

            // Convert to complex array for FFT
            var fftBuffer = new System.Numerics.Complex[FftSize];
            for (int i = 0; i < FftSize; i++)
                fftBuffer[i] = new System.Numerics.Complex(sampleBuffer[i], 0);

            // Perform real FFT
            Fourier.Forward(fftBuffer, FourierOptions.NoScaling);

            // Compute magnitudes (only first half due to symmetry)
            int halfSize = FftSize / 2;
            var magnitudes = new double[halfSize];
            for (int i = 0; i < halfSize; i++)
            {
                magnitudes[i] = Math.Sqrt(fftBuffer[i].Real * fftBuffer[i].Real + fftBuffer[i].Imaginary * fftBuffer[i].Imaginary);
            }

            // Map FFT bins to logarithmic (Mel-like) frequency bands
            // Low frequencies (bass) get more bins, high frequencies (treble) get fewer
            var rawBands = new float[BandCount];
            float maxAmplitude = 0f;

            // Mel-scale mapping: lower bands cover fewer Hz, higher bands cover more Hz
            double nyquist = (_audioFile?.WaveFormat.SampleRate ?? 44100) / 2.0;
            double minFreq = 20.0;  // lowest audible freq
            double maxFreq = nyquist;

            for (int i = 0; i < halfSize; i++)
            {
                double freq = (double)i / halfSize * nyquist;
                if (freq < minFreq || freq > maxFreq) continue;

                // Map frequency to mel scale
                double mel = 2595.0 * Math.Log10(1.0 + freq / 700.0);
                double melMax = 2595.0 * Math.Log10(1.0 + maxFreq / 700.0);
                double melMin = 2595.0 * Math.Log10(1.0 + minFreq / 700.0);
                double normalizedMel = (mel - melMin) / (melMax - melMin);

                int bandIdx = (int)(normalizedMel * (BandCount - 1));
                bandIdx = Math.Clamp(bandIdx, 0, BandCount - 1);

                rawBands[bandIdx] += (float)magnitudes[i];
                if ((float)magnitudes[i] > maxAmplitude)
                    maxAmplitude = (float)magnitudes[i];
            }

            // Normalize raw bands
            float maxRaw = rawBands.Max();
            if (maxRaw > 0f)
            {
                for (int i = 0; i < BandCount; i++)
                {
                    rawBands[i] = Math.Min(1f, rawBands[i] / maxRaw * 1.5f);
                }
            }

            // Apply EMA smoothing for natural motion
            for (int i = 0; i < BandCount; i++)
            {
                _smoothedBands[i] += (rawBands[i] - _smoothedBands[i]) * SmoothingFactor;
                if (Math.Abs(_smoothedBands[i] - rawBands[i]) < 0.001f)
                    _smoothedBands[i] = rawBands[i];
            }

            float avgAmplitude = 0f;
            for (int i = 0; i < Math.Min(4, BandCount); i++)
                avgAmplitude += _smoothedBands[i]; // bass amplitude
            avgAmplitude /= 4f;

            OnVisualization?.Invoke(new AudioVisualizationData
            {
                Bands = (float[])_smoothedBands.Clone(),
                Amplitude = Math.Min(1f, avgAmplitude * 2f),
                IsActive = true
            });
        }
        catch
        {
            // Ignore visualization errors
        }
    }

    public async Task<string> HandleIpcMessage(IpcMessage msg)
    {
        try
        {
            Log("RECV", msg.Type + " " + msg.Payload.GetRawText());
            var result = msg.Type switch
            {
                "scanLibrary" => await ScanLibrary(msg.Payload),
                "playTrack" => PlayTrack(msg.Payload),
                "playPause" => PlayPause(),
                "stop" => Stop(),
                "next" => Next(),
                "previous" => Previous(),
                "seek" => Seek(msg.Payload),
                "setVolume" => SetVolume(msg.Payload),
                "adjustVolume" => AdjustVolume(msg.Payload),
                "setLoopMode" => SetLoopMode(msg.Payload),
                "toggleShuffle" => ToggleShuffle(),
                "toggleRepeat" => ToggleRepeat(),
                "toggleMute" => ToggleMute(),
                "getState" => GetState(),
                "getLibrary" => GetLibrary(),
                "getPlaylists" => GetPlaylists(),
                "getSettings" => GetSettings(),
                "setTheme" => SetTheme(msg.Payload),
                "createPlaylist" => CreatePlaylist(msg.Payload),
                "renamePlaylist" => RenamePlaylist(msg.Payload),
                "deletePlaylist" => DeletePlaylist(msg.Payload),
                "addToPlaylist" => AddToPlaylist(msg.Payload),
                "removeFromPlaylist" => RemoveFromPlaylist(msg.Payload),
                "reorderPlaylist" => ReorderPlaylist(msg.Payload),
                "scanFolder" => await ScanFolder(msg.Payload),
                "rescanAll" => await RescanAll(),
                "importFiles" => ImportPaths(msg.Payload),
                "deleteTracks" => DeleteTracks(msg.Payload),
                "setSleepTimer" => SetSleepTimer(msg.Payload),
                "setCrossfade" => SetCrossfade(msg.Payload),
                "getLyrics" => GetLyrics(msg.Payload),
                "fetchLyrics" => await FetchOnlineLyrics(msg.Payload),
                "getQueue" => GetQueue(),
                "playQueueTrack" => PlayQueueTrack(msg.Payload),
                "reorderQueue" => ReorderQueue(msg.Payload),
                "setFullscreen" => SetFullscreen(msg.Payload),
                "setVizMode" => SetVizMode(msg.Payload),
                "setVisualizer" => SetVisualizer(msg.Payload),
                "setReplayGain" => SetReplayGain(msg.Payload),
                "setDiscordRpc" => SetDiscordRpc(msg.Payload),
                "startFlow" => StartFlow(msg.Payload),
                "stopFlow" => StopFlow(),
                "openFolderDialog" => await OpenFolderDialog(),
                "getFileTags" => await GetFileTags(msg.Payload),
                "toggleFavorite" => ToggleFavorite(msg.Payload),
                "getFavorites" => GetFavorites(),
                "getEqualizerPresets" => GetEqualizerPresets(),
                "setEqualizerGains" => SetEqualizerGains(msg.Payload),
                "setEqualizerPreset" => SetEqualizerPreset(msg.Payload),
                "toggleEqualizer" => ToggleEqualizer(),
                "log" => LogJs(msg.Payload.GetRawText()),
                _ => JsonSerializer.Serialize(new { success = false, error = "Unknown command" })
            };
            Log("RESP", msg.Type + " => " + result);
            return result;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private string GetStringPayload(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            _ => el.GetRawText().Trim('"')
        };
    }

    private async Task<string> ScanLibrary(JsonElement payload)
    {
        List<string> directories;
        if (payload.ValueKind == JsonValueKind.Array)
        {
            directories = JsonSerializer.Deserialize<List<string>>(payload.GetRawText()) ?? 
                new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) };
        }
        else
        {
            directories = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) };
        }

        _libraryDirectories.Clear();
        foreach (var d in directories)
            if (!string.IsNullOrWhiteSpace(d) && !_libraryDirectories.Contains(d, StringComparer.OrdinalIgnoreCase))
                _libraryDirectories.Add(d);
        SaveSettings();

        _library.Clear();
        _currentPlaylist.Tracks.Clear();
        lock (_pendingFilesLock) { _pendingFiles.Clear(); _pendingRetries.Clear(); }
        
        foreach (var dir in directories)
        {
            await Task.Run(() => ScanDirectory(dir));
        }

        // Populate current playlist with all library tracks
        var tracks = SnapshotLibrary();
        _currentPlaylist.Tracks.Clear();
        _currentPlaylist.Tracks.AddRange(tracks);

        RefreshPlaylistTracks();
        OnLibraryChanged?.Invoke(tracks);
        return JsonSerializer.Serialize(new { success = true, tracks });
    }

    private static string LogJs(string message)
    {
        Log("JS", message);
        try
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(message);
            Log("JSHEX", string.Join(" ", raw.Take(200).Select(b => b.ToString("X2"))));
        }
        catch
        {
        }
        return JsonSerializer.Serialize(new { success = true });
    }

    private void ScanDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            var added = new List<TrackInfo>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!_supportedExtensions.Contains(ext)) continue;

                try
                {
                    var track = LoadTrackMetadata(file);
                    lock (_library)
                    {
                        if (!_library.Any(t => t.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
                        {
                            _library.Add(track);
                            added.Add(track);
                        }
                    }
                }
                catch
                {
                    // Skip corrupted files
                }
            }
            if (added.Count > 0) QueueAnalysis(added);
        }
        catch
        {
            // Skip inaccessible directories
        }
    }

    private string ImportPaths(JsonElement payload)
    {
        var paths = new List<string>();
        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in payload.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String)
                    paths.Add(el.GetString() ?? "");
        }
        else if (payload.ValueKind == JsonValueKind.String)
        {
            paths.Add(payload.GetString() ?? "");
        }

        bool anyProcessed = false;
        var newDirs = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    if (!_libraryDirectories.Contains(p)) newDirs.Add(p);
                    ScanDirectory(p);
                    anyProcessed = true;
                }
                else if (System.IO.File.Exists(p))
                {
                    var ext = Path.GetExtension(p);
                    if (!_supportedExtensions.Contains(ext)) continue;
                    var track = LoadTrackMetadata(p);
                    lock (_library)
                    {
                        if (!_library.Any(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        {
                            _library.Add(track);
                            QueueAnalysis(new[] { track });
                        }
                    }
                    anyProcessed = true;
                }
            }
            catch
            {
            }
        }

        if (newDirs.Count > 0)
        {
            _libraryDirectories.AddRange(newDirs);
            StartFolderWatchers();
            SaveSettings();
        }

        if (anyProcessed)
        {
            RefreshPlaylistTracks();
            OnLibraryChanged?.Invoke(SnapshotLibrary());
        }
        return JsonSerializer.Serialize(new { success = true, imported = anyProcessed });
    }

    private TrackInfo LoadTrackMetadata(string filePath)
    {
        var track = new TrackInfo
        {
            Id = Guid.NewGuid().ToString(),
            Path = filePath,
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "Unknown",
            Album = "Unknown",
            Format = Path.GetExtension(filePath).TrimStart('.').ToUpper(),
            Size = new FileInfo(filePath).Length
        };

        try
        {
            using var reader = new AudioFileReader(filePath);
            track.Duration = reader.TotalTime.TotalSeconds;
            track.SampleRate = reader.WaveFormat.SampleRate;
            track.Channels = reader.WaveFormat.Channels;
        }
        catch
        {
            // Duration read failed
        }

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            track.Title = tagFile.Tag.Title ?? track.Title;
            track.Artist = tagFile.Tag.FirstAlbumArtist ?? tagFile.Tag.FirstPerformer ?? "Unknown";
            track.Album = tagFile.Tag.Album ?? "Unknown";
            track.Bitrate = (int)(tagFile.Properties.AudioBitrate * 1000);
            track.TrackNumber = tagFile.Tag.Track;
            
            if (tagFile.Tag.Pictures.Length > 0)
                {
                    var coverDir = Path.Combine(Path.GetTempPath(), "GlassMusicPlayer", "covers");
                    Directory.CreateDirectory(coverDir);
                    var coverFile = Path.Combine(coverDir, $"{track.Id}.jpg");
                    System.IO.File.WriteAllBytes(coverFile, tagFile.Tag.Pictures[0].Data.Data);
                    // Use virtual hostname URL instead of file:/// (WebView2 blocks file:// when loaded via NavigateToString)
                    track.CoverPath = $"https://covers.localhost/{track.Id}.jpg";
                    track.Accent = GetDominantAccent(tagFile.Tag.Pictures[0].Data.Data);
                }
            else
                {
                    // No embedded cover - try to fetch album art from iTunes Search API
                    track.CoverPath = AlbumArtProvider.GetCoverUrl(track.Artist, track.Album) ?? "";
                }
        }
        catch
        {
            // Tags not available
        }

        return track;
    }

    private void TryStartCrossfade()
    {
        lock (_playbackLock)
        {
            if (_crossfadeActive || _audioFile == null) return;

            int trackCount = _currentPlaylist.Tracks.Count;
            if (trackCount == 0) return;
            if (_loopMode == LoopMode.One) return;

            EnsureFlowQueue();

            bool shouldAdvance = _loopMode == LoopMode.All ||
                                 (_loopMode == LoopMode.None && _currentTrackIndex < trackCount - 1);
            if (!shouldAdvance) return;

            int nextIndex;
            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                if (_loopMode == LoopMode.None && _shuffleIndex >= _shuffleOrder.Count - 1) return;
                _shuffleIndex = (_shuffleIndex + 1) % _shuffleOrder.Count;
                nextIndex = _shuffleOrder[_shuffleIndex];
            }
            else
            {
                nextIndex = (_currentTrackIndex + 1) % trackCount;
            }
            if (nextIndex < 0 || nextIndex >= _currentPlaylist.Tracks.Count) return;
            var nextTrack = _currentPlaylist.Tracks[nextIndex];
            if (nextTrack == null || !System.IO.File.Exists(nextTrack.Path)) return;

            try
            {
                var nextReader = new AudioFileReader(nextTrack.Path);
                var nextViz = new VisualizingSampleProvider(nextReader);
                var nextEq = new EqualizerService(nextViz);
                nextEq.SetGains(EqualizerSettings.CustomGains, EqualizerSettings.IsEnabled);
                nextReader.Volume = 0f;
                var nextDevice = new WaveOutEvent
                {
                    DesiredLatency = 500,
                    NumberOfBuffers = 3
                };
                nextDevice.Init(nextEq);
                nextDevice.Play();

                _incomingReader = nextReader;
                _incomingViz = nextViz;
                _incomingEq = nextEq;
                _incomingDevice = nextDevice;
                _fadeOutReader = _audioFile;

                _crossfadeFromIndex = _currentTrackIndex;
                _crossfadeFromShuffleIndex = _isShuffled ? _shuffleIndex : -1;
                _currentTrackIndex = nextIndex;
                _crossfadeActive = true;
                _isTransitioning = true;
                _crossfadeStart = DateTime.UtcNow;
                _crossfadeTargetVolume = GetEffectiveVolume(nextTrack);
            }
            catch
            {
                CleanupIncoming();
            }
        }
    }

    private void OnCrossfadeTimer(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_crossfadeActive)
            return;

        if (_fadeOutReader == null || _incomingReader == null)
        {
            lock (_playbackLock)
            {
                _crossfadeActive = false;
                _isTransitioning = false;
            }
            return;
        }

        var progress = (DateTime.UtcNow - _crossfadeStart).TotalSeconds / _crossfadeDuration;
        if (progress >= 1.0)
        {
            CompleteCrossfade();
            return;
        }

        lock (_playbackLock)
        {
            _fadeOutReader.Volume = _crossfadeTargetVolume * (float)(1.0 - progress);
            _incomingReader.Volume = _crossfadeTargetVolume * (float)progress;
        }
    }

    private void CompleteCrossfade()
    {
        lock (_playbackLock)
        {
            try { _outputDevice?.Stop(); } catch { }
            _outputDevice?.Dispose();
            _visualizingProvider?.Dispose();
            _equalizerService?.Dispose();
            try { _fadeOutReader?.Dispose(); } catch { }

            _audioFile = _incomingReader;
            _visualizingProvider = _incomingViz;
            _equalizerService = _incomingEq;
            _outputDevice = _incomingDevice;

            _incomingReader = null;
            _incomingViz = null;
            _incomingEq = null;
            _incomingDevice = null;
            _fadeOutReader = null;

            _crossfadeFromIndex = -1;
            _crossfadeFromShuffleIndex = -1;

            _crossfadeActive = false;
            _isTransitioning = false;
            _isPlaying = true;

            if (_currentTrackIndex >= 0 && _currentTrackIndex < _currentPlaylist.Tracks.Count)
                OnTrackChanged?.Invoke(_currentPlaylist.Tracks[_currentTrackIndex]);
            NotifyState();
        }
    }

    private void CancelCrossfade()
    {
        lock (_playbackLock)
        {
            if (!_crossfadeActive) return;
            _crossfadeActive = false;
            _isTransitioning = false;
            // Restore to the track that was playing before the crossfade began
            if (_crossfadeFromIndex >= 0)
                _currentTrackIndex = _crossfadeFromIndex;
            if (_crossfadeFromShuffleIndex >= 0)
                _shuffleIndex = _crossfadeFromShuffleIndex;
            _crossfadeFromIndex = -1;
            _crossfadeFromShuffleIndex = -1;
            if (_fadeOutReader != null)
            {
                try { _fadeOutReader.Volume = _crossfadeTargetVolume; } catch { }
                _fadeOutReader = null;
            }
            CleanupIncoming();
        }
    }

    private void CleanupIncoming()
    {
        try { _incomingDevice?.Stop(); } catch { }
        try { _incomingDevice?.Dispose(); } catch { }
        _incomingDevice = null;
        try { _incomingEq?.Dispose(); } catch { }
        _incomingEq = null;
        try { _incomingViz?.Dispose(); } catch { }
        _incomingViz = null;
        try { _incomingReader?.Dispose(); } catch { }
        _incomingReader = null;
    }

    private bool TryPlayTrack(TrackInfo track)
    {
        lock (_playbackLock)
        {
            Stop();
            try
            {
                _audioFile = new AudioFileReader(track.Path);
                if (_resumeStartPosition > 1)
                {
                    var maxStart = _audioFile.TotalTime.TotalSeconds - 1;
                    if (_resumeStartPosition < maxStart)
                        _audioFile.CurrentTime = TimeSpan.FromSeconds(_resumeStartPosition);
                    _resumeStartPosition = 0;
                }
                _visualizingProvider = new VisualizingSampleProvider(_audioFile);
                _equalizerService = new EqualizerService(_visualizingProvider);
                _equalizerService.SetGains(EqualizerSettings.CustomGains, EqualizerSettings.IsEnabled);
                _outputDevice = new WaveOutEvent
                {
                    DesiredLatency = 500,
                    NumberOfBuffers = 3
                };
                _outputDevice.Init(_equalizerService);
                _audioFile.Volume = GetEffectiveVolume(track);
                _outputDevice.Play();
                _isPlaying = true;

                _lastTrackPath = track.Path;
                _lastPosition = _audioFile.CurrentTime.TotalSeconds;

                EnsureAccent(track);
                OnTrackChanged?.Invoke(track);
                NotifyState();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private string PlayTrack(JsonElement payload)
    {
        lock (_playbackLock)
        {
            // Accept either a string track id or { id, playlistId }
            string? trackId = null;
            string? playlistId = null;
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("id", out var idEl)) trackId = idEl.GetString();
                if (payload.TryGetProperty("playlistId", out var plEl)) playlistId = plEl.GetString();
            }
            else
            {
                trackId = GetStringPayload(payload);
            }

            var library = SnapshotLibrary();

            var track = library.FirstOrDefault(t => t.Id == trackId)
                        ?? library.FirstOrDefault(t => t.Path.Equals(trackId ?? "", StringComparison.OrdinalIgnoreCase));

            if (track == null)
                return JsonSerializer.Serialize(new { success = false, error = "Track not found" });

            // Build the playback queue from the playlist context, otherwise the whole library
            if (!string.IsNullOrEmpty(playlistId) && _playlists.TryGetValue(playlistId, out var playlist))
            {
                _currentPlaylist.Id = playlist.Id;
                _currentPlaylist.Name = playlist.Name;
                _currentPlaylist.Tracks.Clear();
                _currentPlaylist.Tracks.AddRange(ResolveTracks(playlist.Tracks));
            }
            else if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("trackIds", out var idsEl) &&
                     idsEl.ValueKind == JsonValueKind.Array)
            {
                var paths = idsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                var byPath = library.ToDictionary(t => t.Path, t => t, StringComparer.OrdinalIgnoreCase);
                var ordered = new List<TrackInfo>();
                foreach (var p in paths)
                    if (byPath.TryGetValue(p, out var lt))
                        ordered.Add(lt);
                _currentPlaylist.Id = "album";
                _currentPlaylist.Name = "Album";
                _currentPlaylist.Tracks.Clear();
                _currentPlaylist.Tracks.AddRange(ordered);
            }
            else
            {
                _currentPlaylist.Id = "default";
                _currentPlaylist.Name = "Default";
                _currentPlaylist.Tracks.Clear();
                _currentPlaylist.Tracks.AddRange(library);
            }
            RebuildShuffleOrderIfNeeded();

            int playlistIndex = _currentPlaylist.Tracks.FindIndex(t => t.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
            if (playlistIndex < 0)
            {
                _currentPlaylist.Tracks.Add(track);
                playlistIndex = _currentPlaylist.Tracks.Count - 1;
            }

            _currentTrackIndex = playlistIndex;
            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                var si = _shuffleOrder.IndexOf(playlistIndex);
                _shuffleIndex = si < 0 ? 0 : si;
            }

            if (!TryPlayTrack(track))
                return JsonSerializer.Serialize(new { success = false, error = "Failed to play track" });

            return JsonSerializer.Serialize(new { success = true, track });
        }
    }

    private string PlayPause()
    {
        lock (_playbackLock)
        {
            if (_outputDevice == null)
            {
                if (_currentPlaylist.Tracks.Count > 0)
                    return PlayTrack(MakeStringElement(_currentPlaylist.Tracks[0].Id));
                return JsonSerializer.Serialize(new { success = false, error = "No tracks" });
            }

            if (_isPlaying)
            {
                CancelCrossfade();
                _outputDevice.Pause();
                _isPlaying = false;
            }
            else
            {
                _outputDevice.Play();
                _isPlaying = true;
            }

            NotifyState();
            return JsonSerializer.Serialize(new { success = true });
        }
    }

    private string Stop()
    {
        lock (_playbackLock)
        {
            CancelCrossfade();
            _outputDevice?.Stop();
            _outputDevice?.Dispose();
            _outputDevice = null;
            _visualizingProvider?.Dispose();
            _visualizingProvider = null;
            _equalizerService?.Dispose();
            _equalizerService = null;
            _audioFile?.Dispose();
            _audioFile = null;
            _isPlaying = false;
            NotifyState();
            return JsonSerializer.Serialize(new { success = true });
        }
    }

    private string ToggleMute()
    {
        lock (_playbackLock)
        {
            _isMuted = !_isMuted;
            if (_audioFile != null)
                _audioFile.Volume = (float)(_isMuted ? 0 : _volume);
            NotifyState();
            return JsonSerializer.Serialize(new { success = true, isMuted = _isMuted });
        }
    }

    private string Next()
    {
        lock (_playbackLock)
        {
            if (_currentPlaylist.Tracks.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No tracks" });

            EnsureFlowQueue();
            RebuildShuffleOrderIfNeeded();

            int nextIndex;
            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                if (_loopMode == LoopMode.None && _shuffleIndex >= _shuffleOrder.Count - 1)
                    return Stop();
                _shuffleIndex = (_shuffleIndex + 1) % _shuffleOrder.Count;
                nextIndex = _shuffleOrder[_shuffleIndex];
            }
            else
            {
                // If loop mode is None and we're at the last track, stop instead of wrapping
                if (_loopMode == LoopMode.None && _currentTrackIndex >= _currentPlaylist.Tracks.Count - 1)
                    return Stop();
                nextIndex = (_currentTrackIndex + 1) % _currentPlaylist.Tracks.Count;
            }

            _currentTrackIndex = nextIndex;
            _isTransitioning = false; // Reset transition flag for next track
            var nextTrack = _currentPlaylist.Tracks[_currentTrackIndex];
            if (!TryPlayTrack(nextTrack))
                return JsonSerializer.Serialize(new { success = false, error = "Failed to play track" });
            return JsonSerializer.Serialize(new { success = true, track = nextTrack });
        }
    }

    private string Previous()
    {
        lock (_playbackLock)
        {
            if (_currentPlaylist.Tracks.Count == 0)
                return JsonSerializer.Serialize(new { success = false, error = "No tracks" });

            RebuildShuffleOrderIfNeeded();

            int prevIndex;
            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                _shuffleIndex = (_shuffleIndex - 1 + _shuffleOrder.Count) % _shuffleOrder.Count;
                prevIndex = _shuffleOrder[_shuffleIndex];
            }
            else
            {
                prevIndex = (_currentTrackIndex - 1 + _currentPlaylist.Tracks.Count) % _currentPlaylist.Tracks.Count;
            }

            _currentTrackIndex = prevIndex;
            var prevTrack = _currentPlaylist.Tracks[_currentTrackIndex];
            if (!TryPlayTrack(prevTrack))
                return JsonSerializer.Serialize(new { success = false, error = "Failed to play track" });
            return JsonSerializer.Serialize(new { success = true, track = prevTrack });
        }
    }

    private string GetQueue()
    {
        lock (_playbackLock)
        {
            RebuildShuffleOrderIfNeeded();
            var items = new List<object>();
            string? currentPath = _currentTrackIndex >= 0 && _currentTrackIndex < _currentPlaylist.Tracks.Count
                ? _currentPlaylist.Tracks[_currentTrackIndex].Path
                : null;

            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                for (int i = 0; i < _shuffleOrder.Count; i++)
                {
                    int idx = _shuffleOrder[(i + _shuffleIndex) % _shuffleOrder.Count];
                    if (idx < 0 || idx >= _currentPlaylist.Tracks.Count) continue;
                    var t = _currentPlaylist.Tracks[idx];
                    items.Add(new
                    {
                        id = t.Id,
                        path = t.Path,
                        title = t.Title,
                        artist = t.Artist,
                        album = t.Album,
                        format = t.Format,
                        duration = t.Duration,
                        coverPath = t.CoverPath,
                        isCurrent = t.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            else
            {
                for (int i = 0; i < _currentPlaylist.Tracks.Count; i++)
                {
                    var t = _currentPlaylist.Tracks[i];
                    items.Add(new
                    {
                        id = t.Id,
                        path = t.Path,
                        title = t.Title,
                        artist = t.Artist,
                        album = t.Album,
                        format = t.Format,
                        duration = t.Duration,
                        coverPath = t.CoverPath,
                        isCurrent = t.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            return JsonSerializer.Serialize(new { success = true, isShuffled = _isShuffled, queue = items });
        }
    }

    private string DeleteTracks(JsonElement payload)
    {
        var paths = new List<string>();
        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in payload.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String)
                    paths.Add(el.GetString() ?? "");
        }
        else if (payload.ValueKind == JsonValueKind.String)
        {
            paths.Add(payload.GetString() ?? "");
        }

        int deleted = 0;
        foreach (var p in paths)
        {
            try
            {
                lock (_library)
                {
                    _library.RemoveAll(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
                }
                lock (_playbackLock)
                {
                    _currentPlaylist.Tracks.RemoveAll(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
                }
                foreach (var pl in _playlists.Values)
                {
                    pl.Tracks.RemoveAll(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
                }
                _favorites.Remove(p);
                lock (_pendingFilesLock)
                {
                    _pendingFiles.Remove(p);
                    _pendingRetries.Remove(p);
                }

                if (System.IO.File.Exists(p))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        p,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    deleted++;
                }
            }
            catch
            {
            }
        }

        if (deleted > 0)
        {
            SaveFavorites();
            SavePlaylists();
            RefreshPlaylistTracks();
            OnLibraryChanged?.Invoke(SnapshotLibrary());
            OnFavoritesChanged?.Invoke(_favorites);
        }
        return JsonSerializer.Serialize(new { success = true, deleted });
    }

    private string ReorderQueue(JsonElement payload)
    {
        lock (_playbackLock)
        {
            int from = -1, to = -1;
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("from", out var f)) from = f.GetInt32();
                if (payload.TryGetProperty("to", out var t)) to = t.GetInt32();
            }
            int count = _currentPlaylist.Tracks.Count;
            if (count == 0 || from < 0 || from >= count || to < 0 || to >= count || from == to)
                return JsonSerializer.Serialize(new { success = false, error = "Invalid indices" });

            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                int curShuffle = _shuffleIndex;
                int moved = _shuffleOrder[from];
                _shuffleOrder.RemoveAt(from);
                _shuffleOrder.Insert(to, moved);
                if (from == curShuffle) _shuffleIndex = to;
                else if (from < curShuffle && to >= curShuffle) _shuffleIndex--;
                else if (from > curShuffle && to <= curShuffle) _shuffleIndex++;
            }
            else
            {
                int curIdx = _currentTrackIndex;
                var movedTrack = _currentPlaylist.Tracks[from];
                _currentPlaylist.Tracks.RemoveAt(from);
                _currentPlaylist.Tracks.Insert(to, movedTrack);
                if (from == curIdx) _currentTrackIndex = to;
                else if (from < curIdx && to >= curIdx) _currentTrackIndex--;
                else if (from > curIdx && to <= curIdx) _currentTrackIndex++;
            }
            return JsonSerializer.Serialize(new { success = true });
        }
    }

    private string SetFullscreen(JsonElement payload)
    {
        bool open = payload.ValueKind == JsonValueKind.True || payload.GetBoolean();
        // When entering fullscreen, visualization starts paused; the user can
        // re-enable it with a specific mode via setVizMode.
        if (open) _vizMode = 0;
        else _vizMode = 1;
        return JsonSerializer.Serialize(new { success = true });
    }

    private string SetVizMode(JsonElement payload)
    {
        int mode = payload.ValueKind == JsonValueKind.Number ? payload.GetInt32() : 0;
        _vizMode = Math.Clamp(mode, 0, 3);
        return JsonSerializer.Serialize(new { success = true, mode = _vizMode });
    }

    private string SetVisualizer(JsonElement payload)
    {
        bool on = payload.ValueKind == JsonValueKind.True || payload.GetBoolean();
        _vizMode = on ? 1 : 0;
        SaveSettings();
        return JsonSerializer.Serialize(new { success = true, visualizerOn = _vizMode != 0 });
    }

    private string PlayQueueTrack(JsonElement payload)
    {
        lock (_playbackLock)
        {
            string trackId = GetStringPayload(payload);
            if (string.IsNullOrEmpty(trackId))
                return JsonSerializer.Serialize(new { success = false, error = "No track" });

            RebuildShuffleOrderIfNeeded();

            int idx = _currentPlaylist.Tracks.FindIndex(t =>
                t.Id == trackId || t.Path.Equals(trackId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                return JsonSerializer.Serialize(new { success = false, error = "Track not in queue" });

            _currentTrackIndex = idx;
            if (_isShuffled && _shuffleOrder.Count > 0)
            {
                var si = _shuffleOrder.IndexOf(idx);
                _shuffleIndex = si >= 0 ? si : _shuffleIndex;
            }

            var track = _currentPlaylist.Tracks[idx];
            if (!TryPlayTrack(track))
                return JsonSerializer.Serialize(new { success = false, error = "Failed to play track" });
            return JsonSerializer.Serialize(new { success = true, track = track });
        }
    }

    private string Seek(JsonElement payload)
    {
        lock (_playbackLock)
        {
            double position = payload.ValueKind == JsonValueKind.Number ? payload.GetDouble() : 0;
            if (_audioFile != null)
            {
                _audioFile.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, Math.Min(position, _audioFile.TotalTime.TotalSeconds)));
                NotifyState();
                return JsonSerializer.Serialize(new { success = true });
            }
            return JsonSerializer.Serialize(new { success = false, error = "No track loaded" });
        }
    }

    private string SetVolume(JsonElement payload)
    {
        lock (_playbackLock)
        {
            _volume = payload.ValueKind == JsonValueKind.Number ? payload.GetDouble() : 1.0;
            _volume = Math.Max(0, Math.Min(1, _volume));
            if (_audioFile != null)
                _audioFile.Volume = (float)(_isMuted ? 0 : _volume);
            NotifyState();
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true });
        }
    }

    private string AdjustVolume(JsonElement payload)
    {
        lock (_playbackLock)
        {
            double delta = payload.ValueKind == JsonValueKind.Number ? payload.GetDouble() : 0.05;
            _volume = Math.Max(0, Math.Min(1, _volume + delta));
            if (_audioFile != null)
                _audioFile.Volume = (float)(_isMuted ? 0 : _volume);
            NotifyState();
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true, volume = _volume });
        }
    }

    private string SetLoopMode(JsonElement payload)
    {
        lock (_playbackLock)
        {
            _loopMode = JsonSerializer.Deserialize<LoopMode>(payload.GetRawText());
            NotifyState();
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true });
        }
    }

    private string ToggleShuffle()
    {
        lock (_playbackLock)
        {
            _isShuffled = !_isShuffled;
            if (_isShuffled)
            {
                _shuffleQueueKey = "";
                RebuildShuffleOrderIfNeeded();
            }
            NotifyState();
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true, isShuffled = _isShuffled });
        }
    }

    private string ToggleRepeat()
    {
        lock (_playbackLock)
        {
            _loopMode = _loopMode switch
            {
                LoopMode.None => LoopMode.All,
                LoopMode.All => LoopMode.One,
                LoopMode.One => LoopMode.None,
                _ => LoopMode.None
            };
            NotifyState();
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true, loopMode = (int)_loopMode });
        }
    }

    private string ToggleFavorite(JsonElement payload)
    {
        var path = GetStringPayload(payload);
        if (string.IsNullOrEmpty(path))
            return JsonSerializer.Serialize(new { success = false, error = "No path" });

        if (_favorites.Contains(path))
            _favorites.Remove(path);
        else
            _favorites.Add(path);

        OnFavoritesChanged?.Invoke(new HashSet<string>(_favorites, StringComparer.OrdinalIgnoreCase));
        SaveFavorites();
        return JsonSerializer.Serialize(new { success = true, favorites = _favorites.ToList() });
    }

    private string GetFavorites()
    {
        return JsonSerializer.Serialize(new { success = true, favorites = _favorites.ToList() });
    }

    private string GetEqualizerPresets()
    {
        var presets = EqualizerPresets.GetAll();
        return JsonSerializer.Serialize(new { success = true, presets, currentPreset = EqualizerSettings.CurrentPreset, isEnabled = EqualizerSettings.IsEnabled, customGains = EqualizerSettings.CustomGains });
    }

    private string SetEqualizerGains(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            var gains = JsonSerializer.Deserialize<float[]>(payload.GetRawText());
            if (gains != null && gains.Length == 10)
            {
                EqualizerSettings.CustomGains = gains;
                EqualizerSettings.CurrentPreset = "custom";
                EqualizerSettings.IsEnabled = true;
                _equalizerService?.SetGains(gains, true);
                SaveSettings();
                return JsonSerializer.Serialize(new { success = true });
            }
        }
        return JsonSerializer.Serialize(new { success = false, error = "Invalid gains data" });
    }

    private string SetEqualizerPreset(JsonElement payload)
    {
        var presetName = GetStringPayload(payload);
        var preset = EqualizerPresets.GetPreset(presetName);
        if (preset != null)
        {
            EqualizerSettings.CurrentPreset = presetName;
            EqualizerSettings.CustomGains = (float[])preset.Gains.Clone();
            EqualizerSettings.IsEnabled = !preset.IsFlat;
            _equalizerService?.SetGains(preset.Gains, !preset.IsFlat);
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true, gains = preset.Gains, isEnabled = !preset.IsFlat });
        }
        return JsonSerializer.Serialize(new { success = false, error = "Preset not found" });
    }

    private string ToggleEqualizer()
    {
        EqualizerSettings.IsEnabled = !EqualizerSettings.IsEnabled;
        var gains = EqualizerSettings.IsEnabled ? EqualizerSettings.CustomGains : EqualizerPresets.GetFlatGains();
        _equalizerService?.SetGains(gains, EqualizerSettings.IsEnabled);
        SaveSettings();
        return JsonSerializer.Serialize(new { success = true, isEnabled = EqualizerSettings.IsEnabled });
    }

    private string GetState()
    {
        return JsonSerializer.Serialize(new
        {
            success = true,
            state = new PlayerState
            {
                IsPlaying = _isPlaying,
                CurrentTime = _audioFile?.CurrentTime.TotalSeconds ?? 0,
                Duration = _audioFile?.TotalTime.TotalSeconds ?? 0,
                Volume = _volume,
                IsMuted = _isMuted,
                CurrentTrack = _currentTrackIndex >= 0 && _currentTrackIndex < _currentPlaylist.Tracks.Count 
                    ? _currentPlaylist.Tracks[_currentTrackIndex] : null,
                LoopMode = _loopMode,
                IsShuffled = _isShuffled
            }
        });
    }

    private List<TrackInfo> SnapshotLibrary()
    {
        lock (_library)
        {
            return new List<TrackInfo>(_library);
        }
    }

    private List<TrackInfo> ResolveTracks(IEnumerable<TrackInfo> stored)
    {
        var library = SnapshotLibrary();
        return stored.Select(t =>
        {
            var fresh = library.FirstOrDefault(x => x.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
            return fresh ?? t;
        }).ToList();
    }

    private void RebuildShuffleOrderIfNeeded()
    {
        if (!_isShuffled) return;
        var key = string.Join("\u0001", _currentPlaylist.Tracks.Select(t => t.Id));
        if (key == _shuffleQueueKey) return;
        _shuffleQueueKey = key;
        int currentIndex = _currentTrackIndex;
        _shuffleOrder.Clear();
        _shuffleOrder.AddRange(Enumerable.Range(0, _currentPlaylist.Tracks.Count));
        _shuffleOrder = _shuffleOrder.OrderBy(_ => _random.Next()).ToList();
        // Anchor the current track in the new queue so playback continues without
        // restarting the shuffle cycle (avoids immediate repeats).
        if (currentIndex >= 0)
        {
            var pos = _shuffleOrder.IndexOf(currentIndex);
            _shuffleIndex = pos >= 0 ? pos : 0;
        }
        else
        {
            _shuffleIndex = 0;
        }
    }

    private void RefreshPlaylistTracks()
    {
        foreach (var pl in _playlists.Values)
        {
            pl.Tracks = ResolveTracks(pl.Tracks);
        }
        OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
    }

    private sealed class PlaylistFileDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> TrackPaths { get; set; } = new();
    }

    private void SavePlaylists()
    {
        try
        {
            var dir = Path.GetDirectoryName(PlaylistsFilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            var dto = _playlists.Values.Select(p => new PlaylistFileDto
            {
                Id = p.Id,
                Name = p.Name,
                TrackPaths = p.Tracks.Select(t => t.Path).ToList()
            }).ToList();
            System.IO.File.WriteAllText(PlaylistsFilePath, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // Persistence is best-effort
        }
    }

    private void LoadPlaylists()
    {
        try
        {
            if (!System.IO.File.Exists(PlaylistsFilePath)) return;
            var json = System.IO.File.ReadAllText(PlaylistsFilePath);
            var dto = JsonSerializer.Deserialize<List<PlaylistFileDto>>(json);
            if (dto == null) return;

            _playlists.Clear();
            foreach (var item in dto)
            {
                var playlist = new PlaylistData { Id = item.Id, Name = item.Name };
                foreach (var path in item.TrackPaths)
                {
                    var track = _library.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (track != null)
                    {
                        playlist.Tracks.Add(track);
                    }
                    else
                    {
                        playlist.Tracks.Add(new TrackInfo
                        {
                            Id = Guid.NewGuid().ToString(),
                            Path = path,
                            Title = Path.GetFileNameWithoutExtension(path),
                            Format = Path.GetExtension(path).TrimStart('.').ToUpper()
                        });
                    }
                }
                _playlists[playlist.Id] = playlist;
            }
        }
        catch
        {
            // Corrupt or missing file
        }
    }

    private void LoadFavorites()
    {
        try
        {
            if (!System.IO.File.Exists(FavoritesFilePath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(FavoritesFilePath));
            if (list == null) return;
            _favorites.Clear();
            foreach (var p in list)
                if (!string.IsNullOrEmpty(p))
                    _favorites.Add(p);
        }
        catch
        {
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var dir = Path.GetDirectoryName(FavoritesFilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(FavoritesFilePath, JsonSerializer.Serialize(_favorites.ToList()));
        }
        catch
        {
        }
    }

    private sealed class SettingsFileDto
    {
        public double Volume { get; set; } = 1.0;
        public int LoopMode { get; set; }
        public bool IsShuffled { get; set; }
        public bool EqEnabled { get; set; }
        public float[] EqGains { get; set; } = new float[10];
        public string EqPreset { get; set; } = "Flat";
        public string Theme { get; set; } = "default";
        public double CrossfadeDuration { get; set; } = 3.0;
        public List<string> LibraryDirectories { get; set; } = new();
        public string LastTrackPath { get; set; } = "";
        public double LastPosition { get; set; }
        public bool ReplayGain { get; set; }
        public bool VisualizerOn { get; set; } = true;
        public bool DiscordRpc { get; set; }
    }

    private sealed class CachedAnalysis
    {
        public long MtimeTicks { get; set; }
        public double Bpm { get; set; }
        public double Energy { get; set; }
        public double LoudnessDb { get; set; }
        public double PeakDb { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsFilePath)) return;
            var dto = JsonSerializer.Deserialize<SettingsFileDto>(System.IO.File.ReadAllText(SettingsFilePath));
            if (dto == null) return;
            _volume = Math.Max(0, Math.Min(1, dto.Volume));
            _loopMode = (LoopMode)Math.Clamp(dto.LoopMode, 0, 2);
            _isShuffled = dto.IsShuffled;
            EqualizerSettings.IsEnabled = dto.EqEnabled;
            EqualizerSettings.CurrentPreset = dto.EqPreset;
            if (dto.EqGains != null && dto.EqGains.Length == 10)
                EqualizerSettings.CustomGains = dto.EqGains;
            _theme = dto.Theme;
            _crossfadeDuration = Math.Max(0, Math.Min(10, dto.CrossfadeDuration));
            _lastTrackPath = dto.LastTrackPath ?? "";
            _lastPosition = Math.Max(0, dto.LastPosition);
            _replayGain = dto.ReplayGain;
            _vizMode = dto.VisualizerOn ? 1 : 0;
            _discordRpc = dto.DiscordRpc;
            _libraryDirectories.Clear();
            if (dto.LibraryDirectories != null)
                foreach (var d in dto.LibraryDirectories)
                    if (!string.IsNullOrWhiteSpace(d) && !_libraryDirectories.Contains(d, StringComparer.OrdinalIgnoreCase))
                        _libraryDirectories.Add(d);
            if (_libraryDirectories.Count == 0)
            {
                var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                if (!string.IsNullOrEmpty(music)) _libraryDirectories.Add(music);
            }
        }
        catch
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            var dto = new SettingsFileDto
            {
                Volume = _volume,
                LoopMode = (int)_loopMode,
                IsShuffled = _isShuffled,
                EqEnabled = EqualizerSettings.IsEnabled,
                EqGains = (float[])EqualizerSettings.CustomGains.Clone(),
                EqPreset = EqualizerSettings.CurrentPreset,
                Theme = _theme,
                CrossfadeDuration = _crossfadeDuration,
                LibraryDirectories = new List<string>(_libraryDirectories),
                LastTrackPath = _lastTrackPath,
                LastPosition = _lastPosition,
                ReplayGain = _replayGain,
                VisualizerOn = _vizMode != 0,
                DiscordRpc = _discordRpc
            };
            System.IO.File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(dto));
        }
        catch
        {
        }
    }

    private void PersistPlaybackState()
    {
        if (_currentTrackIndex >= 0 && _currentTrackIndex < _currentPlaylist.Tracks.Count && _audioFile != null)
        {
            _lastTrackPath = _currentPlaylist.Tracks[_currentTrackIndex].Path;
            _lastPosition = _audioFile.CurrentTime.TotalSeconds;
        }
        SaveSettings();
    }

    private void ResumePlaybackIfNeeded()
    {
        if (_resumeAttempted) return;
        _resumeAttempted = true;
        if (string.IsNullOrEmpty(_lastTrackPath)) return;
        TrackInfo? track = null;
        lock (_library)
        {
            track = _library.FirstOrDefault(t => t.Path.Equals(_lastTrackPath, StringComparison.OrdinalIgnoreCase));
        }
        if (track == null) return;
        _resumeStartPosition = _lastPosition;
        PlayTrack(MakeStringElement(track.Id));
    }

    private void LoadAnalysisCache()
    {
        try
        {
            if (!System.IO.File.Exists(AnalysisFilePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, CachedAnalysis>>(System.IO.File.ReadAllText(AnalysisFilePath));
            if (data == null) return;
            lock (_analysisLock)
            {
                _analysisCache.Clear();
                foreach (var kv in data) _analysisCache[kv.Key] = kv.Value;
            }
        }
        catch
        {
        }
    }

    private void SaveAnalysisCache()
    {
        try
        {
            Dictionary<string, CachedAnalysis> snapshot;
            lock (_analysisLock)
            {
                snapshot = new Dictionary<string, CachedAnalysis>(_analysisCache);
            }
            var dir = Path.GetDirectoryName(AnalysisFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(AnalysisFilePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
        }
    }

    private void QueueAnalysis(IReadOnlyList<TrackInfo> tracks)
    {
        if (_analysisRunning) return;
        _analysisRunning = true;
        var copy = tracks.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                bool changed = false;
                foreach (var t in copy)
                {
                    try
                    {
                        var fi = new FileInfo(t.Path);
                        long mtime = fi.LastWriteTimeUtc.Ticks;
                        CachedAnalysis? cached = null;
                        lock (_analysisLock)
                        {
                            if (_analysisCache.TryGetValue(t.Path, out var c) && Math.Abs(c.MtimeTicks - mtime) < 1000)
                                cached = c;
                        }
                        if (cached != null)
                        {
                            t.Bpm = cached.Bpm;
                            t.Energy = cached.Energy;
                            t.LoudnessDb = cached.LoudnessDb;
                            t.PeakDb = cached.PeakDb;
                            continue;
                        }
                        var res = AudioAnalyzer.Analyze(t.Path);
                        if (res == null) continue;
                        var entry = new CachedAnalysis
                        {
                            MtimeTicks = mtime,
                            Bpm = res.Bpm,
                            Energy = res.Energy,
                            LoudnessDb = res.LoudnessDb,
                            PeakDb = res.PeakDb
                        };
                        lock (_analysisLock) { _analysisCache[t.Path] = entry; }
                        t.Bpm = res.Bpm;
                        t.Energy = res.Energy;
                        t.LoudnessDb = res.LoudnessDb;
                        t.PeakDb = res.PeakDb;
                        changed = true;
                    }
                    catch
                    {
                    }
                }
                if (changed) SaveAnalysisCache();
            }
            finally
            {
                _analysisRunning = false;
            }
        });
    }

    private TrackInfo? GetCurrentTrackInfo()
    {
        if (_currentTrackIndex >= 0 && _currentTrackIndex < _currentPlaylist.Tracks.Count)
            return _currentPlaylist.Tracks[_currentTrackIndex];
        return null;
    }

    private float GetEffectiveVolume(TrackInfo? track)
    {
        double v = _isMuted ? 0 : _volume;
        if (_replayGain && track != null && track.LoudnessDb < -1)
        {
            double gainDb = Math.Clamp(-14 - track.LoudnessDb, -12, 12);
            v *= Math.Pow(10, gainDb / 20);
        }
        return (float)Math.Max(0, Math.Min(1.5, v));
    }

    private string SetReplayGain(JsonElement payload)
    {
        bool on = payload.ValueKind == JsonValueKind.True || payload.GetBoolean();
        _replayGain = on;
        SaveSettings();
        // Apply immediately to the current track
        if (_audioFile != null)
        {
            var cur = GetCurrentTrackInfo();
            _audioFile.Volume = GetEffectiveVolume(cur);
        }
        return JsonSerializer.Serialize(new { success = true });
    }

    public event Action<bool>? OnDiscordRpcChanged;

    public bool DiscordRpcEnabled => _discordRpc;

    private string SetDiscordRpc(JsonElement payload)
    {
        bool on = payload.ValueKind == JsonValueKind.True || payload.GetBoolean();
        _discordRpc = on;
        SaveSettings();
        OnDiscordRpcChanged?.Invoke(on);
        return JsonSerializer.Serialize(new { success = true, discordRpc = on });
    }

    private string StartFlow(JsonElement payload)
    {
        string path = GetStringPayload(payload);
        lock (_playbackLock)
        {
            var track = _library.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (track == null)
                return JsonSerializer.Serialize(new { success = false, error = "Track not found" });

            // If the flow base track is already the loaded/playing track, keep playing
            // from the current position instead of restarting it.
            var currentTrack = GetCurrentTrackInfo();
            bool continueCurrent = currentTrack != null
                && _audioFile != null
                && currentTrack.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase);

            _flowActive = true;
            _flowBase = track;
            _flowUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { track.Path };

            var flowTracks = BuildFlowQueue(track, 40, _flowUsed);
            if (flowTracks.Count == 0)
            {
                _flowActive = false;
                return JsonSerializer.Serialize(new { success = false, error = "No similar tracks in library" });
            }

            _currentPlaylist.Id = "flow";
            _currentPlaylist.Name = "Flow";
            _currentPlaylist.Tracks.Clear();
            _currentPlaylist.Tracks.Add(track);
            _currentPlaylist.Tracks.AddRange(flowTracks);
            foreach (var t in flowTracks) _flowUsed.Add(t.Path);

            _currentTrackIndex = 0;
            _isShuffled = false;
            _shuffleOrder.Clear();
            _shuffleIndex = 0;
            _shuffleQueueKey = "";

            if (!continueCurrent)
                TryPlayTrack(track);
            NotifyState();
            return JsonSerializer.Serialize(new { success = true, flow = true, count = _currentPlaylist.Tracks.Count });
        }
    }

    private string StopFlow()
    {
        lock (_playbackLock)
        {
            _flowActive = false;
            _flowUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _flowBase = null;
            // Restore the normal queue (whole library) while keeping the current track
            var tracks = SnapshotLibrary();
            if (tracks.Count == 0)
                return JsonSerializer.Serialize(new { success = true });
            string? currentPath = GetCurrentTrackInfo()?.Path;
            _currentPlaylist.Id = "";
            _currentPlaylist.Name = "All tracks";
            _currentPlaylist.Tracks.Clear();
            _currentPlaylist.Tracks.AddRange(tracks);
            if (currentPath != null)
            {
                _currentTrackIndex = _currentPlaylist.Tracks.FindIndex(t => t.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
                if (_currentTrackIndex < 0) _currentTrackIndex = 0;
            }
            _isShuffled = false;
            RebuildShuffleOrderIfNeeded();
            NotifyState();
            return JsonSerializer.Serialize(new { success = true, flow = false });
        }
    }

    private void EnsureFlowQueue()
    {
        if (!_flowActive) return;
        if (_currentPlaylist.Tracks.Count == 0) return;
        if (_currentTrackIndex < _currentPlaylist.Tracks.Count - 1) return;
        var baseTrack = _flowBase ?? _currentPlaylist.Tracks[Math.Max(0, _currentTrackIndex)];
        var more = BuildFlowQueue(baseTrack, 40, _flowUsed);
        foreach (var t in more)
        {
            _flowUsed.Add(t.Path);
            _currentPlaylist.Tracks.Add(t);
        }
    }

    private List<TrackInfo> BuildFlowQueue(TrackInfo baseTrack, int count, HashSet<string> exclude)
    {
        List<TrackInfo> candidates;
        lock (_library)
        {
            candidates = _library.Where(t => !exclude.Contains(t.Path)).ToList();
        }
        if (candidates.Count == 0) return new List<TrackInfo>();
        var scored = candidates
            .Select(t => new { Track = t, Score = FlowScore(baseTrack, t) })
            .OrderByDescending(x => x.Score)
            .Take(count)
            .Select(x => x.Track)
            .ToList();
        // Light randomization among the top picks for a flowing, less predictable sequence
        return scored.OrderBy(_ => _random.Next()).ToList();
    }

    private double FlowScore(TrackInfo baseTrack, TrackInfo t)
    {
        double score = 0;
        if (baseTrack.Energy > 0 && t.Energy > 0)
            score += 1.0 - Math.Abs(baseTrack.Energy - t.Energy);
        else
            score += 0.5;
        if (baseTrack.Bpm > 0 && t.Bpm > 0)
            score += 0.7 * (1.0 - Math.Min(1, Math.Abs(baseTrack.Bpm - t.Bpm) / 40.0));
        else
            score += 0.3;
        if (baseTrack.Artist != "Unknown" && string.Equals(baseTrack.Artist, t.Artist, StringComparison.OrdinalIgnoreCase))
            score += 1.5;
        else if (baseTrack.Album != "Unknown" && string.Equals(baseTrack.Album, t.Album, StringComparison.OrdinalIgnoreCase))
            score += 1.0;
        return score;
    }

    private string GetSettings()
    {
        return JsonSerializer.Serialize(new
        {
            success = true,
            theme = _theme,
            volume = _volume,
            loopMode = (int)_loopMode,
            isShuffled = _isShuffled,
            eqEnabled = EqualizerSettings.IsEnabled,
            eqPreset = EqualizerSettings.CurrentPreset,
            eqGains = EqualizerSettings.CustomGains,
            crossfade = _crossfadeDuration,
            replayGain = _replayGain,
            flowActive = _flowActive,
            visualizerOn = _vizMode != 0,
            discordRpc = _discordRpc
        });
    }

    private string SetTheme(JsonElement payload)
    {
        _theme = GetStringPayload(payload);
        if (string.IsNullOrEmpty(_theme)) _theme = "default";
        SaveSettings();
        return JsonSerializer.Serialize(new { success = true });
    }

    private string GetLibrary()
    {
        return JsonSerializer.Serialize(new { success = true, tracks = SnapshotLibrary() });
    }

    private string GetPlaylists()
    {
        return JsonSerializer.Serialize(new { success = true, playlists = _playlists.Values.ToList() });
    }

    private string CreatePlaylist(JsonElement payload)
    {
        PlaylistData? data;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            data = JsonSerializer.Deserialize<PlaylistData>(payload.GetRawText());
        }
        else
        {
            data = new PlaylistData { Name = GetStringPayload(payload) };
        }

        if (data == null || string.IsNullOrEmpty(data.Name))
            return JsonSerializer.Serialize(new { success = false, error = "Playlist name required" });

        data.Id = Guid.NewGuid().ToString();
        data.Tracks = new List<TrackInfo>();
        _playlists[data.Id] = data;
        OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
        SavePlaylists();
        return JsonSerializer.Serialize(new { success = true, playlist = data });
    }

    private string RenamePlaylist(JsonElement payload)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(payload.GetRawText());
        if (data != null && data.TryGetValue("playlistId", out var id) &&
            data.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name) &&
            _playlists.TryGetValue(id, out var playlist))
        {
            playlist.Name = name.Trim();
            OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
            SavePlaylists();
            return JsonSerializer.Serialize(new { success = true });
        }
        return JsonSerializer.Serialize(new { success = false, error = "Failed to rename playlist" });
    }

    private string DeletePlaylist(JsonElement payload)
    {
        var id = GetStringPayload(payload);
        if (_playlists.Remove(id))
        {
            OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
            SavePlaylists();
        }
        return JsonSerializer.Serialize(new { success = true });
    }

    private string AddToPlaylist(JsonElement payload)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(payload.GetRawText());
        Log("ADD", payload.GetRawText());
        if (data != null && _playlists.TryGetValue(data["playlistId"], out var playlist))
        {
            var track = SnapshotLibrary().FirstOrDefault(t => t.Id == data["trackId"]);
            Log("ADD", $"foundTrack={track?.Path ?? "<null>"} libraryCount={_library.Count} playlistTrackCount={playlist.Tracks.Count}");
            if (track != null && !playlist.Tracks.Any(t => t.Id == track.Id))
            {
                playlist.Tracks.Add(track);
                OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
                SavePlaylists();
                return JsonSerializer.Serialize(new { success = true });
            }
        }
        return JsonSerializer.Serialize(new { success = false, error = "Failed to add to playlist" });
    }

    private string RemoveFromPlaylist(JsonElement payload)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(payload.GetRawText());
        if (data != null && _playlists.TryGetValue(data["playlistId"], out var playlist))
        {
            playlist.Tracks.RemoveAll(t => t.Id == data["trackId"]);
            OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
            SavePlaylists();
            return JsonSerializer.Serialize(new { success = true });
        }
        return JsonSerializer.Serialize(new { success = false, error = "Failed to remove from playlist" });
    }

    private string ReorderPlaylist(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return JsonSerializer.Serialize(new { success = false, error = "Invalid payload" });
        if (!payload.TryGetProperty("playlistId", out var idEl) || !payload.TryGetProperty("trackIds", out var idsEl))
            return JsonSerializer.Serialize(new { success = false, error = "Invalid payload" });

        var playlistId = idEl.GetString();
        List<string>? trackIds = null;
        if (idsEl.ValueKind == JsonValueKind.Array)
            trackIds = idsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

        if (string.IsNullOrEmpty(playlistId) || trackIds == null || !_playlists.TryGetValue(playlistId, out var playlist))
            return JsonSerializer.Serialize(new { success = false, error = "Playlist not found" });

        var ordered = new List<TrackInfo>();
        var remaining = new List<TrackInfo>(playlist.Tracks);
        foreach (var id in trackIds)
        {
            var t = remaining.FirstOrDefault(x => x.Id == id);
            if (t == null) continue;
            ordered.Add(t);
            remaining.Remove(t);
        }
        ordered.AddRange(remaining); // keep any tracks the client omitted

        playlist.Tracks = ordered;
        OnPlaylistsChanged?.Invoke(_playlists.Values.ToList());
        SavePlaylists();
        return JsonSerializer.Serialize(new { success = true });
    }

    private async Task<string> ScanFolder(JsonElement payload)
    {
        var folder = GetStringPayload(payload);
        if (string.IsNullOrEmpty(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        if (!_libraryDirectories.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _libraryDirectories.Add(folder);
            SaveSettings();
        }

        await Task.Run(() => ScanDirectory(folder));

        // Populate current playlist with all library tracks
        var tracks = SnapshotLibrary();
        _currentPlaylist.Tracks.Clear();
        _currentPlaylist.Tracks.AddRange(tracks);

        RefreshPlaylistTracks();
        OnLibraryChanged?.Invoke(tracks);
        return JsonSerializer.Serialize(new { success = true, tracks });
    }

    private async Task<string> RescanAll()
    {
        _library.Clear();
        _currentPlaylist.Tracks.Clear();
        lock (_pendingFilesLock) { _pendingFiles.Clear(); _pendingRetries.Clear(); }

        foreach (var dir in _libraryDirectories)
        {
            await Task.Run(() => ScanDirectory(dir));
        }
        var tracks = SnapshotLibrary();
        _currentPlaylist.Tracks.Clear();
        _currentPlaylist.Tracks.AddRange(tracks);

        RefreshPlaylistTracks();
        OnLibraryChanged?.Invoke(tracks);
        StartFolderWatchers();
        ResumePlaybackIfNeeded();
        return JsonSerializer.Serialize(new { success = true, tracks });
    }

    public void StartFolderWatchers()
    {
        StopFolderWatchers();
        foreach (var dir in _libraryDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            watcher.Created += OnFolderFileEvent;
            watcher.Changed += OnFolderFileEvent;
            watcher.Renamed += OnFolderRenamedEvent;
            watcher.Deleted += OnFolderDeletedEvent;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void StopFolderWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    private void OnFolderFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!_supportedExtensions.Contains(Path.GetExtension(e.FullPath))) return;
        if (e.ChangeType == WatcherChangeTypes.Changed && !System.IO.File.Exists(e.FullPath)) return;
        lock (_pendingFilesLock) _pendingFiles.Add(e.FullPath);
        SchedulePendingScan();
    }

    private void OnFolderRenamedEvent(object sender, RenamedEventArgs e)
    {
        if (!_supportedExtensions.Contains(Path.GetExtension(e.FullPath))) return;
        lock (_pendingFilesLock)
        {
            _pendingFiles.Remove(e.OldFullPath);
            _pendingFiles.Add(e.FullPath);
        }
        SchedulePendingScan();
    }

    private void OnFolderDeletedEvent(object sender, FileSystemEventArgs e)
    {
        lock (_library)
        {
            _library.RemoveAll(t => t.Path.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase));
        }
        if (_favorites.Remove(e.FullPath))
            SaveFavorites();
        RefreshPlaylistTracks();
        OnLibraryChanged?.Invoke(SnapshotLibrary());
    }

    private void SchedulePendingScan()
    {
        lock (_pendingFilesLock)
        {
            _scanDebounceTimer?.Dispose();
            _scanDebounceTimer = new System.Threading.Timer(_ => ProcessPendingFiles(), null, 1500, Timeout.Infinite);
        }
    }

    private void ProcessPendingFiles()
    {
        List<string> files;
        lock (_pendingFilesLock)
        {
            files = _pendingFiles.ToList();
            _pendingFiles.Clear();
        }
        if (files.Count == 0) return;

        var added = new List<TrackInfo>();
        var retry = new List<string>();
        lock (_library)
        {
            foreach (var f in files)
            {
                if (!System.IO.File.Exists(f)) continue;
                if (_library.Any(t => t.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    var track = LoadTrackMetadata(f);
                    // File may still be being copied (tags/duration unreadable yet) - retry shortly
                    if (track.Artist == "Unknown" || track.Album == "Unknown" || track.Duration <= 0)
                    {
                        retry.Add(f);
                        continue;
                    }
                    _library.Add(track);
                    added.Add(track);
                }
                catch
                {
                    retry.Add(f);
                }
            }
        }

        if (retry.Count > 0)
        {
            lock (_pendingFilesLock)
            {
                foreach (var f in retry)
                {
                    var n = _pendingRetries.TryGetValue(f, out var c) ? c : 0;
                    if (n >= MaxPendingRetries)
                    {
                        _pendingRetries.Remove(f);
                        // Give up: add as-is so genuinely-untagged files still appear
                        lock (_library)
                        {
                            if (_library.Any(t => t.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                            try
                            {
                                var t = LoadTrackMetadata(f);
                                _library.Add(t);
                                added.Add(t);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        _pendingRetries[f] = n + 1;
                        _pendingFiles.Add(f);
                    }
                }
            }
            SchedulePendingScan();
        }

        if (added.Count == 0) return;
        RefreshPlaylistTracks();
        OnLibraryChanged?.Invoke(SnapshotLibrary());
        QueueAnalysis(added);
    }

    private string SetSleepTimer(JsonElement payload)
    {
        lock (_playbackLock)
        {
            double minutes = payload.ValueKind == JsonValueKind.Number ? payload.GetDouble() : 0;
            _sleepTimer?.Stop();
            _sleepTimer?.Dispose();
            _sleepTimer = null;

            if (minutes > 0)
            {
                _sleepTimer = new System.Timers.Timer(minutes * 60 * 1000);
                _sleepTimer.AutoReset = false;
                _sleepTimer.Elapsed += (_, _) =>
                {
                    lock (_playbackLock) { Stop(); }
                    NotifyState();
                };
                _sleepTimer.Start();
            }

            return JsonSerializer.Serialize(new { success = true, minutes });
        }
    }

    private string SetCrossfade(JsonElement payload)
    {
        lock (_playbackLock)
        {
            double seconds = payload.ValueKind == JsonValueKind.Number ? payload.GetDouble() : 0;
            _crossfadeDuration = Math.Max(0, Math.Min(10, seconds));
            SaveSettings();
            return JsonSerializer.Serialize(new { success = true, crossfade = _crossfadeDuration });
        }
    }

    private string GetLyrics(JsonElement payload)
    {
        string path;
        string artist = "";
        string title = "";
        string reqToken = "";
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("path", out var p)) path = p.GetString() ?? "";
            else path = GetStringPayload(payload);
            if (payload.TryGetProperty("artist", out var a)) artist = a.GetString() ?? "";
            if (payload.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
            if (payload.TryGetProperty("reqToken", out var r)) reqToken = r.GetString() ?? "";
        }
        else
        {
            path = GetStringPayload(payload);
        }

        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return JsonSerializer.Serialize(new { success = false, error = "Track file not found", reqToken });

        var lrcLines = LyricsService.LoadLrcFile(path);
        if (lrcLines != null && lrcLines.Count > 0)
            return JsonSerializer.Serialize(new { success = true, lyrics = lrcLines, synced = true, source = "lrc", reqToken });

        var embedded = LyricsService.LoadEmbeddedLyrics(path);
        if (!string.IsNullOrEmpty(embedded))
            return JsonSerializer.Serialize(new { success = true, lyrics = new List<LrcLine> { new() { Time = 0, Text = embedded } }, synced = false, source = "embedded", reqToken });

        var cached = LyricsService.LoadCache(artist, title);
        if (cached != null)
            return JsonSerializer.Serialize(new { success = true, lyrics = cached.Value.lines, synced = cached.Value.synced, source = "cache", reqToken });

        return JsonSerializer.Serialize(new { success = true, lyrics = new List<LrcLine>(), synced = false, source = "none", reqToken });
    }

    private async Task<string> FetchOnlineLyrics(JsonElement payload)
    {
        string artist = "";
        string title = "";
        string reqToken = "";
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("artist", out var a)) artist = a.GetString() ?? "";
            if (payload.TryGetProperty("title", out var t)) title = t.GetString() ?? "";
            if (payload.TryGetProperty("reqToken", out var r)) reqToken = r.GetString() ?? "";
        }
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return JsonSerializer.Serialize(new { success = false, error = "Missing artist or title", reqToken });

        var result = await FetchLyricsFromLrclib(artist, title);
        var source = "lrclib";
        if (result == null)
        {
            result = await FetchLyricsFromYandex(artist, title);
            source = "yandex";
        }
        if (result == null)
        {
            result = await FetchLyricsFromNetEase(artist, title);
            source = "netease";
        }

        if (result == null)
            return JsonSerializer.Serialize(new { success = false, error = "Not found", lyrics = new List<LrcLine>(), synced = false, source = "none", reqToken });

        LyricsService.SaveCache(artist, title, result.Value.lines, result.Value.synced);
        return JsonSerializer.Serialize(new { success = true, lyrics = result.Value.lines, synced = result.Value.synced, source, reqToken });
    }

    private async Task<(List<LrcLine> lines, bool synced)?> FetchLyricsFromLrclib(string artist, string title)
    {
        var exact = await FetchLrclibHit(artist, title, "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(artist) + "&track_name=" + Uri.EscapeDataString(title));
        if (exact != null) return exact;

        return await FetchLrclibHit(artist, title, "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(artist + " " + title));
    }

    private async Task<(List<LrcLine> lines, bool synced)?> FetchLrclibHit(string artist, string title, string url)
    {
        try
        {
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var normArtist = artist.Trim().ToLowerInvariant();
            var normTitle = title.Trim().ToLowerInvariant();

            var elements = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                elements.AddRange(doc.RootElement.EnumerateArray());
            }
            else
            {
                elements.Add(doc.RootElement.Clone());
            }

            foreach (var el in elements)
            {
                var elArtist = (el.TryGetProperty("artistName", out var aEl) ? aEl.GetString() : "") ?? "";
                var elTitle = (el.TryGetProperty("trackName", out var tEl) ? tEl.GetString() : "") ?? "";
                var elArtistNorm = elArtist.Trim().ToLowerInvariant();
                var elTitleNorm = elTitle.Trim().ToLowerInvariant();

                if (url.Contains("/api/search"))
                {
                    var artistMatches = elArtistNorm.Length > 0 && normArtist.Length > 0 &&
                                        (elArtistNorm.Contains(normArtist) || normArtist.Contains(elArtistNorm));
                    var titleMatches = elTitleNorm.Length > 0 && normTitle.Length > 0 &&
                                       (elTitleNorm.Contains(normTitle) || normTitle.Contains(elTitleNorm));
                    if (!artistMatches || !titleMatches) continue;
                }

                List<LrcLine>? lyrics = null;
                var synced = false;
                if (el.TryGetProperty("syncedLyrics", out var syncedEl) &&
                    syncedEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(syncedEl.GetString()))
                {
                    lyrics = LyricsService.ParseLrc(syncedEl.GetString()!);
                    synced = true;
                }

                if (lyrics == null && el.TryGetProperty("plainLyrics", out var plainEl) &&
                    plainEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(plainEl.GetString()))
                {
                    lyrics = new List<LrcLine> { new() { Time = 0, Text = plainEl.GetString()! } };
                }

                if (lyrics != null && lyrics.Count > 0)
                    return (lyrics, synced);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(List<LrcLine> lines, bool synced)?> FetchLyricsFromYandex(string artist, string title)
    {
        try
        {
            var headers = new Dictionary<string, string>
            {
                ["X-Yandex-Music-Client"] = "YandexMusicAndroid/5.36.2 (Android 13)"
            };

            var searchUrl = "https://api.music.yandex.net/search?text=" + Uri.EscapeDataString(artist + " " + title) + "&type=track&page=0";
            using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            foreach (var kv in headers) searchReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            var searchResp = await _http.SendAsync(searchReq);
            searchResp.EnsureSuccessStatusCode();
            using var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());

            if (!searchDoc.RootElement.TryGetProperty("result", out var resultEl) ||
                !resultEl.TryGetProperty("tracks", out var tracksEl) ||
                !tracksEl.TryGetProperty("results", out var resultsEl) ||
                resultsEl.GetArrayLength() == 0)
                return null;

            var trackId = resultsEl[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(trackId)) return null;

            var supplementUrl = "https://api.music.yandex.net/tracks/" + Uri.EscapeDataString(trackId) + "/supplement";
            using var supplementReq = new HttpRequestMessage(HttpMethod.Get, supplementUrl);
            foreach (var kv in headers) supplementReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            var supplementResp = await _http.SendAsync(supplementReq);
            supplementResp.EnsureSuccessStatusCode();
            using var supplementDoc = JsonDocument.Parse(await supplementResp.Content.ReadAsStringAsync());

            if (!supplementDoc.RootElement.TryGetProperty("result", out var supResult) ||
                !supResult.TryGetProperty("lyrics", out var lyricsEl) ||
                lyricsEl.ValueKind != JsonValueKind.Object ||
                !lyricsEl.TryGetProperty("fullLyrics", out var fullEl) ||
                fullEl.ValueKind != JsonValueKind.String)
                return null;

            var fullText = fullEl.GetString();
            if (string.IsNullOrWhiteSpace(fullText)) return null;

            var lines = fullText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return null;

            var hasTimestamps = lyricsEl.TryGetProperty("timestamps", out var tsEl) &&
                                tsEl.ValueKind == JsonValueKind.Array && tsEl.GetArrayLength() > 0;

            if (hasTimestamps)
            {
                var tsArr = tsEl.EnumerateArray().ToList();
                var syncedLines = new List<LrcLine>();
                for (int i = 0; i < lines.Count && i < tsArr.Count; i++)
                {
                    var ts = tsArr[i];
                    if (ts.ValueKind != JsonValueKind.Array || ts.GetArrayLength() == 0) continue;
                    var start = ts[0].GetDouble();
                    if (start > 1000) start /= 1000.0;
                    syncedLines.Add(new LrcLine { Time = start, Text = lines[i] });
                }
                syncedLines = syncedLines.OrderBy(l => l.Time).ToList();
                if (syncedLines.Count > 0)
                    return (syncedLines, true);
            }

            return (new List<LrcLine> { new() { Time = 0, Text = string.Join("\n", lines) } }, false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(List<LrcLine> lines, bool synced)?> FetchLyricsFromNetEase(string artist, string title)
    {
        try
        {
            var headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://music.163.com/",
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"
            };

            foreach (var query in new[] { title + " " + artist, artist + " " + title })
            {
                var searchUrl = "https://music.163.com/api/cloudsearch/pc?type=1&limit=5&s=" + Uri.EscapeDataString(query);
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                foreach (var kv in headers) searchReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                var searchResp = await _http.SendAsync(searchReq);
                searchResp.EnsureSuccessStatusCode();
                using var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());

                if (!searchDoc.RootElement.TryGetProperty("result", out var resultEl) ||
                    !resultEl.TryGetProperty("songs", out var songsEl) ||
                    songsEl.GetArrayLength() == 0)
                    continue;

                foreach (var song in songsEl.EnumerateArray())
                {
                    if (!song.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                        continue;
                    var songId = idEl.GetInt64();

                    var lyricUrl = "https://music.163.com/api/song/lyric?id=" + songId + "&lv=1";
                    using var lyricReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                    foreach (var kv in headers) lyricReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    var lyricResp = await _http.SendAsync(lyricReq);
                    lyricResp.EnsureSuccessStatusCode();
                    using var lyricDoc = JsonDocument.Parse(await lyricResp.Content.ReadAsStringAsync());

                    if (!lyricDoc.RootElement.TryGetProperty("lrc", out var lrcEl) ||
                        lrcEl.ValueKind != JsonValueKind.Object ||
                        !lrcEl.TryGetProperty("lyric", out var lyricEl) ||
                        lyricEl.ValueKind != JsonValueKind.String)
                        continue;

                    var text = lyricEl.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var parsed = LyricsService.ParseLrc(text);
                    if (parsed != null && parsed.Count > 0)
                    {
                        var synced = parsed.Count > 1 || parsed[0].Time > 0;
                        return (parsed, synced);
                    }
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static void EnsureAccent(TrackInfo track)
    {
        if (track == null || !string.IsNullOrEmpty(track.Accent)) return;
        try
        {
            if (!track.CoverPath.StartsWith("https://covers.localhost/")) return;
            var id = track.CoverPath.Substring("https://covers.localhost/".Length).Replace(".jpg", "");
            var coverFile = Path.Combine(Path.GetTempPath(), "GlassMusicPlayer", "covers", id + ".jpg");
            if (System.IO.File.Exists(coverFile))
                track.Accent = GetDominantAccent(System.IO.File.ReadAllBytes(coverFile));
        }
        catch
        {
        }
    }

    private static string? GetDominantAccent(byte[] imageData)
    {
        try
        {
            using var ms = new MemoryStream(imageData);
            using var src = new Bitmap(ms);
            using var small = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, 32, 32);
            }

            double r = 0, gg = 0, b = 0;
            int count = 0;
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    var px = small.GetPixel(x, y);
                    if (px.A < 128) continue;
                    r += px.R; gg += px.G; b += px.B; count++;
                }
            }
            if (count == 0) return null;

            double ar = r / count / 255.0, ag = gg / count / 255.0, ab = b / count / 255.0;

            double max = Math.Max(ar, Math.Max(ag, ab));
            double min = Math.Min(ar, Math.Min(ag, ab));
            double l = (max + min) / 2.0;
            double s = max == min ? 0 : (max - min) / (1 - Math.Abs(2 * l - 1));
            double h;
            if (max == min) h = 0;
            else if (max == ar) h = 60 * (((ag - ab) / (max - min)) % 6);
            else if (max == ag) h = 60 * (((ab - ar) / (max - min)) + 2);
            else h = 60 * (((ar - ag) / (max - min)) + 4);
            if (h < 0) h += 360;

            double targetSat = Math.Min(Math.Max(s, 0.45), 0.85);
            double targetL = Math.Clamp(l, 0.42, 0.62);

            if (s < 0.12)
            {
                int gr = (int)Math.Round(targetL * 255);
                return $"#{gr:X2}{gr:X2}{gr:X2}";
            }

            var (cr, cg, cb) = HslToRgb(h, targetSat, targetL);
            return $"#{cr:X2}{cg:X2}{cb:X2}";
        }
        catch
        {
            return null;
        }
    }

    private static (int R, int G, int B) HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        if (hp < 1) { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else { r = c; b = x; }
        double m = l - c / 2;
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    private static JsonElement MakeStringElement(string value)
    {
        using var doc = JsonDocument.Parse("\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        return doc.RootElement.Clone();
    }

    private async Task<string> OpenFolderDialog()
    {
        try
        {
            if (OnOpenFolderDialogRequest != null)
            {
                var path = await OnOpenFolderDialogRequest();
                if (!string.IsNullOrEmpty(path))
                {
                    return JsonSerializer.Serialize(new { success = true, path });
                }
                return JsonSerializer.Serialize(new { success = false, error = "Canceled" });
            }
            return JsonSerializer.Serialize(new { success = false, error = "No dialog handler" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private async Task<string> GetFileTags(JsonElement payload)
    {
        var filePath = GetStringPayload(payload);
        return await Task.Run(() =>
        {
            try
            {
                var track = LoadTrackMetadata(filePath ?? "");
                return JsonSerializer.Serialize(new { success = true, track });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        });
    }

    private void NotifyState()
    {
        var currentTrack = _currentTrackIndex >= 0 && _currentTrackIndex < _currentPlaylist.Tracks.Count
            ? _currentPlaylist.Tracks[_currentTrackIndex] : null;

        OnStateChanged?.Invoke(new PlayerState
        {
            IsPlaying = _isPlaying,
            CurrentTime = _audioFile?.CurrentTime.TotalSeconds ?? 0,
            Duration = _audioFile?.TotalTime.TotalSeconds ?? 0,
            Volume = _volume,
            IsMuted = _isMuted,
            CurrentTrack = currentTrack,
            LoopMode = _loopMode,
            IsShuffled = _isShuffled
        });
    }

    public void Dispose()
    {
        PersistPlaybackState();
        _positionTimer.Dispose();
        _visualizationTimer.Dispose();
        _crossfadeTimer.Dispose();
        _sleepTimer?.Dispose();
        CleanupIncoming();
        StopFolderWatchers();
        _outputDevice?.Stop();
        _outputDevice?.Dispose();
        _visualizingProvider?.Dispose();
        _equalizerService?.Dispose();
        _audioFile?.Dispose();
    }
}