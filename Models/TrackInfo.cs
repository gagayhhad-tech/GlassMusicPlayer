namespace GlassMusicPlayer.Models;

public class TrackInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Unknown";
    public string Artist { get; set; } = "Unknown";
    public string Album { get; set; } = "Unknown";
    public string Path { get; set; } = "";
    public double Duration { get; set; }
    public string Format { get; set; } = "";
    public long Size { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string CoverPath { get; set; } = "";
    public string? Accent { get; set; }
    public double Bpm { get; set; }
    public double Energy { get; set; }
    public double LoudnessDb { get; set; }
    public double PeakDb { get; set; }
    public uint TrackNumber { get; set; }
}

public class PlayerState
{
    public bool IsPlaying { get; set; }
    public double CurrentTime { get; set; }
    public double Duration { get; set; }
    public double Volume { get; set; } = 1.0;
    public bool IsMuted { get; set; }
    public TrackInfo? CurrentTrack { get; set; }
    public LoopMode LoopMode { get; set; } = LoopMode.None;
    public bool IsShuffled { get; set; }
}

public enum LoopMode
{
    None,
    All,
    One
}

public class AudioVisualizationData
{
    public float[] Waveform { get; set; } = Array.Empty<float>();
    public float[] Spectrum { get; set; } = Array.Empty<float>();
    public float RMS { get; set; }
    public float Peak { get; set; }
    public float[] Bands { get; set; } = Array.Empty<float>();
    public float Amplitude { get; set; }
    public bool IsActive { get; set; }
}

public class PlaylistData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = new();
}

public class IpcMessage
{
    public string Type { get; set; } = "";
    public System.Text.Json.JsonElement Payload { get; set; }
}

public class ScanStatusData
{
    public bool IsScanning { get; set; }
    public string? CurrentFolder { get; set; }
    public int FilesFound { get; set; }
}
