using Silk.NET.OpenAL;

namespace HomeVideoLibrary.UI.Audio;

/// <summary>
/// Plays synthesized clicks through OpenAL Soft, whose native binaries ship for Windows, macOS, and
/// Linux. Buffers are uploaded once and a small source pool lets rapid presses overlap, so a
/// <see cref="Play"/> call costs a few native calls and no allocation. The device opens on a worker
/// thread the first time the service is enabled; presses before it is ready, or on machines without
/// a usable audio device, are silently dropped.
/// </summary>
public sealed unsafe class OpenAlClickSoundService : IClickSoundService, IDisposable
{
    private const int SourceCount = 6;

    private readonly Lock _gate = new();
    private readonly uint[] _sources = new uint[SourceCount];
    private readonly Dictionary<ClickSoundKind, uint> _buffers = [];
    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private Task? _startup;
    private bool _ready;
    private bool _disposed;
    private int _nextSource;
    private bool _isEnabled;
    private float _volume = 0.6f;

    /// <summary>Why the audio device could not be opened, or null while healthy or not yet started.</summary>
    public string? FailureReason { get; private set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (value)
            {
                _startup ??= Task.Run(Start);
            }
        }
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Completes once the device is open (or has failed), for callers that want to pre-warm.</summary>
    public Task WarmUpAsync()
    {
        _startup ??= Task.Run(Start);
        return _startup;
    }

    public void Play(ClickSoundKind kind)
    {
        if (!_isEnabled || kind == ClickSoundKind.None || !_ready)
        {
            return;
        }

        lock (_gate)
        {
            if (_al is null || _disposed || !_buffers.TryGetValue(kind, out var buffer))
            {
                return;
            }

            var source = _sources[_nextSource];
            _nextSource = (_nextSource + 1) % SourceCount;
            _al.SourceStop(source);
            _al.SetSourceProperty(source, SourceInteger.Buffer, buffer);
            _al.SetSourceProperty(source, SourceFloat.Gain, _volume);
            _al.SourcePlay(source);
        }
    }

    public void Dispose()
    {
        _startup?.Wait(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ready = false;
            if (_al is not null)
            {
                fixed (uint* sources = _sources)
                {
                    _al.DeleteSources(SourceCount, sources);
                }

                foreach (var buffer in _buffers.Values)
                {
                    _al.DeleteBuffer(buffer);
                }
            }

            if (_alc is not null)
            {
                _alc.MakeContextCurrent(null);
                if (_context is not null)
                {
                    _alc.DestroyContext(_context);
                }

                if (_device is not null)
                {
                    _alc.CloseDevice(_device);
                }
            }

            _al?.Dispose();
            _alc?.Dispose();
        }
    }

    private void Start()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _alc = ALContext.GetApi(soft: true);
                _al = AL.GetApi(soft: true);
                _device = _alc.OpenDevice(string.Empty);
                if (_device is null)
                {
                    FailureReason = "No audio output device is available.";
                    return;
                }

                _context = _alc.CreateContext(_device, null);
                _alc.MakeContextCurrent(_context);
                foreach (var kind in new[] { ClickSoundKind.Plastic, ClickSoundKind.Rubber })
                {
                    var pcm = ClickSynth.Synthesize(kind);
                    var buffer = _al.GenBuffer();
                    fixed (short* data = pcm)
                    {
                        _al.BufferData(buffer, BufferFormat.Mono16, data, pcm.Length * sizeof(short), ClickSynth.SampleRate);
                    }

                    _buffers[kind] = buffer;
                }

                fixed (uint* sources = _sources)
                {
                    _al.GenSources(SourceCount, sources);
                }

                var error = _al.GetError();
                if (error != AudioError.NoError)
                {
                    FailureReason = $"OpenAL setup failed: {error}.";
                    return;
                }

                _ready = true;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or InvalidOperationException or EntryPointNotFoundException)
        {
            FailureReason = exception.Message;
        }
    }
}
