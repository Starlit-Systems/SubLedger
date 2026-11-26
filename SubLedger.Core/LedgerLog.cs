namespace SubLedger.Core;

public enum LogLevel
{
    Verbose = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

public sealed record LogLine(long Sequence, DateTime Time, LogLevel Level, string Source, string Message);

/// <summary>
/// In-memory log shown on the Console page. Thread-safe; keeps the newest lines only.
/// </summary>
public sealed class LedgerLog
{
    private readonly object gate = new();
    private readonly LinkedList<LogLine> lines = new();
    private readonly int capacity;
    private long sequence;

    public LedgerLog(int capacity = 2000)
    {
        this.capacity = capacity;
    }

    /// <summary>Raised for every line, on the thread that logged it. Used to mirror lines into the host's log.</summary>
    public event Action<LogLine>? LineAdded;

    /// <summary>Changes whenever a line is added or the log is cleared.</summary>
    public long Version { get; private set; }

    public void Verbose(string source, string message) => Add(LogLevel.Verbose, source, message);
    public void Debug(string source, string message) => Add(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Add(LogLevel.Info, source, message);
    public void Warning(string source, string message) => Add(LogLevel.Warning, source, message);
    public void Error(string source, string message) => Add(LogLevel.Error, source, message);

    public void Error(string source, string message, Exception ex)
        => Add(LogLevel.Error, source, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public void Add(LogLevel level, string source, string message)
    {
        LogLine line;
        lock (gate)
        {
            line = new LogLine(++sequence, DateTime.Now, level, source, message);
            lines.AddLast(line);
            while (lines.Count > capacity)
                lines.RemoveFirst();
            Version++;
        }

        try
        {
            LineAdded?.Invoke(line);
        }
        catch
        {
            // A failing mirror must never break logging.
        }
    }

    public LogLine[] Snapshot()
    {
        lock (gate)
            return [.. lines];
    }

    public void Clear()
    {
        lock (gate)
        {
            lines.Clear();
            Version++;
        }
    }
}
