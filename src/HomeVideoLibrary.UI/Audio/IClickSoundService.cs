namespace HomeVideoLibrary.UI.Audio;

/// <summary>Plays short button feedback sounds. Implementations must never block or throw from <see cref="Play"/>.</summary>
public interface IClickSoundService
{
    bool IsEnabled { get; set; }

    /// <summary>Linear gain from 0 (silent) to 1.</summary>
    float Volume { get; set; }

    void Play(ClickSoundKind kind);
}
