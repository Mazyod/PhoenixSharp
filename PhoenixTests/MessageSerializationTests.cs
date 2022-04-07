using System.Collections.Generic;
using NUnit.Framework;
using Phoenix;
using Newtonsoft.Json.Linq;


namespace PhoenixTests {

	[TestFixture()]
	public class MessageSerializationTests {

		internal static Message sampleMessage {
			get {
				var payload = new Dictionary<string, object> {
					{ "some key", 12 },
					{ "another key", new Dictionary<string, object> {
						{ "nested", "value" }}},
				};

				return new Message(
					topic: "phoenix-test",
					@event: Message.OutBoundEvent.phx_join.ToString(),
					@ref: "123",
					payload: payload,
					joinRef: "456"
				);
			}
		}

		internal static Message replyMessage {
			get {
				var payload = new Dictionary<string, object> {
					{ "status", "ok" },
					{ "response", new Dictionary<string, object> {
						{ "some_key", 42 }
					}}
				};

				return new Message(
					topic: "phoenix-test",
					@event: Message.InBoundEvent.phx_reply.ToString(),
					@ref: "123",
					payload: payload,
					joinRef: "456"
				);
			}
		}


		[Test()]
		public void SerializationTest() {

			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(sampleMessage);
			var expected = @"['456','123','phoenix-test','phx_join',{'some key':12,'another key':{'nested':'value'}}]"
				.Replace("'", "\"");

			Assert.AreEqual(serialized, expected);
		}

		[Test()]
		public void DeserializationTest() {

			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(sampleMessage);
			var deserialized = serializer.Deserialize(serialized);
			// comparing payloads is tricky
			var deserializedNoPayload = deserialized;
			deserializedNoPayload.payload = null;

			var message = sampleMessage;
			message.payload = null;

			Assert.AreEqual(deserializedNoPayload, message);

			var payloadObject = deserialized.payload["another key"] as JObject;
			Assert.IsNotNull(payloadObject);
			Assert.AreEqual(payloadObject["nested"].ToObject<string>(), "value");
			Assert.IsNull(serializer.MapPayload<Reply>(deserialized.payload).status);
		}

		[Test()]
		public void NullJoinRefTest() {
			var serializer = new JSONMessageSerializer();
			var message = serializer.Deserialize(@"[null, null, null, null, null]");
			Assert.IsNull(message.joinRef);
		}

		[Test()]
		public void SerializingNullPayloadTest() {

			var message = new Message();
			Assert.IsNull(message.payload); // inconsistent with phoenix.js
		}

		[Test()]
		public void ReplyDeserializationTest() {

			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(replyMessage);
			var deserialized = serializer.Deserialize(serialized);
			// comparing payloads is tricky
			var deserializedNoPayload = deserialized;
			deserializedNoPayload.payload = null;

			var message = replyMessage;
			message.payload = null;

			Assert.AreEqual(deserializedNoPayload, message);
			Assert.IsInstanceOf(typeof(JObject), deserialized.payload["response"]);

			var reply = serializer.MapPayload<Reply>(deserialized.payload);
			Assert.AreEqual(reply.status, "ok");
			Assert.AreEqual(reply.response["some_key"], 42);
		}
	}
}

