using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HomeVideoLibrary.UI;

/// <summary>
/// Keeps a quick press visible without delaying the button's click or command. Enabled by the
/// VCR button themes, including on stock buttons; the visual class never changes IsPressed.
/// </summary>
public sealed class ButtonPressFeedback
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ButtonPressFeedback, Button, bool>("IsEnabled");

    public static readonly AttachedProperty<TimeSpan> MinimumDurationProperty =
        AvaloniaProperty.RegisterAttached<ButtonPressFeedback, Button, TimeSpan>("MinimumDuration", TimeSpan.FromMilliseconds(90));

    public static readonly AttachedProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.RegisterAttached<ButtonPressFeedback, Control, bool>("ReducedMotion", inherits: true);

    private static readonly ConditionalWeakTable<Button, Feedback> States = new();

    static ButtonPressFeedback()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            if (GetIsEnabled(button))
                States.GetValue(button, b => new Feedback(b));
            else if (States.TryGetValue(button, out var feedback))
            {
                feedback.Dispose();
                States.Remove(button);
            }
        });
    }

    private ButtonPressFeedback() { }

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);
    public static TimeSpan GetMinimumDuration(Button button) => button.GetValue(MinimumDurationProperty);
    public static void SetMinimumDuration(Button button, TimeSpan value) => button.SetValue(MinimumDurationProperty, value);
    public static bool GetReducedMotion(Control control) => control.GetValue(ReducedMotionProperty);
    public static void SetReducedMotion(Control control, bool value) => control.SetValue(ReducedMotionProperty, value);

    private sealed class Feedback : IDisposable
    {
        private readonly Button _button;
        private readonly DispatcherTimer _release = new();
        private long _started;
        private bool _releasedInside;
        private bool _inputActivation;

        public Feedback(Button button)
        {
            _button = button;
            _release.Tick += OnRelease;
            button.PropertyChanged += OnPropertyChanged;
            button.DetachedFromVisualTree += OnDetached;
            button.TemplateApplied += OnTemplateApplied;
            button.AddHandler(Button.ClickEvent, OnClick, handledEventsToo: true);
            button.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
            button.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, handledEventsToo: true);
            button.AddHandler(InputElement.LostFocusEvent, OnLostFocus, handledEventsToo: true);
            UpdateReducedMotion();
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Button.IsPressedProperty)
            {
                if (_button.IsPressed)
                {
                    _inputActivation = true;
                    Begin();
                }
                else Release();
            }
            else if (e.Property == ReducedMotionProperty)
                UpdateReducedMotion();
            else if (e.Property == InputElement.IsEffectivelyEnabledProperty && !_button.IsEffectivelyEnabled)
                Cancel();
        }

        private void Begin()
        {
            if (!_button.IsEffectivelyEnabled || _button.GetVisualRoot() is null) return;
            _release.Stop();
            _releasedInside = false;
            _started = Stopwatch.GetTimestamp();
            _button.Classes.Set("vcr-pressed", true);
        }

        private void Release()
        {
            if (!_button.Classes.Contains("vcr-pressed")) return;
            var remaining = GetMinimumDuration(_button) - Stopwatch.GetElapsedTime(_started);
            if (GetReducedMotion(_button) || remaining <= TimeSpan.Zero)
                Reset();
            else
            {
                _release.Interval = remaining;
                _release.Start();
            }
        }

        // Enter, access keys, and automation can click without changing IsPressed. Pointer/Space
        // clicks already have feedback; do not start a second animation for the same activation.
        private void OnClick(object? sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, _button) || !_button.IsEffectivelyEnabled || GetReducedMotion(_button)) return;
            if (!_inputActivation) Begin();
            _inputActivation = false;
            if (!_button.IsPressed) Release();
        }

        private void OnRelease(object? sender, EventArgs e) => Reset();

        private void UpdateReducedMotion()
        {
            _button.Classes.Set("vcr-reduced-motion", GetReducedMotion(_button));
            Cancel();
            if (_button.IsPressed) Begin();
        }

        private void Reset()
        {
            _release.Stop();
            _button.Classes.Set("vcr-pressed", false);
        }

        private void Cancel()
        {
            _inputActivation = false;
            Reset();
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _releasedInside = new Rect(_button.Bounds.Size).Contains(e.GetPosition(_button));
            if (!_releasedInside) Cancel();
        }

        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            // The input manager releases capture after a normal pointer-up too.
            if (!_releasedInside) Cancel();
            _releasedInside = false;
        }
        private void OnLostFocus(object? sender, RoutedEventArgs e) => Cancel();
        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Cancel();
        private void OnTemplateApplied(object? sender, Avalonia.Controls.Primitives.TemplateAppliedEventArgs e) => Cancel();

        public void Dispose()
        {
            Cancel();
            _release.Tick -= OnRelease;
            _button.PropertyChanged -= OnPropertyChanged;
            _button.DetachedFromVisualTree -= OnDetached;
            _button.TemplateApplied -= OnTemplateApplied;
            _button.RemoveHandler(Button.ClickEvent, OnClick);
            _button.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            _button.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            _button.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
            _button.Classes.Set("vcr-reduced-motion", false);
        }
    }
}
