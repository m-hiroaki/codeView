using System.Collections.Concurrent;

namespace CodeView.Tests;

/// <summary>Models serialized UI continuations without starting a native UI or changing document rules.</summary>
internal sealed class SingleThreadTestContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();

    public override void Post(SendOrPostCallback callback, object? state) => _work.Add((callback, state));

    public static void Run(Func<Task> operation)
    {
        var previous = Current;
        using var context = new SingleThreadTestContext();
        SetSynchronizationContext(context);
        try
        {
            var task = operation();
            while (!task.IsCompleted)
            {
                if (!context._work.TryTake(out var work, TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("UI相当の継続処理が完了しませんでした。");
                work.Callback(work.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }

    public void Dispose() => _work.Dispose();
}
