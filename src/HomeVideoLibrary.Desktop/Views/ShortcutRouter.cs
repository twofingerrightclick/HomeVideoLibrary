using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Audio;
using HomeVideoLibrary.UI.Controls;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>One view's keyboard shortcuts: each gesture executes a view-model command and plays the sound of the
/// on-screen key it stands for. Keys reach the router only when no focused control handled them first (a focused
/// scrub bar keeps its arrows, a list keeps its navigation), and keys typed into text entry never trigger a shortcut
/// unless the mapping allows it.</summary>
public sealed class ShortcutRouter
{
    /// <summary>How soon after the previous press of the same key, with no release between, a press counts as key
    /// repeat. It covers the default delay before repeat starts on Windows (500 ms), macOS and X11 (about 660 ms).</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(1);

    private readonly List<Shortcut> _shortcuts = [];
    // Each held key and when its last KeyDown arrived.
    private readonly Dictionary<Key, long> _held = [];
    private readonly IClickSoundService _sounds;
    private readonly TimeProvider _time;

    /// <param name="commandModifier">The platform's command key (Meta on macOS, Control elsewhere); see
    /// <see cref="CommandModifierOf"/>.</param>
    /// <param name="time">The clock that tells key repeat from a new press.</param>
    public ShortcutRouter(KeyModifiers commandModifier, IClickSoundService? sounds = null, TimeProvider? time = null)
    {
        CommandModifier = commandModifier;
        _sounds = sounds ?? ClickSounds.Service;
        _time = time ?? TimeProvider.System;
    }

    public KeyModifiers CommandModifier { get; }

    public static KeyModifiers CommandModifierOf(TopLevel? topLevel) =>
        topLevel?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <param name="command">Read when the key arrives, so it may follow a changing view model (for example the current page).</param>
    /// <param name="sound">The sound of the on-screen key this shortcut presses; None for seeks and other non-key actions.</param>
    /// <param name="inTextEntry">Whether the shortcut also fires while typing (command-key chords such as save).</param>
    /// <param name="repeats">Whether holding the key repeats the action (seeks, volume). One-shot actions ignore key repeat.</param>
    public ShortcutRouter Map(Key key, KeyModifiers modifiers, Func<ICommand?> command, ClickSoundKind sound = ClickSoundKind.None,
        object? parameter = null, bool inTextEntry = false, bool repeats = false)
    {
        _shortcuts.Add(new Shortcut(key, modifiers, command, sound, parameter, inTextEntry, repeats));
        return this;
    }

    /// <summary>Maps <paramref name="key"/> with the platform command key plus <paramref name="extra"/> modifiers.
    /// Command chords fire in text entry too.</summary>
    public ShortcutRouter MapCommand(Key key, Func<ICommand?> command, ClickSoundKind sound = ClickSoundKind.None,
        object? parameter = null, KeyModifiers extra = KeyModifiers.None) =>
        Map(key, CommandModifier | extra, command, sound, parameter, inTextEntry: true);

    /// <summary>Routes unhandled key presses within <paramref name="root"/> through this router until the returned
    /// subscription is disposed.</summary>
    public IDisposable Attach(InputElement root)
    {
        EventHandler<KeyEventArgs> down = (_, args) => Handle(args);
        EventHandler<KeyEventArgs> up = (_, args) => Released(args.Key);
        EventHandler<VisualTreeAttachmentEventArgs> attached = (_, args) => { if (args.Root is WindowBase window) window.Deactivated += ForgetHeldKeys; };
        EventHandler<VisualTreeAttachmentEventArgs> detached = (_, args) => { if (args.Root is WindowBase window) window.Deactivated -= ForgetHeldKeys; };
        root.AddHandler(InputElement.KeyDownEvent, down, RoutingStrategies.Bubble);
        root.AddHandler(InputElement.KeyUpEvent, up, RoutingStrategies.Bubble, handledEventsToo: true);
        // Keys released in another window never send KeyUp here. Focus moving between controls inside the window is
        // not a release, so only the window losing activation forgets held keys.
        var activationSource = root as WindowBase ?? TopLevel.GetTopLevel(root as Visual) as WindowBase;
        if (activationSource is not null) activationSource.Deactivated += ForgetHeldKeys;
        root.AttachedToVisualTree += attached;
        root.DetachedFromVisualTree += detached;
        return new Subscription(() =>
        {
            root.RemoveHandler(InputElement.KeyDownEvent, down);
            root.RemoveHandler(InputElement.KeyUpEvent, up);
            root.AttachedToVisualTree -= attached;
            root.DetachedFromVisualTree -= detached;
            if (activationSource is not null) activationSource.Deactivated -= ForgetHeldKeys;
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    /// <summary>Executes the first matching shortcut whose command can run; returns whether one ran. Key repeat of such
    /// a shortcut runs it again (silently) only if it allows repeats, and is otherwise swallowed; a key whose shortcuts
    /// cannot run falls through, repeat or not.</summary>
    public bool Handle(KeyEventArgs args)
    {
        var repeat = Pressed(args.Key);
        if (args.Handled) return false;
        var typing = IsTextEntry(args.Source);
        foreach (var shortcut in _shortcuts)
        {
            if (shortcut.Key != args.Key || shortcut.Modifiers != args.KeyModifiers || (typing && !shortcut.InTextEntry)) continue;
            if (shortcut.Command() is not { } command || !command.CanExecute(shortcut.Parameter)) continue;
            args.Handled = true;
            if (repeat && !shortcut.Repeats) return false;
            command.Execute(shortcut.Parameter);
            if (!repeat && shortcut.Sound != ClickSoundKind.None) _sounds.Play(shortcut.Sound);
            return true;
        }
        return false;
    }

    /// <summary>Records a KeyDown and returns whether it is key repeat: the same key went down within
    /// <see cref="RepeatWindow"/> before, with no KeyUp between. Being held alone is not enough, because macOS sends no
    /// KeyUp for a key released while Command is down; holding Command and pressing F again must still count as a new
    /// press. The price is that a deliberate second press within the window, with that KeyUp lost, counts as repeat,
    /// and an OS repeat delay longer than the window yields one extra press.</summary>
    private bool Pressed(Key key)
    {
        var now = _time.GetTimestamp();
        var repeat = _held.TryGetValue(key, out var previous) && _time.GetElapsedTime(previous, now) <= RepeatWindow;
        _held[key] = now;
        return repeat;
    }

    // Releasing a modifier forgets every held key: macOS sends no KeyUp for keys released while Command was down.
    private void Released(Key key)
    {
        if (key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            _held.Clear();
        else _held.Remove(key);
    }

    private void ForgetHeldKeys(object? sender, EventArgs args) => _held.Clear();

    /// <summary>Whether a key from <paramref name="source"/> is text input: a text box (including a chapter title being
    /// edited) or the tag entry.</summary>
    public static bool IsTextEntry(object? source) => source is Visual visual &&
        (visual is TextBox or TagChipInput || visual.FindAncestorOfType<TextBox>() is not null || visual.FindAncestorOfType<TagChipInput>() is not null);

    private sealed record Shortcut(Key Key, KeyModifiers Modifiers, Func<ICommand?> Command, ClickSoundKind Sound, object? Parameter,
        bool InTextEntry, bool Repeats);
}
