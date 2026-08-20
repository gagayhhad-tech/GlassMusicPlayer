using NAudio.Wave;

namespace GlassMusicPlayer.Services;

/// <summary>
/// ISampleProvider that wraps an AudioFileReader, forwards all samples to the output device,
/// and maintains a ring buffer of recent samples for visualization purposes (non-destructive read).
/// </summary>
public class VisualizingSampleProvider : ISampleProvider
{
    private readonly AudioFileReader _source;
    private readonly float[] _ringBuffer;
    private int _writePos;
    private int _availableSamples;
    private readonly object _lock = new();
    private const int BufferSize = 2048; // 2 * FftSize for safety

    public VisualizingSampleProvider(AudioFileReader source)
    {
        _source = source;
        _ringBuffer = new float[BufferSize];
        _writePos = 0;
        _availableSamples = 0;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        if (samplesRead > 0)
        {
            lock (_lock)
            {
                for (int i = 0; i < samplesRead; i++)
                {
                    _ringBuffer[_writePos] = buffer[offset + i];
                    _writePos = (_writePos + 1) % BufferSize;
                    if (_availableSamples < BufferSize)
                        _availableSamples++;
                }
            }
        }

        return samplesRead;
    }

    /// <summary>
    /// Gets a copy of the most recent samples for visualization.
    /// This is a non-destructive read — the samples remain in the ring buffer.
    /// </summary>
    public void GetSamples(out float[] buffer, out int count)
    {
        lock (_lock)
        {
            count = Math.Min(_availableSamples, 1024); // FftSize
            buffer = new float[count];

            if (count == 0)
                return;

            int readStart = (_writePos - count + BufferSize) % BufferSize;
            for (int i = 0; i < count; i++)
            {
                buffer[i] = _ringBuffer[(readStart + i) % BufferSize];
            }
        }
    }

    public void Dispose()
    {
        _source?.Dispose();
    }
}