using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.Desktop.Views.Editing;

public sealed partial class MergePageView : UserControl
{
    public MergePageView()
    {
        InitializeComponent();
        var shortcuts = new ShortcutRouter(ShortcutRouter.CommandModifierOf(null))
            .Map(Key.Up, KeyModifiers.Alt, () => Page?.MoveUpCommand, ClickSoundKind.Plastic)
            .Map(Key.Down, KeyModifiers.Alt, () => Page?.MoveDownCommand, ClickSoundKind.Plastic)
            .Map(Key.Escape, KeyModifiers.None, () => Page?.BackCommand, ClickSoundKind.Plastic);
        shortcuts.Attach(this);
        // The list takes arrow keys whatever the modifiers, so Alt+arrows must reach the shortcuts before it.
        SourceList.AddHandler(KeyDownEvent, (_, args) => shortcuts.Handle(args), RoutingStrategies.Tunnel);
    }

    private MergePageViewModel? Page => DataContext as MergePageViewModel;
}
