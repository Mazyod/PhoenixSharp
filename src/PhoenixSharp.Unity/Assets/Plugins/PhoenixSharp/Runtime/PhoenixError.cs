#nullable enable
using System;

namespace Phoenix
{
    /// <summary>
    /// Identifies the subsystem that surfaced a Phoenix error.
    /// </summary>
    public enum PhoenixErrorKind
    {
        /// <summary>A WebSocket lifecycle or adapter failure.</summary>
        Transport = 1,

        /// <summary>A failure while sending an encoded message.</summary>
        Send = 2,

        /// <summary>A missed heartbeat or heartbeat-processing failure.</summary>
        Heartbeat = 3,

        /// <summary>A message encoding or decoding failure.</summary>
        Serialization = 4,

        /// <summary>A failure while dispatching a message or callback.</summary>
        Dispatch = 5
    }

    /// <summary>
    /// Describes a contained Phoenix runtime error.
    /// </summary>
    public sealed class PhoenixError
    {
        /// <summary>Gets the description of the failure.</summary>
        public string Message { get; }

        /// <summary>Gets the subsystem that surfaced the failure.</summary>
        public PhoenixErrorKind Kind { get; }

        /// <summary>Gets the originating exception, when available.</summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Creates a contained Phoenix runtime error.
        /// </summary>
        /// <param name="message">The failure description.</param>
        /// <param name="kind">The subsystem that surfaced the failure.</param>
        /// <param name="exception">The originating exception, if any.</param>
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
