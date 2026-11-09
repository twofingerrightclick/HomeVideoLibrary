namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Maps the progress of consecutive weighted stages onto one overall 0..1 fraction that never goes backwards.
/// Stages whose length cannot be known report null (indeterminate) instead of a made-up fraction.</summary>
internal sealed class WeightedProgress(double totalWeight, Action<string, double?> sink)
{
    private double _completed, _current, _reported;
    private string _stage = "";

    public void Begin(string stage, double weight, bool determinate = true)
    {
        _completed += _current;
        _current = weight;
        _stage = stage;
        Report(determinate ? 0 : null);
    }

    public void Report(double? fraction)
    {
        if (fraction is not { } local)
        {
            sink(_stage, null);
            return;
        }
        var overall = Math.Clamp((_completed + _current * Math.Clamp(local, 0, 1)) / totalWeight, 0, 1);
        _reported = Math.Max(_reported, overall);
        sink(_stage, _reported);
    }

    public void Complete(string stage)
    {
        _completed = totalWeight;
        _current = 0;
        _stage = stage;
        Report(1);
    }
}
