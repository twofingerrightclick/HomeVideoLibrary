using Avalonia.Controls;
using Avalonia.Interactivity;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class ClipEditorView : UserControl
{
    public ClipEditorView() => InitializeComponent();
    private ClipEditorViewModel? ViewModel => DataContext as ClipEditorViewModel;
    private async void TrimClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.TrimAsync(); }
    private async void SplitClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.SplitAsync(); }
    private async void MergeClicked(object? sender, RoutedEventArgs args) { if (ViewModel is { } vm) await vm.MergeAsync(); }
    private void MoveUpClicked(object? sender, RoutedEventArgs args) => ViewModel?.MoveSelected(-1);
    private void MoveDownClicked(object? sender, RoutedEventArgs args) => ViewModel?.MoveSelected(1);
    private void CancelClicked(object? sender, RoutedEventArgs args) => ViewModel?.Cancel();
}
