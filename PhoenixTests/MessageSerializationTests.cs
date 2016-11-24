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
				var payload = new Dictionary<string, object>() {
					{ "some key", 12 },
					{ "another key", new Dictionary<string, object>() { 
							{ "nested", "value" }}},
				};

				return new Message() {
					topic = "phoenix-test",
					@ref = "123",
					@event = "somevalue",
					payload = payload
				};
			}
		}


		[Test()]
		public void SerializationTest() {

			var serialized = sampleMessage.Serialize();
			var expected = "{"
				+ "\"topic\":\"phoenix-test\","
				+ "\"payload\":{\"some key\":12,\"another key\":{\"nested\":\"value\"}},"
				+ "\"event\":\"somevalue\","
				+ "\"ref\":\"123\""
				+ "}";

			Assert.AreEqual(serialized, expected);
		}

		[Test()]
		public void DeserializationTest() {

			var serialized = sampleMessage.Serialize();
			var deserialized = MessageSerialization.Deserialize(serialized);

			Assert.AreEqual(deserialized, sampleMessage);
			Assert.IsNotInstanceOf(typeof(JObject), deserialized.payload["another key"]);
			CollectionAssert.AreEquivalent(deserialized.payload, sampleMessage.payload);
		}

		[Test()]
		public void SerializingNullPayloadTest() {

			var message = sampleMessage;
			message.payload = null;

			var serialized = message.Serialize();
			var expected = "{"
				+ "\"topic\":\"phoenix-test\","
				+ "\"payload\":{}," // consistent with phoenix.js
				+ "\"event\":\"somevalue\","
				+ "\"ref\":\"123\""
				+ "}";

			Assert.AreEqual(serialized, expected);
		}
	}
}

