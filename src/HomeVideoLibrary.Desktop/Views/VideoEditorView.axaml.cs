using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class VideoEditorView : UserControl
{
    public VideoEditorView()
    {
        InitializeComponent();
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => { if (ViewModel is { } vm) vm.IsSeeking = true; }, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, async (_, _) =>
        {
            if (ViewModel is not { } vm) return;
            await vm.SeekAsync(vm.SeekPosition);
            vm.IsSeeking = false;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.KeyDown += (_, args) => { if (IsSeekKey(args.Key) && ViewModel is { } vm) vm.IsSeeking = true; };
        SeekSlider.KeyUp += async (_, args) =>
        {
            if (!IsSeekKey(args.Key) || ViewModel is not { } vm) return;
            await vm.SeekAsync(vm.SeekPosition);
            vm.IsSeeking = false;
        };
    }
    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private VideoEditorViewModel? ViewModel => DataContext as VideoEditorViewModel;
    private async void PlayClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.TogglePlaybackAsync(); }
    private async void SeekClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.SeekAsync(vm.SeekPosition); }
    private void AddChapterClicked(object? sender, RoutedEventArgs args) => ViewModel?.AddChapter();
    private void CancelOperationClicked(object? sender, RoutedEventArgs args) => ViewModel?.CancelOperation();
    private async void SaveClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.SaveAsync(); }
    private async void CaptureClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.CaptureAsync(); }
    private async void VolumeChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Slider.ValueProperty && ViewModel is { } vm) await vm.ApplyVolumeAsync();
    }
    private async void ChapterSeekClicked(object? sender, RoutedEventArgs args)
    {
        if (ViewModel is { } vm && sender is Control { DataContext: ChapterEditorViewModel chapter })
        {
            if (TimeSpan.TryParse(chapter.Position, System.Globalization.CultureInfo.InvariantCulture, out var position)) await vm.SeekAsync(position.TotalSeconds);
        }
    }
    private void ChapterRemoveClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Control { DataContext: ChapterEditorViewModel chapter }) ViewModel?.RemoveChapter(chapter);
    }
    private void ThumbnailRemoveClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Control { DataContext: ThumbnailEditorViewModel thumbnail }) ViewModel?.RemoveThumbnail(thumbnail);
    }
}
