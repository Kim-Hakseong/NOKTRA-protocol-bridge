namespace Pb.Core.Diagnostics;

/// <summary>Severity of a bridge log entry.</summary>
public enum BridgeLogLevel
{
    /// <summary>Normal progress: a route started, an endpoint connected.</summary>
    Info = 0,

    /// <summary>Something recoverable: a read failed, a reconnect is pending.</summary>
    Warning,

    /// <summary>Something that stops part of the bridge from working.</summary>
    Error,
}

/// <summary>
/// Where the bridge reports what it is doing. Kept to one method so the CLI, the monitor window
/// and tests can each supply a sink without pulling in a logging framework — the allowed
/// dependency list has none.
/// </summary>
public interface IBridgeLog
{
    /// <summary>Records one entry.</summary>
    void Write(BridgeLogLevel level, string source, string message, Exception? exception = null);
}

/// <summary>Convenience wrappers over <see cref="IBridgeLog.Write"/>.</summary>
public static class BridgeLogExtensions
{
    public static void Info(this IBridgeLog log, string source, string message)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(BridgeLogLevel.Info, source, message);
    }

    public static void Warn(this IBridgeLog log, string source, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(BridgeLogLevel.Warning, source, message, exception);
    }

    public static void Error(this IBridgeLog log, string source, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(BridgeLogLevel.Error, source, message, exception);
    }
}

/// <summary>A log that discards everything, used when no sink is supplied.</summary>
public sealed class NullBridgeLog : IBridgeLog
{
    public static NullBridgeLog Instance { get; } = new NullBridgeLog();

    public void Write(BridgeLogLevel level, string source, string message, Exception? exception = null)
    {
    }
}

/// <summary>A log that forwards each entry to a callback.</summary>
public sealed class DelegateBridgeLog : IBridgeLog
{
    private readonly Action<BridgeLogLevel, string, string, Exception?> _write;

    public DelegateBridgeLog(Action<BridgeLogLevel, string, string, Exception?> write) =>
        _write = write ?? throw new ArgumentNullException(nameof(write));

    public void Write(BridgeLogLevel level, string source, string message, Exception? exception = null) =>
        _write(level, source, message, exception);
}
