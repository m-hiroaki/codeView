using System.Collections.Specialized;
using CodeView.Contracts;
using CodeView.Presentation;
using CodeView.Search;

namespace CodeView.Tests.Presentation;

internal sealed class SearchResultsTests
{
    private static readonly string Root = Path.GetTempPath();
    private static SearchHit Hit(long id, long offset = 0) => new(id, SourcePath.Create("example.cs", Root),
        new(offset, 3, new(1, offset + 1)), new("aFOOb", 1, 3), new(100, DateTime.UnixEpoch));

    [Test]
    public void SeveralFilesPublishOneRangeNotificationAndShareCommitState()
    {
        var results = new SearchResults();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        results.Rows.CollectionChanged += (_, args) => notifications.Add(args);
        results.Stage(Hit(1), Root);
        results.Stage(Hit(1, 4), Root);
        results.Commit(1);
        results.Stage(Hit(2), Root);
        results.Commit(2);
        Assert.That(results.Rows, Is.Empty);
        results.Publish();
        Assert.That(notifications.Count, Is.EqualTo(1));
        Assert.That(notifications[0].NewItems!.Count, Is.EqualTo(3));
        Assert.That(results.Rows[0].File, Is.SameAs(results.Rows[1].File));
        Assert.That(results.Rows[2].File, Is.Not.SameAs(results.Rows[0].File));
        Assert.That(results.Rows.All(results.CanActivate), Is.True);
        Assert.That(results.CommittedCount, Is.EqualTo(3));
        Assert.That(results.Rows[0].Before, Is.EqualTo("a"));
        Assert.That(results.Rows[0].Match, Is.EqualTo("FOO"));
        Assert.That(results.Rows[0].After, Is.EqualTo("b"));
        Assert.That(results.Rows[0].Location, Is.EqualTo("example.cs : 1:1"));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void InvalidationRemovesPublishedAndStagedTailOnly(int published)
    {
        var results = new SearchResults();
        results.Stage(Hit(1), Root);
        results.Commit(1);
        results.Publish();
        for (var i = 0; i < published; i++) results.Stage(Hit(2, i), Root);
        results.Publish();
        for (var i = published; i < 2; i++) results.Stage(Hit(2, i), Root);
        results.Invalidate(2);
        results.Publish();
        Assert.That(results.Rows.Count, Is.EqualTo(1));
        Assert.That(results.Rows.Single().Hit.FileScanId, Is.EqualTo(1));
        Assert.That(results.CommittedCount, Is.EqualTo(1));
    }

    [Test]
    public void CancellationRemovesAllUnadoptedCommitsEvenWhenWorkerHasNoUncommittedId()
    {
        var results = new SearchResults();
        results.Stage(Hit(1), Root);
        results.Commit(1);
        results.Stage(Hit(2), Root);
        results.Publish();
        var stale = results.Rows[1];
        Assert.That(results.CanActivate(stale), Is.False);
        // FileCommitted(2) was still queued when cancellation discarded it.
        results.RemoveProvisional();
        Assert.That(results.Rows.Count, Is.EqualTo(1));
        Assert.That(results.CanActivate(stale), Is.False);
        Assert.That(stale.File.IsInvalidated, Is.True);
        Assert.That(results.CommittedCount, Is.EqualTo(1));
    }

    [Test]
    public void LimitKeepsProvisionalRowsClearlyIncompleteAndNonNavigable()
    {
        var results = new SearchResults();
        results.Stage(Hit(1), Root);
        results.Publish();
        results.MarkIncomplete();
        Assert.That(results.Rows.Single().File.Status, Is.EqualTo("未完了・移動不可"));
        Assert.That(results.CanActivate(results.Rows.Single()), Is.False);
        results.Commit(1);
        Assert.That(results.CanActivate(results.Rows.Single()), Is.True);
    }

    [Test]
    public void ClearedAndForeignRowsCannotActivateEvenWithSameFileId()
    {
        var results = new SearchResults();
        results.Stage(Hit(1), Root); results.Commit(1); results.Publish();
        var old = results.Rows.Single();
        results.Clear();
        results.Stage(Hit(1), Root); results.Commit(1); results.Publish();
        Assert.That(results.CanActivate(old), Is.False);
        Assert.That(new SearchResults().CanActivate(results.Rows.Single()), Is.False);
    }

    [Test]
    public void ResultLimitIsEnforcedAndEmptyFileCommitRetainsNoRows()
    {
        var results = new SearchResults();
        results.Commit(1);
        results.Invalidate(1);
        for (var i = 0; i < SearchLimits.ResultLimit; i++) results.Stage(Hit(2, i), Root);
        Assert.Throws<InvalidOperationException>(() => results.Stage(Hit(2), Root));
        results.Publish();
        Assert.That(results.Rows.Count, Is.EqualTo(10000));
    }
}
