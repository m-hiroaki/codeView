using CodeView.Documents;

namespace CodeView.Presentation;

/// <summary>Fixed source navigation commands, not injected physical keyboard input.</summary>
internal static class GuiNavigationPlan
{
    internal const int StepCount = 64;

    internal static void Apply(DocumentDisplaySession source, int step)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(step, StepCount);
        switch (step)
        {
            case 0: source.MoveToDocumentBoundary(false); break;
            case 1: source.MoveToDocumentBoundary(true); break;
            case 2: source.MoveToDocumentBoundary(false, true); break;
            case 3: source.SelectAll(); break;
            case 4: source.MoveToDocumentBoundary(false); break;
            case StepCount - 1: source.MoveToDocumentBoundary(true, true); break;
            default:
                for (var i = 0; i < 32; i++) source.MoveHorizontal(1, true);
                break;
        }
    }
}
