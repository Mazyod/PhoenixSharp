using System;
using System.Collections.Generic;
using NUnit.Framework;
using Phoenix;
using Newtonsoft.Json.Linq;


namespace PhoenixTests {

	[TestFixture()]
	public class MessageSerializationTests {

		private Message sampleMessage {
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

		private Message replyMessage {
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
					payload: payload
				);
			}
		}


		[Test()]
		public void SerializationTest() {

			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(sampleMessage);
			var expected = "{"
				+ "\"topic\":\"phoenix-test\","
				+ "\"event\":\"phx_join\","
				+ "\"ref\":\"123\","
				+ "\"payload\":{\"some key\":12,\"another key\":{\"nested\":\"value\"}}"
				+ "}";

			Assert.AreEqual(serialized, expected);
		}

		[Test()]
		public void DeserializationTest() {

			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(sampleMessage);
			var deserialized = serializer.Deserialize(serialized);

			Assert.AreEqual(deserialized, sampleMessage);
			Assert.IsInstanceOf(typeof(JObject), deserialized.payload["another key"]);
			Assert.IsNull(deserialized.ParseReply());
		}

		[Test()]
		public void SerializingNullPayloadTest() {

			var message = new Message(null, null, null, null);
			Assert.IsNull(message.payload); // inconsistent with phoenix.js
		}

		[Test()]
		public void  ReplyDeserializationTest() {
			
			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(replyMessage);
			var deserialized = serializer.Deserialize(serialized);

			Assert.AreEqual(deserialized, replyMessage);
			Assert.IsInstanceOf(typeof(JObject), deserialized.payload["response"]);

			var reply = deserialized.ParseReply();
			Assert.AreEqual(reply.status, "ok");
			Assert.AreEqual(reply.response["some_key"], 42);
		}
	}
}

