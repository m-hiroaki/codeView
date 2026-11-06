namespace CodeView.Presentation;

/// <summary>Bounded, Main-relative checkpoints in the explicit GUI diagnostic build.</summary>
internal sealed class StartupTimeline
{
    private static readonly string[] Stages =
    [
        "main-entered", "builder-configured", "app-initialize-start", "app-initialize-end",
        "framework-start", "window-constructor-start", "window-xaml-loaded",
        "probe-attach-start", "probe-attach-end", "window-constructor-end", "framework-end", "window-opened"
    ];
    private readonly List<StartupCheckpoint> _samples = new(Stages.Length);

    public void Record(string stage, double milliseconds)
    {
        if (_samples.Count == Stages.Length || stage != Stages[_samples.Count])
            throw new InvalidOperationException("Unexpected startup checkpoint order.");
        if (!double.IsFinite(milliseconds) || milliseconds < 0 ||
            (_samples.Count > 0 && milliseconds < _samples[^1].Milliseconds))
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        _samples.Add(new(stage, milliseconds));
    }

    // A failed launch may have only a prefix. Do not hide its original failure.
    public IReadOnlyList<StartupCheckpoint> Snapshot() => _samples.ToArray();
}

internal sealed record StartupCheckpoint(string Stage, double Milliseconds);
