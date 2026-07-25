using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public sealed class AsyncApiFootgunTests : PhoenixTestBase
    {
        [Test]
        public void WaitForInitialSyncAsyncCompletesImmediatelyAfterInitialSyncTest()
        {
            var (presence, channel) = CreatePresence();
            TriggerInitialSync(channel);

            var waitTask = presence.WaitForInitialSyncAsync();

            Assert.That(waitTask.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public void WaitForInitialSyncAsyncSubscribesUnderStateLockTest()
        {
            var (presence, _) = CreatePresence();
            var stateLock = GetPresenceStateLock(presence);
            using var cancellationSource = new CancellationTokenSource();
            using var invocationStarted = new ManualResetEventSlim(false);
            using var callReturned = new ManualResetEventSlim(false);
            Task? waitTask = null;

            Monitor.Enter(stateLock);
            try
            {
                var invocationTask = Task.Run(() =>
                {
                    invocationStarted.Set();
                    waitTask = presence.WaitForInitialSyncAsync(
                        cancellationSource.Token
                    );
                    callReturned.Set();
                });

                Assert.That(
                    invocationStarted.Wait(TimeSpan.FromSeconds(2)),
                    Is.True
                );
                Assert.That(
                    callReturned.Wait(TimeSpan.FromMilliseconds(250)),
                    Is.False,
                    "WaitForInitialSyncAsync mutated OnSync without taking "
                    + "the presence state lock."
                );

                Monitor.Exit(stateLock);
                Assert.That(
                    callReturned.Wait(TimeSpan.FromSeconds(2)),
                    Is.True
                );
                invocationTask.GetAwaiter().GetResult();
            }
            finally
            {
                if (Monitor.IsEntered(stateLock))
                {
                    Monitor.Exit(stateLock);
                }
            }

            cancellationSource.Cancel();
            Assert.That(waitTask, Is.Not.Null);
            Assert.ThrowsAsync<TaskCanceledException>(
                async () => await waitTask!
            );
        }

        [Test]
        public async Task WaitForInitialSyncAsyncDisposesRegistrationAfterSyncTest()
        {
            var (presence, channel) = CreatePresence();
            using var cancellationSource = new CancellationTokenSource();

            var waitTask = presence.WaitForInitialSyncAsync(
                cancellationSource.Token
            );

            Assert.That(
                AsyncCancellationRegistrationTests.CountActiveRegistrations(
                    cancellationSource
                ),
                Is.EqualTo(1)
            );
            TriggerInitialSync(channel);
            await waitTask;

            Assert.That(
                AsyncCancellationRegistrationTests.CountActiveRegistrations(
                    cancellationSource
                ),
                Is.Zero
            );
        }

        [Test]
        public void WaitForEventAsyncRegistersBeforePublishingSubscriptionTest()
        {
            var channelSource = File.ReadAllText(
                Path.Combine(
                    FindRepositoryRoot(),
                    "src",
                    "PhoenixSharp.Unity",
                    "Assets",
                    "Plugins",
                    "PhoenixSharp",
                    "Runtime",
                    "Channel.cs"
                )
            );
            var methodStart = channelSource.IndexOf(
                "public Task<Message> WaitForEventAsync(",
                StringComparison.Ordinal
            );
            var methodEnd = channelSource.IndexOf(
                "// overrideable message hook",
                methodStart,
                StringComparison.Ordinal
            );
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));
            var methodSource = channelSource.Substring(
                methodStart,
                methodEnd - methodStart
            );
            var registrationIndex = methodSource.IndexOf(
                "cancellationRegistration = cancellationToken.Register(",
                StringComparison.Ordinal
            );
            var subscriptionIndex = methodSource.IndexOf(
                "subscription = On(",
                StringComparison.Ordinal
            );

            Assert.Multiple(() =>
            {
                Assert.That(registrationIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(subscriptionIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    registrationIndex,
                    Is.LessThan(subscriptionIndex),
                    "The subscription became observable before its "
                    + "cancellation registration was assigned."
                );
            });
        }

        [Test]
        public async Task WaitForEventAsyncDisposesRegistrationAfterEventTest()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("room:event-wait");
            using var cancellationSource = new CancellationTokenSource();

            var waitTask = channel.WaitForEventAsync(
                "event",
                cancellationToken: cancellationSource.Token
            );

            Assert.That(
                AsyncCancellationRegistrationTests.CountActiveRegistrations(
                    cancellationSource
                ),
                Is.EqualTo(1)
            );
            channel.Trigger(new Message(@event: "event"));
            await waitTask;

            Assert.That(
                AsyncCancellationRegistrationTests.CountActiveRegistrations(
                    cancellationSource
                ),
                Is.Zero
            );
        }

        [Test]
        public void GenericOnNullPayloadSkipsCallbackAndSurfacesWarningTest()
        {
            var logger = new WarnCapturingLogger();
            var socket = CreateSocket(logger);
            var channel = socket.Channel("room:typed");
            var callbackCount = 0;
            var unhandledErrors = new List<PhoenixError>();
            socket.OnUnhandledError += unhandledErrors.Add;
            channel.On<int>("typed_null", _ => callbackCount++);

            Assert.DoesNotThrow(() =>
                channel.Trigger(new Message(@event: "typed_null"))
            );

            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.Zero);
                Assert.That(unhandledErrors, Has.Count.EqualTo(1));
                Assert.That(
                    unhandledErrors[0].Kind,
                    Is.EqualTo(PhoenixErrorKind.Dispatch)
                );
                Assert.That(
                    unhandledErrors[0].Message,
                    Is.EqualTo(
                        "Typed event callback for 'typed_null' skipped "
                        + "because its payload is null"
                    )
                );
                Assert.That(unhandledErrors[0].Exception, Is.Null);
                Assert.That(logger.Entries, Has.Count.EqualTo(1));
                Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warn));
                Assert.That(
                    logger.Entries[0].Source,
                    Is.EqualTo(LogSource.Channel)
                );
                Assert.That(
                    logger.Entries[0].Message,
                    Is.EqualTo(unhandledErrors[0].Message)
                );
                Assert.That(logger.Entries[0].Exception, Is.Null);
            });
        }

        [Test]
        public void GenericOnUnboxFailureSkipsCallbackAndSurfacesWarningTest()
        {
            var logger = new WarnCapturingLogger();
            var socket = CreateSocket(logger);
            var channel = socket.Channel("room:typed");
            var callbackCount = 0;
            var unboxException = new InvalidOperationException(
                "cannot unbox"
            );
            var unhandledErrors = new List<PhoenixError>();
            socket.OnUnhandledError += unhandledErrors.Add;
            channel.On<int>("typed_invalid", _ => callbackCount++);

            Assert.DoesNotThrow(() =>
                channel.Trigger(
                    new Message(
                        @event: "typed_invalid",
                        payload: new ThrowingJsonBox(unboxException)
                    )
                )
            );

            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.Zero);
                Assert.That(unhandledErrors, Has.Count.EqualTo(1));
                Assert.That(
                    unhandledErrors[0].Kind,
                    Is.EqualTo(PhoenixErrorKind.Dispatch)
                );
                Assert.That(
                    unhandledErrors[0].Message,
                    Is.EqualTo(
                        "Typed event callback for 'typed_invalid' could not "
                        + "unbox its payload as 'System.Int32'"
                    )
                );
                Assert.That(
                    unhandledErrors[0].Exception,
                    Is.SameAs(unboxException)
                );
                Assert.That(logger.Entries, Has.Count.EqualTo(1));
                Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warn));
                Assert.That(
                    logger.Entries[0].Source,
                    Is.EqualTo(LogSource.Channel)
                );
                Assert.That(
                    logger.Entries[0].Message,
                    Is.EqualTo(unhandledErrors[0].Message)
                );
                Assert.That(
                    logger.Entries[0].Exception,
                    Is.SameAs(unboxException)
                );
            });
        }

        private static (Presence Presence, Channel Channel) CreatePresence()
        {
            var socket = CreateConnectedSocket();
            var channel = socket.Channel("room:presence");
            return (new Presence(channel), channel);
        }

        private static Socket CreateSocket(ILogger logger)
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    Logger = logger,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            return socket;
        }

        private static void TriggerInitialSync(Channel channel)
        {
            channel.Trigger(
                new Message(
                    @event: "presence_state",
                    payload: JsonBox.Serialize(
                        new Dictionary<string, PresencePayload>()
                    )
                )
            );
        }

        private static object GetPresenceStateLock(Presence presence)
        {
            var stateLockField = typeof(Presence).GetField(
                "_stateLock",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(stateLockField, Is.Not.Null);
            return stateLockField!.GetValue(presence)!;
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(
                    TestContext.CurrentContext.TestDirectory
                );
                directory != null;
                directory = directory.Parent)
            {
                if (File.Exists(
                        Path.Combine(directory.FullName, "Phoenix.sln")
                    ))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException(
                "Could not locate the PhoenixSharp repository root."
            );
        }

        private sealed class ThrowingJsonBox : IJsonBox
        {
            private readonly Exception _exception;

            public ThrowingJsonBox(Exception exception)
            {
                _exception = exception;
            }

            public T Unbox<T>()
            {
                throw _exception;
            }
        }

        private sealed class WarnCapturingLogger : ILogger
        {
            public List<LogEntry> Entries { get; } = new List<LogEntry>();

            public bool IsEnabled(LogLevel level, string source)
            {
                return level >= LogLevel.Warn;
            }

            public void Log(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                Entries.Add(new LogEntry(level, source, message, exception));
            }
        }

        private sealed class LogEntry
        {
            public LogLevel Level { get; }
            public string Source { get; }
            public string Message { get; }
            public Exception? Exception { get; }

            public LogEntry(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                Level = level;
                Source = source;
                Message = message;
                Exception = exception;
            }
        }
    }
}
