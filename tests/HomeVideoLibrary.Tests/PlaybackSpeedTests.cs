using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class PlaybackSpeedTests
{
    [Fact]
    public void FullscreenMenuKeepsControlsVisibleUntilItCloses()
    {
        var time = new FakeTimeProvider();
        using var fullscreen = new FullscreenViewModel(() => true, time) { IsFullscreen = true };
        fullscreen.HoldMenu(true);
        fullscreen.Hold(false);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(fullscreen.AreControlsShown);
        fullscreen.HoldMenu(false);
        Assert.True(fullscreen.AreControlsShown);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(fullscreen.AreControlsShown);
    }

    [Fact]
    public async Task SpeedChangesReachPlaybackAndSurviveReopening()
    {
        var playback = new FakePlayback();
        await using var player = new PlayerViewModel(playback, "/test.mkv", TimeSpan.FromMinutes(1), action => action(), _ => { });
        await player.OpenAsync(TimeSpan.Zero, true, CancellationToken.None);
        Assert.Equal([1d], playback.Speeds);
        player.Speed = 0.25;
        player.Speed = 2;
        Assert.Equal([1d, 0.25, 2], playback.Speeds);
        await player.WhileReleasedAsync(() => { player.Speed = 1.5; return Task.CompletedTask; });
        Assert.Equal(1.5, playback.Speeds[^1]);
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public async Task SpeedIsBoundedAndRejectedChangesRestoreThePreviousValue()
    {
        var playback = new FakePlayback();
        var errors = new List<string>();
        await using var player = new PlayerViewModel(playback, "/test.mkv", TimeSpan.FromMinutes(1), action => action(), errors.Add);
        await player.OpenAsync(TimeSpan.Zero, false, CancellationToken.None);
        player.Speed = 0;
        Assert.Equal(0.25, player.Speed);
        player.Speed = 3;
        Assert.Equal(2, player.Speed);
        player.Speed = double.NaN;
        Assert.Equal(2, player.Speed);
        playback.SpeedError = new InvalidOperationException("Unsupported speed");
        player.Speed = 0.5;
        Assert.Equal(2, player.Speed);
        Assert.Equal(["Unsupported speed"], errors);
    }
}
