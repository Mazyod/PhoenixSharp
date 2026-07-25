using System;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class SocketPerformanceTests
    {
        private const int Iterations = 10_000;
        private const int MaxDispatchBytesPerMessage = 128;
        private const int MaxReplyBytesPerMessage = 144;
        private const int MeasurementRuns = 5;
        private const int WarmupIterations = 1_000;

        [Test, Category("Performance")]
        public void ReceiveDispatchAllocationStaysWithinBudgetTest()
        {
            var message = new Message(
                topic: "test",
                @event: "custom_event"
            );
            var serializer = new ReusingMessageSerializer(message);
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(serializer)
                {
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            var connection = factory.LastCreatedWebsocket!;
            var channel = socket.Channel("test");
            channel.On("custom_event", static _ => { });

            DriveMessages(connection, WarmupIterations);

            var allocatedBytes = new long[MeasurementRuns];
            for (var run = 0; run < MeasurementRuns; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                DriveMessages(connection, Iterations);
                allocatedBytes[run] =
                    GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Array.Sort(allocatedBytes);
            var medianBytesPerMessage =
                allocatedBytes[MeasurementRuns / 2] / (double)Iterations;
            TestContext.Out.WriteLine(
                $"Median receive dispatch allocation: "
                + $"{medianBytesPerMessage:F2} bytes/message."
            );

            Assert.That(
                medianBytesPerMessage,
                Is.LessThanOrEqualTo(MaxDispatchBytesPerMessage),
                $"Median receive dispatch allocation was "
                + $"{medianBytesPerMessage:F2} bytes/message."
            );
        }

        [Test, Category("Performance")]
        public void ReceiveReplyAllocationStaysWithinBudgetTest()
        {
            var message = new Message(
                topic: "test",
                @event: Message.InBoundEvent.Reply.Serialized(),
                @ref: "7"
            );
            var serializer = new ReusingMessageSerializer(message);
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(serializer)
                {
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            var connection = factory.LastCreatedWebsocket!;
            socket.Channel("test");

            DriveMessages(connection, WarmupIterations);

            var allocatedBytes = new long[MeasurementRuns];
            for (var run = 0; run < MeasurementRuns; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                DriveMessages(connection, Iterations);
                allocatedBytes[run] =
                    GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Array.Sort(allocatedBytes);
            var medianBytesPerMessage =
                allocatedBytes[MeasurementRuns / 2] / (double)Iterations;
            TestContext.Out.WriteLine(
                $"Median reply receive allocation: "
                + $"{medianBytesPerMessage:F2} bytes/message."
            );

            Assert.That(
                medianBytesPerMessage,
                Is.LessThanOrEqualTo(MaxReplyBytesPerMessage),
                $"Median reply receive allocation was "
                + $"{medianBytesPerMessage:F2} bytes/message."
            );
        }

        private static void DriveMessages(
            MockWebsocketAdapterWithCallbacks connection,
            int count
        )
        {
            for (var i = 0; i < count; i++)
            {
                connection.SimulateMessage(string.Empty);
            }
        }

        private sealed class ReusingMessageSerializer : IMessageSerializer
        {
            private readonly Message _message;

            public ReusingMessageSerializer(Message message)
            {
                _message = message;
            }

            public string Serialize(object? element)
            {
                throw new NotSupportedException();
            }

            public T Deserialize<T>(string message)
            {
                return (T)(object)_message;
            }

            public IJsonBox Box(object? element)
            {
                throw new NotSupportedException();
            }
        }
    }
}
