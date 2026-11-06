using System.Runtime.CompilerServices;
using CodeView.Documents;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class RowViewHistoryTests
{
    [TestCase("")]
    [TestCase("first\r\n日本語😀\r\nend")]
    public void SameBodyRestoresOriginalSelectionAndIndependentViewport(string body)
    {
        using var handle = Create(body);
        var history = new RowViewHistory();
        using (var first = new DocumentDisplaySession(handle))
        {
            first.Select(body.Length, 0);
            first.SetViewport(1, body.Length);
            history.Remember(handle, new(first.Anchor, first.Caret, first.FirstVisibleLine, first.HorizontalSourceOffset));
        }
        using var restored = new DocumentDisplaySession(handle);
        Assert.That(history.TryGet(handle, out var state), Is.True);
        restored.Select(state!.Anchor, state.Caret);
        restored.SetViewport(state.FirstLine, state.Horizontal);
        Assert.That((restored.Anchor, restored.Caret, restored.FirstVisibleLine, restored.HorizontalSourceOffset),
            Is.EqualTo(((long)body.Length, 0L, 1L, (long)body.Length)));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1)); // History retains no lease from the first session.
    }

    [Test]
    public void RecreatedSameContentNeverInheritsRetiredBodyState()
    {
        using var first = Create("same");
        using var reloaded = Create("same");
        var history = new RowViewHistory();
        history.Remember(first, new(4, 0, 1, 2));
        Assert.That(history.TryGet(reloaded, out _), Is.False);
        first.Dispose();
        Assert.That(history.TryGet(first, out _), Is.False);
        Assert.Throws<ObjectDisposedException>(() => history.Remember(first, new(0, 0, 1, 0)));
    }

    [Test]
    public void InvalidStateDoesNotReplaceLatestValidValueAndClearDropsIt()
    {
        using var handle = Create("abc");
        var history = new RowViewHistory();
        history.Remember(handle, new(1, 2, 1, 0));
        var latest = new RowViewState(3, 1, 1, 2);
        history.Remember(handle, latest);
        foreach (var state in new[] { new RowViewState(-1, 0, 1, 0), new(0, 4, 1, 0), new(0, 0, 0, 0), new(0, 0, long.MaxValue, 0), new(0, 0, 1, -1), new(0, 0, 1, 4) })
            Assert.Throws<ArgumentOutOfRangeException>(() => history.Remember(handle, state));
        Assert.That(history.TryGet(handle, out var actual) && actual == latest, Is.True);
        history.Clear();
        Assert.That(history.TryGet(handle, out _), Is.False);
    }

    [Test]
    public void HistoryRejectsForeignThreadAndNullArguments()
    {
        var history = new RowViewHistory();
        using var handle = Create("x");
        Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => history.Remember(handle, new(0, 0, 1, 0)));
            Assert.Throws<InvalidOperationException>(() => history.TryGet(handle, out _));
            Assert.Throws<InvalidOperationException>(history.Clear);
        }).GetAwaiter().GetResult();
        Assert.Throws<ArgumentNullException>(() => history.TryGet(null!, out _));
        Assert.Throws<ArgumentNullException>(() => history.Remember(handle, null!));
    }

    [Test]
    public void WeakHistoryDoesNotRetainAnUnownedHandleOrBody()
    {
        var history = new RowViewHistory();
        var (handle, body) = RememberUnowned(history);
        for (var i = 0; i < 3 && (handle.IsAlive || body.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.That(handle.IsAlive, Is.False);
        Assert.That(body.IsAlive, Is.False);
        GC.KeepAlive(history);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference) RememberUnowned(RowViewHistory history)
    {
        var handle = Create(new string('x', 1024 * 1024));
        var document = handle.ClaimForDisplay();
        history.Remember(handle, new(1, 2, 1, 100));
        return (new(handle), new(document));
    }

    private static DocumentHandle Create(string text)
    {
        using var builder = new DocumentBuilder(collectLineExtent: true);
        builder.Append(text.AsMemory());
        return builder.Complete();
    }
}
