using System.Collections.Concurrent;

namespace SubLedger.Core;

/// <summary>
/// Runs queued jobs one at a time on a background thread, in the order they were queued. Keeps disk writes off the
/// game's main thread while preserving their order.
/// </summary>
public sealed class SerialWorker : IDisposable
{
    private readonly BlockingCollection<(string Name, Action Job)> queue = [];
    private readonly Thread thread;
    private readonly LedgerLog log;

    public SerialWorker(LedgerLog log)
    {
        this.log = log;
        thread = new Thread(Run) { IsBackground = true, Name = "SubLedger worker" };
        thread.Start();
    }

    public void Enqueue(string name, Action job)
    {
        if (queue.IsAddingCompleted)
            return;

        try
        {
            queue.Add((name, job));
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    private void Run()
    {
        foreach (var (name, job) in queue.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                log.Error("Worker", $"{name} failed", ex);
            }
        }
    }

    /// <summary>Finishes the jobs already queued (within a few seconds), then stops.</summary>
    public void Dispose()
    {
        queue.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(5));
    }
}
