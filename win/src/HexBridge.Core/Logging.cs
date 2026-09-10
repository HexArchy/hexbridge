namespace HexBridge;

public enum LogLevel { Info, Warning, Error }

public readonly record struct LogEntry(DateTime At, LogLevel Level, string Message)
{
    public override string ToString() => $"{At.ToLocalTime():HH:mm:ss}  {Message}";
}
