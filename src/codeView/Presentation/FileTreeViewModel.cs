using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Threading;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed class FileTreeRow(TreeNodeState node, FileTreeRow? parent) : INotifyPropertyChanged
{
    public TreeNodeState Node { get; } = node;
    public FileTreeRow? Parent { get; } = parent;
    public int Depth { get; } = parent is null ? 0 : checked(parent.Depth + 1);
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Name => Node.Entry.Name;
    public string Path => Node.Entry.Path.DisplayPath;
    public bool CanExpand => Node.CanExpand;
    public bool IsFile => Node.Entry.Kind == SourceEntryKind.FileCandidate;
    public string Disclosure => CanExpand ? Node.IsExpanded ? "⌄" : "›" : Node.Entry.IsLink ? "↗" : "·";
    public string Detail => Node.Entry.IsLink ? "リンク" : !CanExpand ? "" : Node.Status switch
    {
        TreeLoadStatus.Loading => "読込中…",
        TreeLoadStatus.Failed => Node.Children.Count > 0 ? "部分一覧・再試行可" : "読込失敗・再試行可",
        TreeLoadStatus.Loaded when Node.Children.Count == 0 => "空",
        _ => ""
    };
    public string AccessibleName => Name + (CanExpand ? " フォルダ " : " ") + Detail;
    internal long ProjectionVersion { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh()
    {
        foreach (var name in new[] { nameof(Disclosure), nameof(Detail), nameof(AccessibleName) })
            PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>Only visible logical rows, projected incrementally into a virtualized flat list.</summary>
internal sealed class FileTreeViewModel(Func<Task>? yieldToUi = null) : INotifyPropertyChanged
{
    private readonly Func<Task> _yieldToUi = yieldToUi ?? YieldToDispatcherAsync;
    private readonly Dictionary<TreeNodeState, FileTreeRow> _visible = [];
    private readonly Dictionary<FileTreeRow, Task> _expansions = [];
    private TreeSession? _tree;
    private long _bindingVersion;
    private FileTreeRow? _selected;
    private string _notice = "";
    public AvaloniaList<FileTreeRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public string Notice { get => _notice; private set { _notice = value; Changed(nameof(Notice)); } }
    public FileTreeRow? SelectedRow { get => _selected; set { _selected = value; Changed(nameof(SelectedRow)); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCurrentRow(FileTreeRow row) => _tree is not null && row.Node.IsAttached
        && _visible.TryGetValue(row.Node, out var current) && ReferenceEquals(current, row);

    public void Clear()
    {
        _bindingVersion = checked(_bindingVersion + 1);
        _tree = null;
        _visible.Clear();
        _expansions.Clear();
        Rows.Clear();
        SelectedRow = null;
        Notice = "";
        Changed(nameof(HasRows));
    }

    public async Task BindAsync(TreeSession? tree)
    {
        Clear();
        _tree = tree;
        if (tree is null) return;
        var version = _bindingVersion;
        await InsertChildrenAsync(tree.Root.Children, null,
            () => ReferenceEquals(_tree, tree) && _bindingVersion == version && tree.Root.IsAttached);
    }

    public Task ExpandAsync(FileTreeRow row, bool reload = false)
    {
        if (!IsCurrentRow(row) || !row.CanExpand) return Task.CompletedTask;
        if (!reload && _expansions.TryGetValue(row, out var task)) return task;
        if (!reload && row.Node.IsExpanded && row.Node.Status == TreeLoadStatus.Loaded) return Task.CompletedTask;
        task = ExpandCoreAsync(row, reload);
        if (!task.IsCompleted) _expansions[row] = task;
        return task;
    }

    private async Task ExpandCoreAsync(FileTreeRow row, bool reload)
    {
        var tree = _tree!;
        var before = row.Node.Generation;
        var operation = tree.ExpandAsync(row.Node, reload);
        if (operation.IsCompletedSuccessfully && operation.Result.Status == OperationStatus.Failure && row.Node.Generation == before)
        {
            Notice = operation.Result.Error!.Code == ErrorCode.QueueFull ? "展開待ちが上限に達しました。後でもう一度お試しください。" : "展開できません。";
            return;
        }
        var version = row.ProjectionVersion = checked(row.ProjectionVersion + 1);
        var generation = row.Node.Generation;
        RemoveDescendants(row);
        row.Refresh();
        Notice = "";
        try
        {
            var result = await operation;
            if (!CanAdopt() || result.Status == OperationStatus.Cancelled) return;
            row.Refresh();
            await InsertChildrenAsync(row.Node.Children, row, CanAdopt);
        }
        finally
        {
            if (row.ProjectionVersion == version) _expansions.Remove(row);
        }
        bool CanAdopt() => ReferenceEquals(_tree, tree) && IsCurrentRow(row) && row.Node.IsExpanded
            && row.Node.Generation == generation && row.ProjectionVersion == version;
    }

    public void Collapse(FileTreeRow row)
    {
        if (!IsCurrentRow(row) || !row.CanExpand) return;
        row.ProjectionVersion = checked(row.ProjectionVersion + 1);
        _tree!.Collapse(row.Node);
        _expansions.Remove(row);
        RemoveDescendants(row);
        row.Refresh();
    }

    private async Task InsertChildrenAsync(IReadOnlyList<TreeNodeState> children, FileTreeRow? parent, Func<bool> canAdopt)
    {
        var offset = 0;
        FileTreeRow? previous = parent;
        while (offset < children.Count)
        {
            if (!canAdopt()) return;
            var watch = Stopwatch.StartNew();
            var batch = new List<FileTreeRow>(Math.Min(256, children.Count - offset));
            do
            {
                batch.Add(new(children[offset++], parent));
            } while (offset < children.Count && batch.Count < 256 && watch.Elapsed.TotalMilliseconds < 4);
            var index = previous is null ? 0 : IndexAfterSubtree(previous);
            if (index < 0) return;
            foreach (var row in batch) _visible.Add(row.Node, row);
            Rows.InsertRange(index, batch);
            previous = batch[^1];
            Changed(nameof(HasRows));
            if (offset < children.Count) await _yieldToUi();
        }
    }

    private int IndexAfterSubtree(FileTreeRow row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0) return -1;
        index++;
        while (index < Rows.Count && Rows[index].Depth > row.Depth) index++;
        return index;
    }

    private void RemoveDescendants(FileTreeRow row)
    {
        var start = Rows.IndexOf(row) + 1;
        var end = IndexAfterSubtree(row);
        for (var index = start; index < end; index++) _visible.Remove(Rows[index].Node);
        if (SelectedRow is { } selected && !IsCurrentRow(selected)) SelectedRow = row;
        if (end > start) Rows.RemoveRange(start, end - start);
        Changed(nameof(HasRows));
    }

    private static async Task YieldToDispatcherAsync() => await Dispatcher.Yield(DispatcherPriority.Background);
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
