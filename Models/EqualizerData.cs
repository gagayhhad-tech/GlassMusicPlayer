using System.Text.Json.Serialization;

namespace GlassMusicPlayer.Models;

public class EqualizerPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("gains")]
    public float[] Gains { get; set; } = new float[10]; // 10 bands: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz
    [JsonPropertyName("isFlat")]
    public bool IsFlat => Name == "Flat";
}

public class EqualizerSettings
{
    public bool IsEnabled { get; set; }
    public float[] CustomGains { get; set; } = new float[10];
    public string CurrentPreset { get; set; } = "Flat";

    public static readonly Dictionary<string, float[]> Presets = new()
    {
        ["Flat"] = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["Rock"] = new float[] { 4, 3, 2, 1, 0, 0, 1, 2, 3, 4 },
        ["Pop"] = new float[] { -1, 0, 2, 3, 2, 1, 0, -1, -1, -1 },
        ["Jazz"] = new float[] { 3, 2, 1, 1, 0, 0, 1, 2, 3, 4 },
        ["Classical"] = new float[] { 4, 3, 2, 1, 0, 1, 2, 3, 4, 5 },
        ["Bass Boost"] = new float[] { 6, 5, 4, 2, 1, 0, -1, -2, -2, -3 },
    };
}

public static class EqualizerPresets
{
    public static List<EqualizerPreset> GetAll()
    {
        var presets = new List<EqualizerPreset>();
        foreach (var kvp in EqualizerSettings.Presets)
        {
            presets.Add(new EqualizerPreset
            {
                Name = kvp.Key,
                Gains = (float[])kvp.Value.Clone()
            });
        }
        return presets;
    }

    public static EqualizerPreset? GetPreset(string name)
    {
        if (EqualizerSettings.Presets.TryGetValue(name, out var gains))
        {
            return new EqualizerPreset { Name = name, Gains = (float[])gains.Clone() };
        }
        return null;
    }

    public static float[] GetFlatGains()
    {
        return new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }
}

public static class PresetFrequencyLabels
{
    public static readonly string[] Labels = { "31Hz", "62Hz", "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz", "8kHz", "16kHz" };
}