namespace CodeView.Presentation;

/// <summary>Fixed workload for GUI measurements; not a simulation of physical input.</summary>
internal static class ScrollProbePlan
{
    internal const int StepCount = 64;

    internal static (int Line, int Column) Target(int index, int lineCount, int firstLineLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, StepCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(firstLineLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstLineLength, int.MaxValue - 1);
        if (lineCount == 1)
            return (1, checked((int)(firstLineLength * (long)index / (StepCount - 1) + 1)));
        var line = index < 32 ? index * 8L + 1 : 1 + (lineCount - 1L) * (index - 32) / 31;
        return ((int)Math.Min(lineCount, line), 1);
    }
}
