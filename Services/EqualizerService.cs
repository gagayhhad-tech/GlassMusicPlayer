using NAudio.Wave;
using GlassMusicPlayer.Models;

namespace GlassMusicPlayer.Services;

/// <summary>
/// ISampleProvider that applies 10-band equalizer using biquad filters
/// Bands: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz
/// </summary>
public class EqualizerService : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[,] _filters; // [channel, band]
    private readonly int _channels;
    private readonly int _sampleRate;
    private float[] _gains = new float[10];
    private bool _enabled;

    // Center frequencies for each band
    private static readonly double[] BandFrequencies =
    {
        31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    };

    // Q factors (bandwidth) - wider for lower bands, narrower for higher
    private static readonly double[] BandQ =
    {
        0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 0.7
    };

    public EqualizerSettings Settings { get; private set; } = new();

    public EqualizerService(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _filters = new BiQuadFilter[_channels, 10];

        for (int ch = 0; ch < _channels; ch++)
        {
            for (int b = 0; b < 10; b++)
            {
                _filters[ch, b] = BiQuadFilter.PeakingEQ(_sampleRate, BandFrequencies[b], BandQ[b], 0);
            }
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void SetGains(float[] gains, bool enabled)
    {
        if (gains == null || gains.Length != 10) return;

        bool changed = enabled != _enabled;
        for (int i = 0; i < 10; i++)
        {
            if (Math.Abs(_gains[i] - gains[i]) > 0.01f)
                changed = true;
            _gains[i] = gains[i];
        }
        _enabled = enabled;

        if (!changed) return;

        // Rebuild filters with new gains
        for (int ch = 0; ch < _channels; ch++)
        {
            for (int b = 0; b < 10; b++)
            {
                float gainDb = _enabled ? _gains[b] : 0;
                _filters[ch, b] = BiQuadFilter.PeakingEQ(_sampleRate, BandFrequencies[b], BandQ[b], gainDb);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (!_enabled || samplesRead <= 0) return samplesRead;

        // Apply filters sample by sample, channel by channel
        for (int i = 0; i < samplesRead; i++)
        {
            int ch = i % _channels;
            float sample = buffer[offset + i];

            for (int b = 0; b < 10; b++)
            {
                sample = _filters[ch, b].Transform(sample);
            }

            // Soft limit to prevent clipping
            if (sample > 1.0f) sample = (sample + 1.0f) / 2.0f;
            if (sample < -1.0f) sample = (sample - 1.0f) / 2.0f;

            buffer[offset + i] = sample;
        }

        return samplesRead;
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Simple biquad filter implementation for peaking EQ
/// </summary>
internal class BiQuadFilter
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _x1, _x2, _y1, _y2;

    private BiQuadFilter() { }

    public static BiQuadFilter PeakingEQ(int sampleRate, double freq, double q, double gainDb)
    {
        var filter = new BiQuadFilter();
        filter.SetPeakingEQ(sampleRate, freq, q, gainDb);
        return filter;
    }

    private void SetPeakingEQ(int sampleRate, double freq, double q, double gainDb)
    {
        // Peaking EQ coefficients per RBJ Audio EQ Cookbook
        double a = Math.Pow(10, gainDb / 40); // amplitude A
        double omega = 2 * Math.PI * freq / sampleRate;
        double alpha = Math.Sin(omega) / (2 * q);
        double cosOmega = Math.Cos(omega);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cosOmega;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cosOmega;
        double a2 = 1 - alpha / a;

        // Normalize by a0
        _a0 = b0 / a0;
        _a1 = b1 / a0;
        _a2 = b2 / a0;
        _b1 = a1 / a0;
        _b2 = a2 / a0;

        _x1 = _x2 = _y1 = _y2 = 0;
    }

    public float Transform(float sample)
    {
        double x0 = sample;
        double y0 = _a0 * x0 + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;
        
        _x2 = _x1;
        _x1 = x0;
        _y2 = _y1;
        _y1 = y0;
        
        return (float)y0;
    }
}