#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Phoenix
{
    /// <summary>
    /// Writes PhoenixSharp log entries to the process error console.
    /// </summary>
    /// <remarks>
    /// <see cref="MinimumLevel"/> defaults to <see cref="LogLevel.Info"/> so
    /// informational notices and failures are visible without enabling the
    /// higher-volume trace and debug streams.
    /// </remarks>
    public sealed class ConsoleLogger : ILogger
    {
        private readonly Func<DateTimeOffset> _getTimestamp;
        private readonly Func<TextWriter> _getWriter;
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
        /// Creates a logger that writes entries at Info and above to
        /// <see cref="Console.Error"/>.
        /// </summary>
        public ConsoleLogger() : this(
            () => Console.Error,
            () => DateTimeOffset.UtcNow
        )
        {
        }

        /// <summary>
        /// Creates a logger with the specified minimum level.
        /// </summary>
        public ConsoleLogger(LogLevel minimumLevel) : this()
        {
            MinimumLevel = minimumLevel;
        }

        internal ConsoleLogger(
            TextWriter writer,
            Func<DateTimeOffset> getTimestamp
        ) : this(
            () => writer,
            getTimestamp
        )
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }
        }

        private ConsoleLogger(
            Func<TextWriter> getWriter,
            Func<DateTimeOffset> getTimestamp
        )
        {
            _getWriter = getWriter
                ?? throw new ArgumentNullException(nameof(getWriter));
            _getTimestamp = getTimestamp
                ?? throw new ArgumentNullException(nameof(getTimestamp));
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

            _getWriter().WriteLine(
                DefaultLogFormatter.Format(
                    _getTimestamp(),
                    level,
                    source,
                    message,
                    exception
                )
            );
        }
    }

    internal static class DefaultLogFormatter
    {
        internal static string Format(
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
}
