using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Phoenix;

namespace PhoenixTests {
	[TestFixture()]
	public class PresenceTests {

		private static Dictionary<string, object> sampleState {
			get {
				return new() {
					{
						"u1",
						new Dictionary<string, object> {
						{"metas", new List<Dictionary<string, object>> {
							new Dictionary<string, object> {
								{ "id", 1 },
								{ "phx_ref", "1" }
							}
						}}
					}
					}
				};
			}
		}

		private static Dictionary<string, List<Dictionary<string, object>>> sampleMetaContainer {
			get {
				return new() {
					{
						"metas",
						new List<Dictionary<string, object>> {
						new Dictionary<string, object> {
							{ "id", 1 },
							{ "phx_ref", "1" }
						}
					}
					}
				};
			}
		}

		/**
		syncs empty state
		*/
		[Test()]
		public void SyncsEmptyStateTest() {

			var serializer = new JSONMessageSerializer();
			var newState = serializer.MapPayload<
				Dictionary<string, Presence.MetadataContainer>
			>(sampleState);
			// sanity check the serializer works
			Assert.AreEqual(newState["u1"].metas.Count, 1);

			var state = new Dictionary<string, Presence.MetadataContainer>();
			var stateBefore = new Dictionary<string, Presence.MetadataContainer>(state);
			Presence.SyncState(state, newState);
			CollectionAssert.AreEqual(state, stateBefore);

			state = Presence.SyncState(state, newState);
			CollectionAssert.AreEqual(state, newState);
		}

		[Test()]
		public void InstanceSyncStateAndDiff() {

			var channel = ChannelTests.channel;
			// generate joinRef
			channel.Join();

			var presence = new Presence(channel);

			var user1 = sampleMetaContainer;
			var user2 = sampleMetaContainer;
			user2["metas"][0]["id"] = 2;
			user2["metas"][0]["phx_ref"] = "2";

			var newState = new Dictionary<string, object>() {
				{ "u1", user1 },
				{ "u2", user2 }
			};

			var stateMessage = new Message(
				@event: "presence_state",
				payload: newState
			);
			channel.Trigger(stateMessage);
			static object listByFirst(
				KeyValuePair<string, Presence.MetadataContainer> container
			) => container.Value.metas.First();

			var presenceList = presence.List(listByFirst)
				.Select(o => o as Dictionary<string, object>)
				.ToList();

			Assert.AreEqual(presenceList.Count, 2);
			Assert.AreEqual(presenceList[0]["id"], 1);
			Assert.AreEqual(presenceList[0]["phx_ref"], "1");
			Assert.AreEqual(presenceList[1]["id"], 2);
			Assert.AreEqual(presenceList[1]["phx_ref"], "2");

			var diffMessage = new Message(
				@event: "presence_diff",
				payload: new Dictionary<string, object>() {
					{ "joins", new Dictionary<string, object>() },
					{ "leaves", new Dictionary<string, object>() {
						{ "u1", user1 }
					}}
				}
			);
			channel.Trigger(diffMessage);
			presenceList = presence.List(listByFirst)
				.Select(o => o as Dictionary<string, object>)
				.ToList();

			Assert.AreEqual(presenceList.Count, 1);
			Assert.AreEqual(presenceList[0]["id"], 2);
			Assert.AreEqual(presenceList[0]["phx_ref"], "2");
		}

		/**
		serialization tests
		*/
		[Test()]
		public void SerializationTest() {

			var message = MessageSerializationTests.sampleMessage;
			message.payload = sampleState;

			// serialize
			var serializer = new JSONMessageSerializer();
			var serialized = serializer.Serialize(message);
			Assert.IsTrue(serialized.Contains("u1"));

			// deserialize
			var deserialized = serializer.Deserialize(serialized);
			var payload = serializer.MapPayload<
				Dictionary<string, Presence.MetadataContainer>
			>(deserialized.payload);
			var rawMetas = (
				sampleState["u1"] as Dictionary<string, object>
			)["metas"] as List<Dictionary<string, object>>;

			CollectionAssert.AreEqual(payload["u1"].metas, rawMetas);
		}
	}
}

