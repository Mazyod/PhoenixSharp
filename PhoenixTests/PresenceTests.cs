using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture]
    public class PresenceTests
    {
        private static object ListByFirst(
            KeyValuePair<string, Presence.MetadataContainer> container
        )
        {
            return container.Value.Metas.First();
        }

        private static Dictionary<string, object> SampleState =>
            new()
            {
                {
                    "u1",
                    new Dictionary<string, object>
                    {
                        {
                            "metas", new List<Dictionary<string, object>>
                            {
                                new()
                                {
                                    {"id", 1},
                                    {"phx_ref", "1"}
                                }
                            }
                        }
                    }
                }
            };

        private static Dictionary<string, object> SampleDiff(
            Dictionary<string, object> joins = null,
            Dictionary<string, object> leaves = null
        )
        {
            return new Dictionary<string, object>
            {
                {
                    "joins",
                    joins ?? new Dictionary<string, object>()
                },
                {
                    "leaves",
                    leaves ?? new Dictionary<string, object>()
                }
            };
        }

        private static Dictionary<string, List<Dictionary<string, object>>> SampleMetaContainer(uint id)
        {
            return new Dictionary<string, List<Dictionary<string, object>>>
            {
                {
                    "metas",
                    new List<Dictionary<string, object>>
                    {
                        new()
                        {
                            {"id", id},
                            {"phx_ref", $"{id}"}
                        }
                    }
                }
            };
        }

        /**
        syncs empty state
        */
        [Test]
        public void SyncsEmptyStateTest()
        {
            var serializer = new JsonMessageSerializer();
            var newState = serializer.MapPayload<
                Dictionary<string, Presence.MetadataContainer>
            >(SampleState);
            // sanity check the serializer works
            Assert.AreEqual(1, newState["u1"].Metas.Count);

            var state = new Dictionary<string, Presence.MetadataContainer>();
            var stateBefore = new Dictionary<string, Presence.MetadataContainer>(state);
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

            var user1 = SampleMetaContainer(1);
            var user2 = SampleMetaContainer(2);

            var newState = new Dictionary<string, object>
            {
                {"u1", user1},
                {"u2", user2}
            };

            var stateMessage = new Message(
                @event: "presence_state",
                payload: newState
            );
            channel.Trigger(stateMessage);

            var presenceList = presence.List(ListByFirst)
                .Select(o => o as Dictionary<string, object>)
                .ToList();

            Assert.AreEqual(2, presenceList.Count);
            Assert.AreEqual(1, presenceList[0]["id"]);
            Assert.AreEqual("1", presenceList[0]["phx_ref"]);
            Assert.AreEqual(2, presenceList[1]["id"]);
            Assert.AreEqual("2", presenceList[1]["phx_ref"]);

            var diffMessage = new Message(
                @event: "presence_diff",
                payload: new Dictionary<string, object>
                {
                    {"joins", new Dictionary<string, object>()},
                    {
                        "leaves", new Dictionary<string, object>
                        {
                            {"u1", user1}
                        }
                    }
                }
            );
            channel.Trigger(diffMessage);
            presenceList = presence.List(ListByFirst)
                .Select(o => o as Dictionary<string, object>)
                .ToList();

            Assert.AreEqual(1, presenceList.Count);
            Assert.AreEqual(2, presenceList[0]["id"]);
            Assert.AreEqual("2", presenceList[0]["phx_ref"]);
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
            var user1 = SampleMetaContainer(1);
            var user2 = SampleMetaContainer(2);
            var user3 = SampleMetaContainer(3);

            var newState = new Dictionary<string, object>
            {
                {"u1", user1},
                {"u2", user2}
            };

            var leaves = new Dictionary<string, object>
            {
                {"u2", user2}
            };

            var presenceDiff = SampleDiff(
                leaves: leaves
            );

            var diffMessage = new Message(
                @event: "presence_diff",
                payload: presenceDiff
            );
            channel.Trigger(diffMessage);

            CollectionAssert.IsEmpty(presence.List(ListByFirst));
            // pendingDiffs is private, can't assert on it

            var stateMessage = new Message(
                @event: "presence_state",
                payload: newState
            );
            channel.Trigger(stateMessage);

            CollectionAssert.AreEqual(new[] {"u2"}, usersLeft.ToArray());

            var presenceList = presence.List(ListByFirst).ToArray();
            Assert.AreEqual(1, presenceList.Length);
            Assert.AreEqual(1, (presenceList[0] as Dictionary<string, object>)!["id"]);
            // pendingDiffs is private, can't assert on it
            CollectionAssert.AreEqual(new[] {"u1", "u2"}, usersJoined.ToArray());

            // disconnect and reconnect
            Assert.AreEqual(false, presence.InPendingSyncState());
            // simulated disconnect and reconnect
            channel.Trigger(Message.InBoundEvent.Error); // channel needs to be errored to trigger rejoin
            (channel.Socket.Conn as MockWebsocketAdapter)!.Connect(); // onOpen will trigger channel.Rejoin()
            Assert.AreEqual(true, presence.InPendingSyncState());

            presenceDiff = SampleDiff(
                leaves: new Dictionary<string, object>
                {
                    {"u1", user1}
                }
            );

            diffMessage = new Message(
                @event: "presence_diff",
                payload: presenceDiff
            );
            channel.Trigger(diffMessage);

            presenceList = presence.List(ListByFirst).ToArray();
            Assert.AreEqual(1, presenceList.Length);
            Assert.AreEqual(1, (presenceList[0] as Dictionary<string, object>)!["id"]);

            stateMessage = new Message(
                @event: "presence_state",
                payload: new Dictionary<string, object>
                {
                    {"u1", user1},
                    {"u3", user3}
                }
            );
            channel.Trigger(stateMessage);

            presenceList = presence.List(ListByFirst).ToArray();
            Assert.AreEqual(1, presenceList.Length);
            Assert.AreEqual(3, (presenceList[0] as Dictionary<string, object>)!["id"]);
        }

        /**
         * serialization tests
         */
        [Test]
        public void SerializationTest()
        {
            var message = MessageSerializationTests.SampleMessage;
            message.Payload = SampleState;

            // serialize
            var serializer = new JsonMessageSerializer();
            var serialized = serializer.Serialize(message);
            Assert.IsTrue(serialized.Contains("u1"));

            // deserialize
            var deserialized = serializer.Deserialize(serialized);
            var payload = serializer.MapPayload<
                Dictionary<string, Presence.MetadataContainer>
            >(deserialized.Payload);
            var userState = (SampleState["u1"] as Dictionary<string, object>)!;
            var rawMetas = userState["metas"] as List<Dictionary<string, object>>;

            CollectionAssert.AreEqual(rawMetas, payload["u1"].Metas);
        }
    }
}