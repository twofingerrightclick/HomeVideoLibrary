using System.ComponentModel;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>The page around an EDIT tab, as the tab sees it. Every property raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// when it changes, so the tab observes its inputs instead of being told about them.</summary>
public interface IEditHost : INotifyPropertyChanged
{
    /// <summary>The video as the library knows it now (its title changes when the page saves).</summary>
    LibraryVideo Source { get; }

    TimeSpan Playhead { get; }

    /// <summary>Whether the playhead is resting (paused or stopped). Positions taken while playing are already stale.</summary>
    bool IsPlayheadResting { get; }

    /// <summary>Whether the EDIT tab is on screen; split plans run only then.</summary>
    bool IsEditShown { get; }

    /// <summary>Why no edit can start now (for example unsaved details, which new clips would not contain), or null.</summary>
    string? EditBlocker { get; }
}
