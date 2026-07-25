using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public sealed class ChannelConcurrencyTests : PhoenixTestBase
    {
        [Test]
        public void ParallelJoinCheckAndSetIsGuardedByStateLockTest()
        {
            using var socket = CreateSocket(new MockDelayedExecutor());
            var channel = socket.Channel("join:fence");
            var stateLock = GetPrivateField<object>(
                channel,
                "_stateLock"
            );
            var joinedOnceField = typeof(Channel).GetField(
                "_joinedOnce",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            using var startGate = new ManualResetEventSlim(false);
            using var callersReady = new CountdownEvent(2);
            var successCount = 0;
            var duplicateJoinCount = 0;
            Exception? unexpectedException = null;

            Thread CreateJoinThread()
            {
                return new Thread(() =>
                {
                    startGate.Wait();
                    callersReady.Signal();
                    try
                    {
                        channel.Join();
                        Interlocked.Increment(ref successCount);
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref duplicateJoinCount);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(
                            ref unexpectedException,
                            ex,
                            null
                        );
                    }
                });
            }

            var firstThread = CreateJoinThread();
            var secondThread = CreateJoinThread();
            bool joinedOnceWhileLockHeld;
            lock (stateLock)
            {
                firstThread.Start();
                secondThread.Start();
                startGate.Set();
                Assert.That(
                    callersReady.Wait(TimeSpan.FromSeconds(1)),
                    Is.True,
                    "Parallel Join callers did not start."
                );
                Assert.That(
                    SpinWait.SpinUntil(
                        () => IsWaitingOrStopped(firstThread)
                            && IsWaitingOrStopped(secondThread),
                        TimeSpan.FromSeconds(1)
                    ),
                    Is.True,
                    "Parallel Join callers did not reach the state-lock boundary."
                );
                joinedOnceWhileLockHeld =
                    (bool)joinedOnceField.GetValue(channel)!;
            }

            Assert.That(
                firstThread.Join(TimeSpan.FromSeconds(1)),
                Is.True
            );
            Assert.That(
                secondThread.Join(TimeSpan.FromSeconds(1)),
                Is.True
            );
            Assert.Multiple(() =>
            {
                Assert.That(joinedOnceWhileLockHeld, Is.False);
                Assert.That(successCount, Is.EqualTo(1));
                Assert.That(duplicateJoinCount, Is.EqualTo(1));
                Assert.That(unexpectedException, Is.Null);
            });
        }

        [Test]
        public void PushBufferedAcrossJoinDrainSeamIsSentTest()
        {
            var executor = new MockDelayedExecutor();
            using var socket = CreateSocket(executor);
            socket.Connect();
            var websocket =
                ((MockWebsocketFactoryWithCallbackTracking)
                    GetPrivateField<IWebsocketFactory>(
                        socket,
                        "_websocketFactory"
                    )).LastCreatedWebsocket!;
            var channel = socket.Channel("push:buffer-seam");
            var joinPush = channel.Join();
            websocket.CallSend.Clear();
            var pushBufferLock = GetPrivateField<object>(
                channel,
                "_pushBufferLock"
            );
            using var pushTimeoutScheduled =
                new ManualResetEventSlim(false);
            executor.OnExecute = _ => pushTimeoutScheduled.Set();
            Task<Push> pushTask;

            lock (pushBufferLock)
            {
                pushTask = Task.Run(() => channel.Push("seam_event"));
                Assert.That(
                    pushTimeoutScheduled.Wait(TimeSpan.FromSeconds(1)),
                    Is.True,
                    "Push did not reach the buffer insertion seam."
                );

                joinPush.Trigger(ReplyStatus.Ok);
                Assert.That(
                    channel.State,
                    Is.EqualTo(ChannelState.Joined)
                );
            }

            Assert.That(
                pushTask.Wait(TimeSpan.FromSeconds(1)),
                Is.True,
                "Push did not return after the buffer lock was released."
            );
            Assert.That(
                websocket.CallSend.Any(message =>
                    message.Contains("\"seam_event\"")
                ),
                Is.True,
                "The push was stranded after the join callback drained the buffer."
            );
        }

        private static Socket CreateSocket(
            IDelayedExecutor delayedExecutor
        )
        {
            return new Socket(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactoryWithCallbackTracking(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = delayedExecutor,
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
        }

        private static T GetPrivateField<T>(
            object instance,
            string fieldName
        )
        {
            return (T)instance
                .GetType()
                .GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!
                .GetValue(instance)!;
        }

        private static bool IsWaitingOrStopped(Thread thread)
        {
            return !thread.IsAlive
                || (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
        }
    }
}
