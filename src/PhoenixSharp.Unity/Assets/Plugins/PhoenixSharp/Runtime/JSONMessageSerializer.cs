#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Phoenix
{
    /**
     * An adapter for abstracting over the JSON library used.
     */
    public sealed class JsonBox : IJsonBox
    {
        /// <summary>
        /// Maximum number of UTF-16 characters returned by <see cref="ToString"/>.
        /// </summary>
        public const int MaximumToStringLength = 4_096;

        private const string TruncationSuffix = "...[truncated]";

        public readonly JToken Element;

        public JsonBox(JToken element)
        {
            Element = element;
        }

        public static JsonBox Serialize(object? obj)
        {
            var token = obj == null
                ? JValue.CreateNull()
                : JToken.FromObject(obj, JsonMessageSerializer.Serializer);

            return new JsonBox(token);
        }

        [return: MaybeNull]
        public T Unbox<T>()
        {
            return Element.ToObject<T>(JsonMessageSerializer.Serializer);
        }

        /// <summary>
        /// Renders the boxed value as compact JSON for diagnostics.
        /// </summary>
        /// <remarks>
        /// Output longer than <see cref="MaximumToStringLength"/> UTF-16
        /// characters is prefix-truncated and ends with
        /// <c>...[truncated]</c>. The result is diagnostic text and is not
        /// guaranteed to remain valid JSON after truncation.
        /// </remarks>
        public override string ToString()
        {
            var json = Element.ToString(Formatting.None);
            if (json.Length <= MaximumToStringLength)
            {
                return json;
            }

            return json.Substring(
                    0,
                    MaximumToStringLength - TruncationSuffix.Length
                )
                + TruncationSuffix;
        }
    }

    public sealed class JsonMessageSerializer : IMessageSerializer
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Converters =
            {
                new StringEnumConverter(),
                new JsonBoxConverter(),
                new MessageConverter(),
                new PresencePayloadConverter(),
                new PresenceMetaConverter()
            },
            Formatting = Formatting.None
        };

        internal static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public string Serialize(object? element)
        {
            return JsonConvert.SerializeObject(element, Formatting.None, Settings);
        }

        [return: MaybeNull]
        public T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        public IJsonBox Box(object? element)
        {
            return JsonBox.Serialize(element);
        }
    }

    internal sealed class JsonBoxConverter : JsonConverter<IJsonBox?>
    {
        public override void WriteJson(JsonWriter writer, IJsonBox? value, JsonSerializer serializer)
        {
            var element = value?.Unbox<JToken>();
            if (serializer.NullValueHandling != NullValueHandling.Ignore)
            {
                element ??= JValue.CreateNull();
            }

            element?.WriteTo(writer);
        }

        public override IJsonBox ReadJson(JsonReader reader, Type objectType, IJsonBox? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            return new JsonBox(JToken.Load(reader));
        }
    }

    internal sealed class MessageConverter : JsonConverter<Message>
    {
        public override void WriteJson(JsonWriter writer, Message? value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            // phoenix.js: consistent with phoenix, also backwards compatible
            // e.g. if the backend has handle_in(event, {}, socket)
            var payload = value.Payload?.Unbox<JToken>();
            if (payload == null || payload.Type == JTokenType.Null || payload.Type == JTokenType.Undefined)
            {
                payload = new JObject();
            }

            writer.WriteStartArray();
            writer.WriteValue(value.JoinRef);
            writer.WriteValue(value.Ref);
            writer.WriteValue(value.Topic);
            writer.WriteValue(value.Event);
            payload.WriteTo(writer);
            writer.WriteEndArray();
        }

        public override Message ReadJson(JsonReader reader, Type objectType, Message? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            if (!(token is JArray array))
            {
                throw new JsonSerializationException(
                    $"expected Phoenix V2 frame to be a JSON array, got {token.Type}"
                );
            }

            // Deliberately stricter than phoenix.js array destructuring: reject both
            // truncated and extended frames instead of silently ignoring extra elements.
            if (array.Count != 5)
            {
                throw new JsonSerializationException(
                    $"expected 5-element Phoenix V2 frame, got {array.Count}"
                );
            }

            return new Message(
                joinRef: array[0].ToObject<string?>(),
                @ref: array[1].ToObject<string?>(),
                topic: array[2].ToObject<string?>(),
                @event: array[3].ToObject<string?>(),
                payload: new JsonBox(array[4])
            );
        }
    }

    internal sealed class PresencePayloadConverter : JsonConverter<PresencePayload?>
    {
        public override void WriteJson(JsonWriter writer, PresencePayload? value, JsonSerializer serializer)
        {
            value?.Payload?.Unbox<JToken>()?.WriteTo(writer);
        }

        public override PresencePayload ReadJson(JsonReader reader, Type objectType, PresencePayload? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var obj = JObject.Load(reader);
            return new PresencePayload
            {
                Metas = obj["metas"]?.ToObject<List<PresenceMeta>>(serializer) ?? new List<PresenceMeta>(),
                Payload = new JsonBox(obj)
            };
        }
    }

    internal sealed class PresenceMetaConverter : JsonConverter<PresenceMeta?>
    {
        public override void WriteJson(JsonWriter writer, PresenceMeta? value, JsonSerializer serializer)
        {
            value?.Payload?.Unbox<JToken>()?.WriteTo(writer);
        }

        public override PresenceMeta ReadJson(JsonReader reader, Type objectType, PresenceMeta? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var meta = JObject.Load(reader);
            return new PresenceMeta
            {
                PhxRef = meta.Value<string>("phx_ref") ?? string.Empty,
                Payload = new JsonBox(meta)
            };
        }
    }
}
