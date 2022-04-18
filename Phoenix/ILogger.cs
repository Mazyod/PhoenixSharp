namespace Phoenix
{
    public enum LogLevel
    {
        Trace,
        Debug,
        Info,
        Warn,
        Error
    }

    public interface ILogger
    {
        void Log(LogLevel level, string source, string message);
    }
}