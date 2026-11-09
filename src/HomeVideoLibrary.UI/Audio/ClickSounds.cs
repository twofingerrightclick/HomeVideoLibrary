namespace HomeVideoLibrary.UI.Audio;

/// <summary>
/// Process-wide access point that VCR controls call when pressed. Defaults to an OpenAL-backed
/// service that stays silent until <see cref="IClickSoundService.IsEnabled"/> is set.
/// </summary>
public static class ClickSounds
{
    private static IClickSoundService? _service;

    public static IClickSoundService Service
    {
        get => _service ??= new OpenAlClickSoundService();
        set => _service = value ?? throw new ArgumentNullException(nameof(value));
    }
}
