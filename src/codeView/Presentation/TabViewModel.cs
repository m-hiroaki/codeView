using System.ComponentModel;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed class TabViewModel(TabState state) : INotifyPropertyChanged
{
    public TabState State { get; } = state;
    public string Name => Path.GetFileName(State.Path.FullPath);
    public string FullPath => State.Path.FullPath;
    public string Label { get; private set; } = Path.GetFileName(state.Path.FullPath);
    public string StatusText => State.Status switch
    {
        TabStatus.Loading => "読込中",
        TabStatus.Reloading => "再読込中",
        TabStatus.Failed => State.WasCancelled ? "取消" : "失敗",
        _ => ""
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh(bool duplicateName)
    {
        Label = duplicateName ? Name + " — " + Path.GetDirectoryName(FullPath) : Name;
        PropertyChanged?.Invoke(this, new(nameof(Label)));
        PropertyChanged?.Invoke(this, new(nameof(StatusText)));
    }
}
