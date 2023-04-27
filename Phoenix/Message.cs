using System;
using System.Runtime.Serialization;

namespace Phoenix
{
    public interface IJsonBox
    {
        T Unbox<T>();
    }

    public interface IMessageSerializer
    {
        string Serialize(object element);
        T Deserialize<T>(string message);

        IJsonBox Box(object element);
    }

    /**
     * A reply payload, in response to a push.
     */
    public readonly struct Reply
    {
        // PhoenixJS maps incoming phx_reply to chan_reply_{ref} when broadcasting the event
        public const string ReplyEventPrefix = "chan_reply_";

        public readonly string Status;
        public readonly IJsonBox Response;

        [IgnoreDataMember]
        public ReplyStatus ReplyStatus
        {
            get
            {
                if (Status == null) // shouldn't happen
                {
                    return ReplyStatus.Error;
                }

                return Status switch
                {
                    "ok" => ReplyStatus.Ok,
                    "error" => ReplyStatus.Error,
                    "timeout" => ReplyStatus.Timeout,
                    _ => throw new ArgumentException("Unknown status: " + Status)
                };
            }
        }

        public Reply(string status, IJsonBox response)
        {
            Status = status;
            Response = response;
        }
    }

    public enum ReplyStatus
    {
        Ok,
        Error,
        Timeout

        // extension methods also implemented below
    }


    public struct Message
    {
        public enum InBoundEvent
        {
            Reply,
            Close,
            Error

            // extension methods defined below
        }

        public enum OutBoundEvent
        {
            Join,
            Leave

            // extension methods defined below
        }


        public readonly string Topic;

        // unfortunate mutation of the original message
        public string Event;
        public readonly string Ref;
        public IJsonBox Payload;
        public string JoinRef;

        public Message(
            string topic = null,
            string @event = null,
            IJsonBox payload = null,
            string @ref = null,
            string joinRef = null
        )
        {
            Topic = topic;
            Event = @event;
            Payload = payload;
            Ref = @ref;
            JoinRef = joinRef;
        }
    }

    public static class ReplyStatusExtensions
    {
        /** 
         * Serialized value of the enum.
         * Is apparently much more performant than ToString.
         */
        public static string Serialized(this ReplyStatus status)
        {
            return status switch
            {
                ReplyStatus.Ok => "ok",
                ReplyStatus.Error => "error",
                ReplyStatus.Timeout => "timeout",
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
            };
        }
    }

    public static class MessageInBoundEventExtensions
    {
        /** 
         * Serialized value of the enum.
         * Is apparently much more performant than ToString.
         */
        public static string Serialized(this Message.InBoundEvent @event)
        {
            return @event switch
            {
                Message.InBoundEvent.Reply => "phx_reply",
                Message.InBoundEvent.Close => "phx_close",
                Message.InBoundEvent.Error => "phx_error",
                _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
            };
        }
    }

    public static class MessageOutBoundEventExtensions
    {
        /** 
         * Serialized value of the enum.
         * Is apparently much more performant than ToString.
         */
        public static string Serialized(this Message.OutBoundEvent @event)
        {
            return @event switch
            {
                Message.OutBoundEvent.Join => "phx_join",
                Message.OutBoundEvent.Leave => "phx_leave",
                _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
            };
        }
    }
}