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

    /// <param name="commandModifier">The platform's command key (Meta on macOS, Control elsewhere); see
    /// <see cref="CommandModifierOf"/>.</param>
    public ShortcutRouter(KeyModifiers commandModifier) => CommandModifier = commandModifier;

    public KeyModifiers CommandModifier { get; }

    public static KeyModifiers CommandModifierOf(TopLevel? topLevel) =>
        topLevel?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <param name="command">Read when the key arrives, so it may follow a changing view model (for example the current page).</param>
    /// <param name="sound">The sound of the on-screen key this shortcut presses; None for seeks and other non-key actions.</param>
    /// <param name="inTextEntry">Whether the shortcut also fires while typing (command-key chords such as save).</param>
    public ShortcutRouter Map(Key key, KeyModifiers modifiers, Func<ICommand?> command, ClickSoundKind sound = ClickSoundKind.None,
        object? parameter = null, bool inTextEntry = false)
    {
        _shortcuts.Add(new Shortcut(key, modifiers, command, sound, parameter, inTextEntry));
        return this;
    }

    /// <summary>Maps <paramref name="key"/> with the platform command key plus <paramref name="extra"/> modifiers.
    /// Command chords fire in text entry too.</summary>
    public ShortcutRouter MapCommand(Key key, Func<ICommand?> command, ClickSoundKind sound = ClickSoundKind.None,
        object? parameter = null, KeyModifiers extra = KeyModifiers.None) =>
        Map(key, CommandModifier | extra, command, sound, parameter, inTextEntry: true);

    /// <summary>Routes unhandled key presses within <paramref name="root"/> through this router.</summary>
    public void Attach(InputElement root)
    {
        root.AddHandler(InputElement.KeyDownEvent, (_, args) => Handle(args), RoutingStrategies.Bubble);
        root.AddHandler(InputElement.KeyUpEvent, (_, args) => _held.Remove(args.Key), RoutingStrategies.Bubble, handledEventsToo: true);
        root.LostFocus += (_, _) => _held.Clear();
    }

    /// <summary>Executes the matching shortcut; returns whether one ran.</summary>
    public bool Handle(KeyEventArgs args)
    {
        var repeat = !_held.Add(args.Key);
        if (args.Handled) return false;
        var typing = IsTextEntry(args.Source);
        foreach (var shortcut in _shortcuts)
        {
            if (shortcut.Key != args.Key || shortcut.Modifiers != args.KeyModifiers || (typing && !shortcut.InTextEntry)) continue;
            if (shortcut.Command() is not { } command || !command.CanExecute(shortcut.Parameter)) continue;
            command.Execute(shortcut.Parameter);
            if (!repeat && shortcut.Sound != ClickSoundKind.None) ClickSounds.Service.Play(shortcut.Sound);
            args.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>Whether a key from <paramref name="source"/> is text input: a text box (including a chapter title being
    /// edited) or the tag entry.</summary>
    public static bool IsTextEntry(object? source) => source is Visual visual &&
        (visual is TextBox or TagChipInput || visual.FindAncestorOfType<TextBox>() is not null || visual.FindAncestorOfType<TagChipInput>() is not null);

    private sealed record Shortcut(Key Key, KeyModifiers Modifiers, Func<ICommand?> Command, ClickSoundKind Sound, object? Parameter,
        bool InTextEntry);
}
