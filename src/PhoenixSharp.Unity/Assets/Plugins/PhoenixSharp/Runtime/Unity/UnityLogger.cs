#nullable enable
using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Phoenix
{
    internal enum UnityLogOutput
    {
        Log,
        Warning,
        Error
    }

    internal static class UnityLogLevelMapping
    {
        internal static UnityLogOutput GetOutput(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Warn:
                    return UnityLogOutput.Warning;
                case LogLevel.Error:
                    return UnityLogOutput.Error;
                default:
                    return UnityLogOutput.Log;
            }
        }
    }

#if UNITY_5_3_OR_NEWER
    /// <summary>
    /// Writes PhoenixSharp entries through <see cref="UnityEngine.Debug.unityLogger"/>.
    /// </summary>
    /// <remarks>
    /// This type is isolated in the engine-enabled Phoenix.UnityLogger
    /// assembly. Its minimum level defaults to Info; Trace and Debug entries
    /// are suppressed until enabled.
    /// </remarks>
    public sealed class UnityLogger : ILogger
    {
        private int _minimumLevel = (int)LogLevel.Info;

        /// <summary>
        /// Gets or sets the least severe level that is written.
        /// </summary>
        public LogLevel MinimumLevel
        {
            get => (LogLevel)Volatile.Read(ref _minimumLevel);
            set => Volatile.Write(ref _minimumLevel, (int)value);
        }

        /// <summary>
        /// Creates a logger that writes entries at Info and above.
        /// </summary>
        public UnityLogger()
        {
        }

        /// <summary>
        /// Creates a logger with the specified minimum level.
        /// </summary>
        public UnityLogger(LogLevel minimumLevel)
        {
            MinimumLevel = minimumLevel;
        }

        public bool IsEnabled(LogLevel level, string source)
        {
            return level >= MinimumLevel;
        }

        public void Log(
            LogLevel level,
            string source,
            string message,
            Exception? exception
        )
        {
            if (!IsEnabled(level, source))
            {
                return;
            }

            var formatted = Format(
                DateTimeOffset.UtcNow,
                level,
                source,
                message,
                exception
            );
            switch (UnityLogLevelMapping.GetOutput(level))
            {
                case UnityLogOutput.Warning:
                    UnityEngine.Debug.unityLogger.LogWarning(
                        source,
                        formatted
                    );
                    break;
                case UnityLogOutput.Error:
                    UnityEngine.Debug.unityLogger.LogError(
                        source,
                        formatted
                    );
                    break;
                default:
                    UnityEngine.Debug.unityLogger.Log(
                        UnityEngine.LogType.Log,
                        formatted
                    );
                    break;
            }
        }

        private static string Format(
            DateTimeOffset timestamp,
            LogLevel level,
            string source,
            string message,
            Exception? exception
        )
        {
            var builder = new StringBuilder()
                .Append(
                    timestamp.ToString(
                        "O",
                        CultureInfo.InvariantCulture
                    )
                )
                .Append(" [")
                .Append(level)
                .Append("] [")
                .Append(source)
                .Append("] ")
                .Append(message);

            if (exception != null)
            {
                builder
                    .AppendLine()
                    .Append(exception);
            }

            return builder.ToString();
        }
    }
#endif
}
