namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>Video-page choices that carry from one video to the next for the rest of the app session. The shell keeps
/// one; each page reads it when it opens and writes it as the user changes them.</summary>
public sealed class VideoPageSession
{
    public const double MinInspectorWidth = 300;
    public const double MaxInspectorWidth = 480;

    public double InspectorWidth { get; set; } = 340;
    public bool IsInspectorOpen { get; set; } = true;
    public int SelectedTab { get; set; } = (int)InspectorTab.Info;
    public double Volume { get; set; } = 0.8;
    public bool IsMuted { get; set; }
}

public enum InspectorTab { Info, Chapters, Tags, Frames, Edit }
