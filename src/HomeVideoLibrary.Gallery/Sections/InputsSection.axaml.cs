using Avalonia.Controls;

namespace HomeVideoLibrary.Gallery.Sections;

public partial class InputsSection : UserControl, IScreenshotScript
{
    public InputsSection() => InitializeComponent();

    public void Prepare(Window window)
    {
        FocusedBox.Focus();
        FocusedBox.CaretIndex = 9;
    }
}
