using Avalonia.Controls;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.Gallery;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SectionList.ItemsSource = GallerySection.All;
        SectionList.SelectionChanged += (_, _) =>
        {
            if (SectionList.SelectedItem is GallerySection section)
            {
                SectionHost.Content = section.Create();
                ContentScroller.Offset = default;
            }
        };
        SectionList.SelectedIndex = 0;

        SoundSwitch.IsCheckedChanged += (_, _) => ClickSounds.Service.IsEnabled = SoundSwitch.IsChecked == true;
        SoundVolume.ValueChanged += (_, e) => ClickSounds.Service.Volume = (float)e.NewValue;
    }
}
