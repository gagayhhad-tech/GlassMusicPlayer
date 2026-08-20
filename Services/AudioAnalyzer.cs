using NAudio.Wave;

namespace GlassMusicPlayer.Services;

/// <summary>
/// Lightweight offline audio analysis: tempo (BPM), energy and loudness.
/// Reads at most a bounded window of each file, downsamples to mono 11 kHz,
/// and runs energy-based onset detection + autocorrelation for BPM.
/// </summary>
public sealed class AudioAnalysisResult
{
    public double Bpm { get; set; }
    public double Energy { get; set; }
    public double LoudnessDb { get; set; }
    public double PeakDb { get; set; }
}

public static class AudioAnalyzer
{
    private const int TargetSampleRate = 11025;
    private const int FrameSize = 1024;

    /// <summary>
    /// Analyzes the audio file. Returns null when the file cannot be read or is too short.
    /// </summary>
    public static AudioAnalysisResult? Analyze(string path, int maxSeconds = 90)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            var sr = reader.WaveFormat.SampleRate;
            var channels = reader.WaveFormat.Channels;
            if (sr <= 0 || channels <= 0) return null;

            var mono = new List<float>(maxSeconds * sr);
            var buf = new float[sr * channels];
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i + channels <= read; i += channels)
                {
                    float s = 0f;
                    for (int c = 0; c < channels; c++) s += buf[i + c];
                    mono.Add(s / channels);
                }
                if (mono.Count >= maxSeconds * sr) break;
            }
            if (mono.Count < TargetSampleRate) return null; // too short to analyze

            var samples = Downsample(mono, sr, TargetSampleRate);
            if (samples.Count < TargetSampleRate) return null;

            int frameCount = samples.Count / FrameSize;
            if (frameCount < 8) return null;

            var rms = new double[frameCount];
            double sumSq = 0;
            long totalN = 0;
            for (int i = 0; i < frameCount; i++)
            {
                double s = 0;
                for (int j = 0; j < FrameSize; j++)
                {
                    double v = samples[i * FrameSize + j];
                    s += v * v;
                }
                rms[i] = Math.Sqrt(s / FrameSize);
                sumSq += s;
                totalN += FrameSize;
            }

            double loudnessDb = 20.0 * Math.Log10(Math.Sqrt(sumSq / Math.Max(1, totalN)) + 1e-9);
            double peakDb = 20.0 * Math.Log10(rms.Max() * Math.Sqrt(2) + 1e-9);
            double meanRms = 0;
            for (int i = 0; i < frameCount; i++) meanRms += rms[i];
            meanRms /= Math.Max(1, frameCount);
            double energy = Math.Clamp(meanRms / 0.22, 0.0, 1.0);

            double bpm = DetectBpm(rms, TargetSampleRate);

            return new AudioAnalysisResult
            {
                Bpm = bpm,
                Energy = energy,
                LoudnessDb = loudnessDb,
                PeakDb = peakDb
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<float> Downsample(List<float> input, int srcRate, int dstRate)
    {
        var result = new List<float>((int)((long)input.Count * dstRate / srcRate) + 1);
        double ratio = (double)srcRate / dstRate;
        double pos = 0;
        int i = 0;
        while (i < input.Count)
        {
            // Simple box filter over the source range covered by one output sample
            int start = i;
            int end = Math.Min(input.Count, (int)Math.Floor(pos + ratio) + 1);
            end = Math.Max(end, start + 1);
            double sum = 0;
            int n = 0;
            for (int k = start; k < end && k < input.Count; k++) { sum += input[k]; n++; }
            result.Add((float)(sum / Math.Max(1, n)));
            i = end;
            pos += ratio;
        }
        return result;
    }

    private static double DetectBpm(double[] rms, int sampleRate)
    {
        int frameCount = rms.Length;

        // Onset envelope: positive part of the difference
        var envelope = new double[frameCount];
        for (int i = 1; i < frameCount; i++)
            envelope[i] = Math.Max(0, rms[i] - rms[i - 1]);

        // Light smoothing
        var smooth = new double[frameCount];
        for (int i = 2; i < frameCount - 2; i++)
            smooth[i] = (envelope[i - 2] + envelope[i - 1] + envelope[i] + envelope[i + 1] + envelope[i + 2]) / 5.0;

        double frameRate = (double)sampleRate / FrameSize; // analysis frames per second
        int minLag = Math.Max(2, (int)(frameRate * 60.0 / 190.0)); // ~190 BPM upper bound
        int maxLag = Math.Min(frameCount - 1, (int)(frameRate * 60.0 / 55.0)); // ~55 BPM lower bound

        double bestVal = 0;
        int bestLag = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            int cnt = 0;
            for (int i = 0; i + lag < frameCount; i++)
            {
                sum += smooth[i] * smooth[i + lag];
                cnt++;
            }
            if (cnt <= 0) continue;
            double val = sum / cnt;
            // Slight preference for shorter lags to avoid octave errors
            val /= (1.0 + lag * 0.01);
            if (val > bestVal) { bestVal = val; bestLag = lag; }
        }

        if (bestLag <= 0) return 0;
        double bpm = 60.0 * frameRate / bestLag;
        while (bpm < 70) bpm *= 2;
        while (bpm > 190) bpm /= 2;
        return Math.Round(bpm, 1);
    }
}