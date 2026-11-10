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
        get => LazyInitializer.EnsureInitialized(ref _service, CreateDefault);
        set => _service = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static IClickSoundService CreateDefault()
    {
        var service = new OpenAlClickSoundService();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => service.Dispose();
        return service;
    }
}
