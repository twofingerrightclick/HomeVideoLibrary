# Quick-click button feedback

Research date: 2026-11-12. Scope: investigation and implementation recommendation; no runtime behavior changed. The reported effect was not reproduced in a running UI during this research.

## Findings

The current [button themes](../src/HomeVideoLibrary.UI/Themes/Buttons.axaml) transition cap movement and shadows over **60 ms** for plastic/latch buttons and **90 ms** for rubber buttons. All pressed styling follows `:pressed`. [Travel tokens](../src/HomeVideoLibrary.UI/Themes/Tokens.axaml) move plastic caps 2 px; rubber caps move 1.5 px and scale to 0.985. `Vcr.Motion.Press` exists, but these button durations are hardcoded.

Avalonia 11.3.22 clears `IsPressed` on pointer release before raising a release-mode click. Space holds the pressed state until key-up; Enter invokes the click directly without setting it. Capture loss and focus loss also clear it. [Button source](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/Button.cs#L265-L419)

When a transition target changes, Avalonia starts from the current animated value and disposes the previous transition. Therefore, releasing before full depression redirects the animation toward rest rather than completing its original travel. This is a strong source-based explanation for the reported quick-click/hold difference; simply lengthening both directions can reduce the depression reached during a quick click. [Animatable source](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Base/Animation/Animatable.cs#L134-L165)

## Recommended change

Give the visual press its own minimum lifetime, independent of the actual input state. Proposed starting values, to tune visually:

| Phase | Plastic/latch | Rubber |
| --- | --- | --- |
| Depress | 30 ms | 45 ms |
| Earliest release begins, measured from press start | 90 ms | 110 ms |
| Return, with ease-out | 130 ms | 160 ms |

A quick click would produce about 220–270 ms of feedback. A held button stays depressed and returns when released after the minimum lifetime. Commands retain their existing activation timing.

Add one opt-in attached behavior in `HomeVideoLibrary.UI`, enabled by the three button themes. Observe real `IsPressed` for pointer/Space feedback, and use accepted `Click` events for activation paths that do not set it, including Enter. Keep a separate visual state; never prolong Avalonia's `IsPressed` or synthesize another click. Update pressed color, cap transform, and shadow together. Set timing resources centrally and keep one owner for each animated property.

Avalonia supports property transitions with duration/easing and explicit keyframe timelines. Either can drive this behavior; transitions plus a small visual-state timer fit the existing templates. A keyframe attached only to the transient `:pressed` selector does not by itself establish an independent minimum lifetime. [Transitions documentation](https://docs.avaloniaui.net/docs/graphics-animation/control-transitions), [keyframe documentation](https://docs.avaloniaui.net/docs/graphics-animation/keyframe-animations)

## Scope and validation

- Cover stock `Button`/`ToggleButton` as well as `PlasticButton`, `RubberButton`, and `LatchButton`; [VcrTheme](../src/HomeVideoLibrary.UI/VcrTheme.axaml) applies these themes globally.
- Deduplicate press/click feedback. Repeated clicks should refresh current feedback without queuing animations or changing command counts. Cancel stale timers on detach, disable, or template replacement.
- On cancellation, clear feedback promptly. Latches return to the latest checked position and retain their real LED state.
- Connect reduced-motion settings to shared button feedback. Existing desktop detection reads macOS and sets a window class; button themes currently do not consume it. Use immediate state feedback when motion is reduced.
- Test very short clicks, holds, rapid repeats, release outside, capture/focus loss, disabled controls, Space, Enter, default/access-key activation, and both toggle directions. Check command counts and cleanup. Visually compare live gallery controls and app buttons; forced pressed gallery specimens cannot validate timing. Navigation may remove a button before its visual effect completes.

This is a small shared UI change plus focused behavior tests and visual tuning. It requires no new package or video/backend changes. Actual effort depends mainly on covering keyboard activation, lifecycle cleanup, and reduced-motion propagation.

## Implementation follow-up

Implemented in `ButtonPressFeedback.cs` and the shared button themes with the proposed timings. The separate visual state preserves normal command timing, supports native and keyboard clicks, and clears pending feedback on cancellation, disable, detach, and template changes. Normal capture release after pointer-up preserves the minimum press. The inherited reduced-motion setting disables cap transitions and the minimum hold.

Validation: all 176 shared UI tests passed, including 12 button feedback cases covering quick clicks across all five button types, keyboard activation, holds/repeated clicks, cancellation, cleanup, reduced motion, and rendered cap travel/return to a latch's checked position. The desktop project built with zero warnings or errors. Motion was verified through headless rendering, without a manual desktop interaction pass.

Terra review found two issues, now corrected: a shared timing token also affected sliders, and the reduced-motion window class did not reach separate dialogs. Buttons now use their own press token, preserving the slider's 60 ms timing. The existing desktop preference read publishes an application resource consumed by all windows, including dialogs already open or created later. Terra reviewed these corrections and reported no further concrete issues. All 178 UI tests pass, including added tests for preference propagation and latch changes during release. The OS preference read itself was not exercised by these tests.
