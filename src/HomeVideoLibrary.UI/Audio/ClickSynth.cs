namespace HomeVideoLibrary.UI.Audio;

/// <summary>
/// Procedurally synthesizes the feedback sounds so no recorded or licensed audio ships with the app.
/// Output is mono signed 16-bit PCM at <see cref="SampleRate"/>.
/// </summary>
public static class ClickSynth
{
    public const int SampleRate = 44_100;

    public static short[] Synthesize(ClickSoundKind kind) => kind switch
    {
        ClickSoundKind.Plastic => ToPcm(Plastic()),
        ClickSoundKind.Rubber => ToPcm(Rubber()),
        _ => [],
    };

    // A hard-plastic key: a bright noise transient, a ~3.4 kHz case resonance, and a second, quieter
    // snap 11 ms later when the switch bottoms out.
    private static float[] Plastic()
    {
        var samples = new float[Samples(0.045)];
        var noise = new Random(7);
        var lowPass = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            var white = (float)(noise.NextDouble() * 2 - 1);
            lowPass += 0.35f * (white - lowPass);
            var highPassed = white - lowPass;
            var transient = highPassed * Decay(t, 0.0018);
            var ring = Math.Sin(2 * Math.PI * 3400 * t) * Decay(t, 0.0045) * 0.55;
            var body = Math.Sin(2 * Math.PI * 1150 * t) * Decay(t, 0.007) * 0.35;
            var snapT = t - 0.011;
            var snap = snapT < 0 ? 0 : (highPassed * 0.45 + Math.Sin(2 * Math.PI * 2600 * snapT) * 0.3) * Decay(snapT, 0.0022);
            samples[i] = (float)(transient + ring + body + snap);
        }

        return Normalize(samples, 0.55f);
    }

    // A rubber dome: soft attack, low-passed noise, and a short downward-pitched thump with no ring.
    private static float[] Rubber()
    {
        var samples = new float[Samples(0.07)];
        var noise = new Random(11);
        var lowPass = 0f;
        var phase = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            var white = (float)(noise.NextDouble() * 2 - 1);
            lowPass += 0.08f * (white - lowPass);
            var attack = Math.Min(1, t / 0.0025);
            var frequency = 150 + 90 * Math.Exp(-t / 0.012);
            phase += 2 * Math.PI * frequency / SampleRate;
            var thump = Math.Sin(phase) * Decay(t, 0.016);
            var muffled = lowPass * 3 * Decay(t, 0.006);
            var knock = Math.Sin(2 * Math.PI * 420 * t) * Decay(t, 0.008) * 0.3;
            samples[i] = (float)(attack * (thump + muffled + knock));
        }

        return Normalize(samples, 0.6f);
    }

    private static int Samples(double seconds) => (int)(seconds * SampleRate);

    private static double Decay(double t, double tau) => Math.Exp(-t / tau);

    private static float[] Normalize(float[] samples, float peak)
    {
        var max = samples.Max(Math.Abs);
        if (max <= 0)
        {
            return samples;
        }

        var scale = peak / max;
        var fadeLength = Math.Min(samples.Length, Samples(0.004));
        for (var i = 0; i < samples.Length; i++)
        {
            var fromEnd = samples.Length - 1 - i;
            var fade = fromEnd < fadeLength ? fromEnd / (float)fadeLength : 1f;
            samples[i] *= scale * fade;
        }

        return samples;
    }

    private static short[] ToPcm(float[] samples)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
        }

        return pcm;
    }
}
