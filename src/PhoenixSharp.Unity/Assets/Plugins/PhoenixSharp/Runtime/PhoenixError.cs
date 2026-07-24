#nullable enable
using System;

namespace Phoenix
{
    /// <summary>
    /// Identifies the subsystem that surfaced a Phoenix error.
    /// </summary>
    public enum PhoenixErrorKind
    {
        Unknown,
        Transport,
        Send,
        Heartbeat,
        Serialization,
        Dispatch
    }

    /// <summary>
    /// Describes a contained Phoenix runtime error.
    /// </summary>
    public sealed class PhoenixError
    {
        public string Message { get; }
        public PhoenixErrorKind Kind { get; }
        public Exception? Exception { get; }

        public PhoenixError(
            string message,
            PhoenixErrorKind kind,
            Exception? exception = null
        )
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Kind = kind;
            Exception = exception;
        }
    }
}
