using HomeVideoLibrary.UI.Audio;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public sealed class ClickSynthTests
{
    [Theory]
    [InlineData(ClickSoundKind.Plastic, 0.06)]
    [InlineData(ClickSoundKind.Rubber, 0.09)]
    [InlineData(ClickSoundKind.Chime, 0.35)]
    public void Feedback_is_short_audible_and_unclipped(ClickSoundKind kind, double maxSeconds)
    {
        var pcm = ClickSynth.Synthesize(kind);

        Assert.InRange(pcm.Length, 1, (int)(maxSeconds * ClickSynth.SampleRate));
        var peak = pcm.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, short.MaxValue / 4, short.MaxValue - 1);
        Assert.Equal(0, pcm[^1]);
    }

    [Fact]
    public void Plastic_click_is_brighter_than_rubber_thud()
    {
        Assert.True(ZeroCrossingRate(ClickSynth.Synthesize(ClickSoundKind.Plastic))
            > 2 * ZeroCrossingRate(ClickSynth.Synthesize(ClickSoundKind.Rubber)));
    }

    [Fact]
    public void None_is_silent()
    {
        Assert.Empty(ClickSynth.Synthesize(ClickSoundKind.None));
    }

    private static double ZeroCrossingRate(short[] pcm)
    {
        var crossings = 0;
        for (var i = 1; i < pcm.Length; i++)
        {
            if ((pcm[i - 1] < 0) != (pcm[i] < 0))
            {
                crossings++;
            }
        }

        return crossings / (double)pcm.Length;
    }
}
