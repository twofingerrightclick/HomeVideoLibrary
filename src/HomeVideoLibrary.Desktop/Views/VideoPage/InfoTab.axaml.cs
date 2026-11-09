using Avalonia.Controls;
using Avalonia.Input;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

public sealed partial class InfoTab : UserControl
{
    public InfoTab()
    {
        InitializeComponent();
        // A single-line title commits as you type; Enter moves on to the description.
        TitleField.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None) return;
            args.Handled = true;
            DescriptionField.Focus(NavigationMethod.Tab);
        };
    }
}
