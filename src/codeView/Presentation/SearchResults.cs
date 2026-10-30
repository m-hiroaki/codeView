using System.ComponentModel;
using Avalonia.Collections;
using CodeView.Search;

namespace CodeView.Presentation;

internal sealed class SearchResultFile(long id, int start) : INotifyPropertyChanged
{
    public long Id { get; } = id;
    internal int Start { get; } = start;
    internal int Count { get; set; }
    public bool IsCommitted { get; private set; }
    public bool IsInvalidated { get; private set; }
    private bool _incomplete;
    public string Status => IsInvalidated ? "無効" : IsCommitted ? "確定" : _incomplete ? "未完了・移動不可" : "検証中";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Commit() { IsCommitted = true; Changed(); }
    internal void Invalidate() { IsInvalidated = true; Changed(); }
    internal void MarkIncomplete() { _incomplete = true; Changed(); }
    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsCommitted)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
}

internal sealed class SearchResultRow
{
    internal object Generation { get; }
    public SearchHit Hit { get; }
    public SearchResultFile File { get; }
    public string Location { get; }
    public string FullPath => Hit.Path.FullPath;
    public string Before { get; }
    public string Match { get; }
    public string After { get; }

    internal SearchResultRow(SearchHit hit, SearchResultFile file, string root, object generation)
    {
        (Hit, File, Generation) = (hit, file, generation);
        Location = $"{Path.GetRelativePath(root, hit.Path.FullPath)} : {hit.Range.Start.Line}:{hit.Range.Start.Column}";
        Before = hit.Snippet.Text[..hit.Snippet.MatchStart];
        Match = hit.Snippet.Text.Substring(hit.Snippet.MatchStart, hit.Snippet.MatchLength);
        After = hit.Snippet.Text[(hit.Snippet.MatchStart + hit.Snippet.MatchLength)..];
    }
}

/// <summary>Stage a UI slice, then publish one collection notification. Only provisional files need a lookup.</summary>
internal sealed class SearchResults
{
    private object _generation = new();
    private readonly Dictionary<long, SearchResultFile> _pending = [];
    private readonly List<SearchResultRow> _staged = new(256);
    public AvaloniaList<SearchResultRow> Rows { get; } = [];
    public int CommittedCount { get; private set; }
    public int Count => Rows.Count + _staged.Count;
    public bool CanActivate(SearchResultRow row) => ReferenceEquals(row.Generation, _generation)
        && row.File.IsCommitted && !row.File.IsInvalidated;

    public void Clear()
    {
        _generation = new();
        _pending.Clear();
        _staged.Clear();
        Rows.Clear();
        CommittedCount = 0;
    }

    public void Stage(SearchHit hit, string root)
    {
        if (Count >= SearchLimits.ResultLimit) throw new InvalidOperationException("結果表示の上限を超えています。");
        if (!_pending.TryGetValue(hit.FileScanId, out var file))
            _pending.Add(hit.FileScanId, file = new(hit.FileScanId, Count));
        file.Count++;
        _staged.Add(new(hit, file, root, _generation));
    }

    public void Commit(long id)
    {
        if (!_pending.Remove(id, out var file)) return; // Files without hits need no retained state.
        CommittedCount = checked(CommittedCount + file.Count);
        file.Commit();
    }

    public void Invalidate(long id)
    {
        if (!_pending.Remove(id, out var file)) return;
        if (file.Start + file.Count != Count) throw new InvalidOperationException("未確定ファイルの結果順序が不正です。");
        var published = Math.Clamp(Rows.Count - file.Start, 0, file.Count);
        var stagedStart = Math.Max(0, file.Start - Rows.Count);
        if (file.Count > published) _staged.RemoveRange(stagedStart, file.Count - published);
        if (published > 0) Rows.RemoveRange(file.Start, published);
        file.Invalidate();
    }

    public void RemoveProvisional()
    {
        foreach (var id in _pending.Values.OrderByDescending(file => file.Start).Select(file => file.Id).ToArray()) Invalidate(id);
    }

    public void MarkIncomplete()
    {
        foreach (var file in _pending.Values) file.MarkIncomplete();
    }

    public void Publish()
    {
        if (_staged.Count == 0) return;
        Rows.AddRange(_staged.ToArray());
        _staged.Clear();
    }
}
