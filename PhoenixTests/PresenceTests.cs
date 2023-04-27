using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture]
    public class PresenceTests
    {
        private static PresenceMeta ListByFirst(
            KeyValuePair<string, PresencePayload> container
        )
        {
            return container.Value.Metas.First();
        }

        private static Dictionary<string, PresencePayload> SampleState()
        {
            var root = new JObject
            {
                ["u1"] = JObject.FromObject(
                    SamplePresencePayload(1),
                    JsonMessageSerializer.Serializer
                )
            };

            return root.ToObject<Dictionary<string, PresencePayload>>(
                JsonMessageSerializer.Serializer
            );
        }

        private static Presence.Diff SampleDiff(
            Dictionary<string, PresencePayload> joins = null,
            Dictionary<string, PresencePayload> leaves = null
        )
        {
            var root = new JObject
            {
                ["joins"] = joins == null
                    ? new JObject()
                    : JObject.FromObject(joins, JsonMessageSerializer.Serializer),
                ["leaves"] = leaves == null
                    ? new JObject()
                    : JObject.FromObject(leaves, JsonMessageSerializer.Serializer)
            };

            return root.ToObject<Presence.Diff>(JsonMessageSerializer.Serializer);
        }

        private static PresencePayload SamplePresencePayload(uint id)
        {
            var root = new JObject
            {
                ["metas"] = new JArray
                {
                    new JObject
                    {
                        ["phx_ref"] = $"{id}",
                        ["id"] = id
                    }
                }
            };

            return root.ToObject<PresencePayload>(
                JsonMessageSerializer.Serializer
            );
        }

        /**
        syncs empty state
        */
        [Test]
        public void SyncsEmptyStateTest()
        {
            var newState = SampleState();
            // sanity check the serializer works
            Assert.AreEqual(1, newState["u1"].Metas.Count);

            var state = new Dictionary<string, PresencePayload>();
            var stateBefore = new Dictionary<string, PresencePayload>(state);
            Presence.SyncState(state, newState);
            CollectionAssert.AreEqual(stateBefore, state);

            state = Presence.SyncState(state, newState);
            CollectionAssert.AreEqual(newState, state);
        }

        [Test]
        public void InstanceSyncStateAndDiffsTest()
        {
            var channel = ChannelTests.TestChannel;
            // generate joinRef
            channel.Join();

            var presence = new Presence(channel);

            var user1 = SamplePresencePayload(1);
            var user2 = SamplePresencePayload(2);

            var newState = new Dictionary<string, object>
            {
                {"u1", user1},
                {"u2", user2}
            };

            var stateMessage = new Message(
                @event: "presence_state",
                payload: JsonBox.Serialize(newState)
            );
            channel.Trigger(stateMessage);

            var presenceList = presence.State
                .Select(ListByFirst)
                .ToList();

            Assert.AreEqual(2, presenceList.Count);
            Assert.AreEqual(1, presenceList[0].Payload.Unbox<JToken>().Value<int>("id"));
            Assert.AreEqual("1", presenceList[0].PhxRef);
            Assert.AreEqual(2, presenceList[1].Payload.Unbox<JToken>().Value<int>("id"));
            Assert.AreEqual("2", presenceList[1].PhxRef);

            var diffMessage = new Message(
                @event: "presence_diff",
                payload: JsonBox.Serialize(new Dictionary<string, object>
                {
                    {"joins", new Dictionary<string, object>()},
                    {
                        "leaves", new Dictionary<string, object>
                        {
                            {"u1", user1}
                        }
                    }
                })
            );
            channel.Trigger(diffMessage);
            presenceList = presence.State
                .Select(ListByFirst)
                .ToList();

            Assert.AreEqual(1, presenceList.Count);
            Assert.AreEqual(2, presenceList[0].Payload.Unbox<JToken>().Value<int>("id"));
            Assert.AreEqual("2", presenceList[0].PhxRef);
        }

        [Test]
        public void InstanceAppliesPendingDiffTest()
        {
            var channel = ChannelTests.TestChannel;
            // initialize the underlying socket adapter
            channel.Socket.Connect();
            // generate joinRef
            channel.Join();

            var presence = new Presence(channel);

            var usersJoined = new List<string>();
            var usersLeft = new List<string>();
            presence.OnJoin += (userId, _, _) => usersJoined.Add(userId);
            presence.OnLeave += (userId, _, _) => usersLeft.Add(userId);

            // new connection
            var user1 = SamplePresencePayload(1);
            var user2 = SamplePresencePayload(2);
            var user3 = SamplePresencePayload(3);

            var newState = new Dictionary<string, PresencePayload>
            {
                {"u1", user1},
                {"u2", user2}
            };

            var leaves = new Dictionary<string, PresencePayload>
            {
                {"u2", user2}
            };

            var presenceDiff = SampleDiff(
                leaves: leaves
            );

            var diffMessage = new Message(
                @event: "presence_diff",
                payload: JsonBox.Serialize(presenceDiff)
            );
            channel.Trigger(diffMessage);

            CollectionAssert.IsEmpty(presence.State.Select(ListByFirst));
            // pendingDiffs is private, can't assert on it

            var stateMessage = new Message(
                @event: "presence_state",
                payload: JsonBox.Serialize(newState)
            );
            channel.Trigger(stateMessage);

            CollectionAssert.AreEqual(new[] {"u2"}, usersLeft.ToArray());

            var presenceList = presence.State.Select(ListByFirst).ToArray();
            Assert.AreEqual(1, presenceList.Length);
            Assert.AreEqual(1, presenceList[0].Payload.Unbox<JToken>().Value<int>("id"));
            // pendingDiffs is private, can't assert on it
            CollectionAssert.AreEqual(new[] {"u1", "u2"}, usersJoined.ToArray());

            // disconnect and reconnect
            Assert.AreEqual(false, presence.InPendingSyncState());
            // simulated disconnect and reconnect
            channel.Trigger(Message.InBoundEvent.Error); // channel needs to be errored to trigger rejoin
            (channel.Socket.Conn as MockWebsocketAdapter)!.Connect(); // onOpen will trigger channel.Rejoin()
            Assert.AreEqual(true, presence.InPendingSyncState());

            presenceDiff = SampleDiff(
                leaves: new Dictionary<string, PresencePayload>
                {
                    {"u1", user1}
                }
            );

            diffMessage = new Message(
                @event: "presence_diff",
                payload: JsonBox.Serialize(presenceDiff)
            );
            channel.Trigger(diffMessage);

            presenceList = presence.State.Select(ListByFirst).ToArray();
            Assert.AreEqual(1, presenceList.Length);
            Assert.AreEqual(1, presenceList[0].Payload.Unbox<JToken>().Value<int>("id"));

            stateMessage = new Message(
                @event: "presence_state",
                payload: JsonBox.Serialize(new Dictionary<string, object>
                {
                    {"u1", user1},
                    {"u3", user3}
                })
            );
            channel.Trigger(stateMessage);

            presenceList = presence.State.Select(ListByFirst).ToArray();
            Assert.AreEqual(1, presenceList.Length);
            Assert.AreEqual(3, presenceList[0].Payload.Unbox<JToken>().Value<int>("id"));
        }

        /**
         * serialization tests
         */
        [Test]
        public void SerializationTest()
        {
            var serializer = new JsonMessageSerializer();
            var message = MessageSerializationTests.SampleMessage;
            message.Payload = serializer.Box(SampleState());

            // serialize
            var serialized = serializer.Serialize(message);
            Assert.IsTrue(serialized.Contains("u1"));

            // deserialize
            var deserialized = serializer.Deserialize<Message>(serialized);
            var payload = deserialized.Payload.Unbox<Dictionary<string, PresencePayload>>();

            Assert.IsNotEmpty(payload["u1"].Metas);
            Assert.AreEqual(1, payload["u1"].Metas[0].Payload.Unbox<JToken>().Value<int>("id"));
            Assert.AreEqual("1", payload["u1"].Metas[0].PhxRef);
        }
    }
}