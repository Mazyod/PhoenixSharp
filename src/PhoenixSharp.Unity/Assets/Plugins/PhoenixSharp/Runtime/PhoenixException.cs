#nullable enable
using System;

namespace Phoenix
{
    /// <summary>
    /// Base exception for Phoenix runtime and protocol failures.
    /// </summary>
    public class PhoenixException : Exception
    {
        /// <summary>
        /// Creates a Phoenix exception with the specified message.
        /// </summary>
        /// <param name="message">The error message.</param>
        public PhoenixException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Creates a Phoenix exception with an originating exception.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The exception that caused this failure.</param>
        public PhoenixException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Represents a failure to establish a Phoenix socket connection.
    /// </summary>
    public sealed class PhoenixConnectionException : PhoenixException
    {
        /// <summary>
        /// Creates a connection exception with the specified message.
        /// </summary>
        /// <param name="message">The error message.</param>
        public PhoenixConnectionException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Creates a connection exception with an originating exception.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The exception that caused this failure.</param>
        public PhoenixConnectionException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
