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
    private readonly List<Shortcut> _shortcuts = [];
    private readonly HashSet<Key> _held = [];
    private readonly IClickSoundService _sounds;

    /// <param name="commandModifier">The platform's command key (Meta on macOS, Control elsewhere); see
    /// <see cref="CommandModifierOf"/>.</param>
    public ShortcutRouter(KeyModifiers commandModifier, IClickSoundService? sounds = null)
    {
        CommandModifier = commandModifier;
        _sounds = sounds ?? ClickSounds.Service;
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
        EventHandler<RoutedEventArgs> lost = (_, _) => _held.Clear();
        root.AddHandler(InputElement.KeyDownEvent, down, RoutingStrategies.Bubble);
        root.AddHandler(InputElement.KeyUpEvent, up, RoutingStrategies.Bubble, handledEventsToo: true);
        root.LostFocus += lost;
        return new Subscription(() =>
        {
            root.RemoveHandler(InputElement.KeyDownEvent, down);
            root.RemoveHandler(InputElement.KeyUpEvent, up);
            root.LostFocus -= lost;
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    /// <summary>Executes the first matching shortcut whose command can run; returns whether one ran. A key already
    /// held (auto-repeat) runs only shortcuts that allow repeats, and silently.</summary>
    public bool Handle(KeyEventArgs args)
    {
        var repeat = !_held.Add(args.Key);
        if (args.Handled) return false;
        var typing = IsTextEntry(args.Source);
        foreach (var shortcut in _shortcuts)
        {
            if (shortcut.Key != args.Key || shortcut.Modifiers != args.KeyModifiers || (typing && !shortcut.InTextEntry)) continue;
            if (repeat && !shortcut.Repeats) { args.Handled = true; return false; }
            if (shortcut.Command() is not { } command || !command.CanExecute(shortcut.Parameter)) continue;
            command.Execute(shortcut.Parameter);
            if (!repeat && shortcut.Sound != ClickSoundKind.None) _sounds.Play(shortcut.Sound);
            args.Handled = true;
            return true;
        }
        return false;
    }

    // macOS sends no KeyUp for a key released while Command is down, so releasing a modifier forgets every held key
    // rather than leaving one marked as repeating forever.
    private void Released(Key key)
    {
        if (key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
            _held.Clear();
        else _held.Remove(key);
    }

    /// <summary>Whether a key from <paramref name="source"/> is text input: a text box (including a chapter title being
    /// edited) or the tag entry.</summary>
    public static bool IsTextEntry(object? source) => source is Visual visual &&
        (visual is TextBox or TagChipInput || visual.FindAncestorOfType<TextBox>() is not null || visual.FindAncestorOfType<TagChipInput>() is not null);

    private sealed record Shortcut(Key Key, KeyModifiers Modifiers, Func<ICommand?> Command, ClickSoundKind Sound, object? Parameter,
        bool InTextEntry, bool Repeats);
}
