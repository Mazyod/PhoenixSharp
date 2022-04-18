using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Phoenix
{
    public sealed class JsonMessageSerializer : IMessageSerializer
    {
        public string Serialize(Message message)
        {
            return new JArray(
                    message.JoinRef,
                    message.Ref,
                    message.Topic,
                    message.Event,
                    // phoenix.js: consistent with phoenix, also backwards compatible
                    // e.g. if the backend has handle_in(event, {}, socket)
                    message.Payload == null
                        ? new JObject()
                        : JObject.FromObject(message.Payload)
                )
                .ToString(
                    Formatting.None,
                    new StringEnumConverter()
                );
        }

        public Message Deserialize(string message)
        {
            var array = JArray.Parse(message);
            return new Message(
                joinRef: array[0].ToObject<string>(),
                @ref: array[1].ToObject<string>(),
                topic: array[2].ToObject<string>(),
                @event: array[3].ToObject<string>(),
                payload: array[4]
            );
        }

        public Reply? MapReply(object payload)
        {
            var jObject = JObject.FromObject(payload);
            return new Reply(
                jObject.Value<string>("status"),
                jObject["response"]
            );
        }

        public T MapPayload<T>(object payload)
        {
            return payload == null
                ? default
                : JToken.FromObject(payload).ToObject<T>();
        }
    }

    public static class JsonPayloadExtensions
    {
        public static T JsonResponse<T>(this Reply reply)
        {
            return ((JToken) reply.Response).ToObject<T>();
        }

        public static T JsonPayload<T>(this Message message)
        {
            return ((JToken) message.Payload).ToObject<T>();
        }
    }
}