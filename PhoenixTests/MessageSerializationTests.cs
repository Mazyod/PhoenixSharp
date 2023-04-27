using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Phoenix;

namespace PhoenixTests
{
    [TestFixture]
    public class MessageSerializationTests
    {
        internal static Message SampleMessage =>
            new(
                "phoenix-test",
                Message.OutBoundEvent.Join.Serialized(),
                @ref: "123",
                payload: JsonBox.Serialize(new Dictionary<string, object>
                {
                    {"some key", 12},
                    {
                        "another key", new Dictionary<string, object>
                        {
                            {"nested", "value"}
                        }
                    }
                }),
                joinRef: "456"
            );

        private static Message ReplyMessage =>
            new(
                "phoenix-test",
                Message.InBoundEvent.Reply.Serialized(),
                @ref: "123",
                payload: JsonBox.Serialize(new Dictionary<string, object>
                {
                    {"status", "ok"},
                    {
                        "response", new Dictionary<string, object>
                        {
                            {"some_key", 42}
                        }
                    }
                }),
                joinRef: "456"
            );


        [Test]
        public void SerializationTest()
        {
            var serializer = new JsonMessageSerializer();
            var serialized = serializer.Serialize(SampleMessage);
            var expected = @"['456','123','phoenix-test','phx_join',{'some key':12,'another key':{'nested':'value'}}]"
                .Replace("'", "\"");

            Assert.AreEqual(expected, serialized);
        }

        [Test]
        public void SerializeNullPayloadTest()
        {
            var serializer = new JsonMessageSerializer();
            var message = SampleMessage;
            message.Payload = null;
            var serialized = serializer.Serialize(message);
            var expected = @"['456','123','phoenix-test','phx_join',{}]"
                .Replace("'", "\"");

            Assert.AreEqual(expected, serialized);
        }

        [Test]
        public void DeserializationTest()
        {
            var serializer = new JsonMessageSerializer();
            var serialized = serializer.Serialize(SampleMessage);
            var deserialized = serializer.Deserialize<Message>(serialized);
            // comparing payloads is tricky
            var deserializedNoPayload = deserialized;
            deserializedNoPayload.Payload = null;

            var message = SampleMessage;
            message.Payload = null;

            Assert.AreEqual(message, deserializedNoPayload);

            var payloadObject = deserialized.Payload.Unbox<JObject>();
            Assert.IsNotNull(payloadObject);
            Assert.IsNotNull(payloadObject["another key"]);
            Assert.IsNotNull(payloadObject["another key"]["nested"]);
            Assert.AreEqual("value", payloadObject["another key"]["nested"].ToObject<string>());
            Assert.IsNull(deserialized.Payload.Unbox<Reply>().Status);
        }

        [Test]
        public void NullJoinRefTest()
        {
            var serializer = new JsonMessageSerializer();
            var message = serializer.Deserialize<Message>(@"[null, null, null, null, null]");
            Assert.IsNull(message.JoinRef);
        }

        [Test]
        public void NullReplyStatusTest()
        {
            // shouldn't happen in practice
            Assert.AreEqual(ReplyStatus.Error, new Reply().ReplyStatus);
        }

        [Test]
        public void ReplyStatusSerializationTest()
        {
            Assert.AreEqual("ok", ReplyStatus.Ok.Serialized());
            Assert.AreEqual("error", ReplyStatus.Error.Serialized());
            Assert.AreEqual("timeout", ReplyStatus.Timeout.Serialized());
        }

        [Test]
        public void ReplyDeserializationTest()
        {
            var serializer = new JsonMessageSerializer();
            var serialized = serializer.Serialize(ReplyMessage);
            var deserialized = serializer.Deserialize<Message>(serialized);
            // comparing payloads is tricky
            var deserializedNoPayload = deserialized;
            deserializedNoPayload.Payload = null;

            var message = ReplyMessage;
            message.Payload = null;

            Assert.AreEqual(message, deserializedNoPayload);
            Assert.IsInstanceOf(typeof(JObject), deserialized.Payload.Unbox<JObject>());

            var reply = deserialized.Payload.Unbox<Reply?>();
            Assert.IsNotNull(reply);
            Assert.AreEqual("ok", reply.Value.Status);

            var response = reply.Value.Response.Unbox<JObject>();
            Assert.AreEqual(42, response.Value<int>("some_key"));
        }
    }
}