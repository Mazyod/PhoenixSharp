#nullable enable
using System;

namespace Phoenix
{
    /// <summary>
    /// Base exception for Phoenix runtime and protocol failures.
    /// </summary>
    public class PhoenixException : Exception
    {
        public PhoenixException(string message)
            : base(message)
        {
        }

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
        public PhoenixConnectionException(string message)
            : base(message)
        {
        }

        public PhoenixConnectionException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
