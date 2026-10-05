namespace Clawd.Core;

/// <summary>Runs async work strictly one after another, in the order it was queued. Keystrokes,
/// terminal fits and permission answers share one so they reach Orca in order.</summary>
public sealed class SerialQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task<T> Enqueue<T>(Func<Task<T>> work)
    {
        lock (_gate)
        {
            var next = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            // A failure must not stop the queue; callers observe their own task.
            _tail = next.ContinueWith(_ => { }, TaskScheduler.Default);
            return next;
        }
    }

    public Task Enqueue(Func<Task> work) => Enqueue(async () => { await work().ConfigureAwait(false); return true; });
}
