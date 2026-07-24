#nullable enable
using System;

namespace Phoenix
{
    /// <summary>
    /// Identifies the severity of a PhoenixSharp log entry.
    /// </summary>
    /// <remarks>
    /// Values are ordered from least to most severe so implementations can use
    /// a direct comparison for minimum-level filtering.
    /// </remarks>
    public enum LogLevel
    {
        Trace,
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Stable source names emitted by PhoenixSharp runtime logging.
    /// </summary>
    /// <remarks>
    /// Sources remain strings so sinks can route PhoenixSharp entries alongside
    /// application-defined sources without an enum conversion layer.
    /// </remarks>
    public static class LogSource
    {
        /// <summary>WebSocket lifecycle and heartbeat activity.</summary>
        public const string Transport = "transport";

        /// <summary>Channel lifecycle and event-dispatch activity.</summary>
        public const string Channel = "channel";

        /// <summary>Outbound Phoenix message activity.</summary>
        public const string Push = "push";

        /// <summary>Socket callbacks, parsing, and dispatch containment.</summary>
        public const string Socket = "socket";

        /// <summary>Inbound Phoenix message activity.</summary>
        public const string Receive = "receive";
    }

    /// <summary>
    /// Receives structured PhoenixSharp runtime log entries.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw from either method. PhoenixSharp does not
    /// contain sink exceptions; they propagate to the calling context.
    /// </remarks>
    public interface ILogger
    {
        /// <summary>
        /// Returns whether an entry at <paramref name="level"/> from
        /// <paramref name="source"/> should be formatted and written.
        /// </summary>
        /// <remarks>
        /// Runtime hot paths call this before constructing log messages.
        /// Implementations should keep this method fast and allocation-free and
        /// must not throw.
        /// </remarks>
        bool IsEnabled(LogLevel level, string source);

        /// <summary>
        /// Writes an enabled log entry.
        /// </summary>
        /// <param name="level">The entry severity.</param>
        /// <param name="source">A source from <see cref="LogSource"/>.</param>
        /// <param name="message">Contextual message text.</param>
        /// <param name="exception">
        /// The original exception for exception-bearing entries; otherwise null.
        /// </param>
        /// <remarks>
        /// Implementations must not throw. A sink exception propagates to the
        /// runtime operation that emitted the entry.
        /// </remarks>
        void Log(
            LogLevel level,
            string source,
            string message,
            Exception? exception
        );
    }
}
