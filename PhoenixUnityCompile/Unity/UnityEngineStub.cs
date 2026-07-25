#nullable enable

namespace UnityEngine
{
    public enum LogType
    {
        Log
    }

    public interface ILogger
    {
        void Log(LogType logType, object message);
        void LogWarning(string tag, object message);
        void LogError(string tag, object message);
    }

    public static class Debug
    {
        public static ILogger unityLogger => null!;
    }
}
