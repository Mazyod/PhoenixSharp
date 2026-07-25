using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit"), NonParallelizable]
    public sealed class SocketUnhandledErrorTests : PhoenixTestBase
    {
        [Test]
        public void ThrowingChannelSubscriptionSurfacesOnlyAsUnhandledErrorTest()
        {
            var (socket, factory) = CreateConnectedSocket();
            var callbackException = new InvalidOperationException(
                "subscription failed"
            );
            var channel = socket.Channel("room:unhandled");
            channel.On("explode", _ => throw callbackException);
            PhoenixError? unhandledError = null;
            var protocolErrorCount = 0;
            socket.OnUnhandledError += error => unhandledError = error;
            socket.OnError += _ => protocolErrorCount++;

            Assert.DoesNotThrow(() =>
                factory.LastCreatedWebsocket!.SimulateMessage(
                    BuildPhxMessage(
                        null,
                        "wire-ref",
                        "room:unhandled",
                        "explode"
                    )
                )
            );

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Dispatch)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo(
                        "Event callback threw exception for 'explode'"
                    )
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(callbackException)
                );
                Assert.That(protocolErrorCount, Is.Zero);
            });
        }

        [Test]
        public void ThrowingOnErrorSubscriberSurfacesAsUnhandledErrorTest()
        {
            var (socket, factory) = CreateConnectedSocket();
            var subscriberException = new InvalidOperationException(
                "OnError subscriber failed"
            );
            PhoenixError? unhandledError = null;
            socket.OnUnhandledError += error => unhandledError = error;
            socket.OnError += _ => throw subscriberException;

            Assert.DoesNotThrow(() =>
                factory.LastCreatedWebsocket!.SimulateError("transport failed")
            );

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Dispatch)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo("OnError callback threw exception")
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(subscriberException)
                );
            });
        }

        [Test]
        public void VoidConnectFailureSurfacesAsUnhandledTransportErrorTest()
        {
            var connectException = new InvalidOperationException(
                "transport build failed"
            );
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingBuildFactory(connectException),
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            PhoenixError? unhandledError = null;
            socket.OnUnhandledError += error => unhandledError = error;

            Assert.DoesNotThrow(socket.Connect);

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Transport)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo("WebSocket connect failed")
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(connectException)
                );
            });
        }

        [Test]
        public void RetryingConnectFailureDoesNotWarnAsSwallowedTest()
        {
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingBuildFactory(
                    new InvalidOperationException("transport build failed")
                ),
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(10),
                    RejoinAfter = null
                }
            );

            var output = CaptureConsoleError(socket.Connect);

            Assert.Multiple(() =>
            {
                Assert.That(executor.PendingCount, Is.EqualTo(1));
                Assert.That(output, Is.Empty);
            });
        }

        [Test]
        public void RetryingConnectFailureStillSurfacesToSubscriberTest()
        {
            var connectException = new InvalidOperationException(
                "transport build failed"
            );
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingBuildFactory(connectException),
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = _ => TimeSpan.FromMilliseconds(10),
                    RejoinAfter = null
                }
            );
            PhoenixError? unhandledError = null;
            socket.OnUnhandledError += error => unhandledError = error;

            socket.Connect();

            Assert.Multiple(() =>
            {
                Assert.That(executor.PendingCount, Is.EqualTo(1));
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Transport)
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(connectException)
                );
            });
        }

        [Test]
        public void DeserializationExceptionSurfacesAsUnhandledDroppedMessageTest()
        {
            var deserializeException = new FormatException("invalid wire JSON");
            var serializer = new ThrowingDeserializeSerializer(
                deserializeException
            );
            var (socket, factory) = CreateConnectedSocket(serializer);
            PhoenixError? unhandledError = null;
            var protocolErrorCount = 0;
            socket.OnUnhandledError += error => unhandledError = error;
            socket.OnError += _ => protocolErrorCount++;

            Assert.DoesNotThrow(() =>
                factory.LastCreatedWebsocket!.SimulateMessage("not-json")
            );

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Serialization)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo("Failed to deserialize message")
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(deserializeException)
                );
                Assert.That(protocolErrorCount, Is.Zero);
                Assert.That(
                    factory.LastCreatedWebsocket!.State,
                    Is.EqualTo(WebsocketState.Open)
                );
            });
        }

        [Test]
        public void NullDeserializedMessageSurfacesAsUnhandledDropTest()
        {
            var (socket, factory) = CreateConnectedSocket(
                new NullDeserializeSerializer()
            );
            PhoenixError? unhandledError = null;
            socket.OnUnhandledError += error => unhandledError = error;

            factory.LastCreatedWebsocket!.SimulateMessage("null");

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Serialization)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo("Deserialized message was null")
                );
                Assert.That(unhandledError?.Exception, Is.Null);
            });
        }

        [Test]
        public void DisposeCloseFailureSurfacesBeforeDelegatesAreClearedTest()
        {
            var closeException = new InvalidOperationException(
                "transport close failed"
            );
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingCloseFactory(closeException),
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            PhoenixError? unhandledError = null;
            socket.OnUnhandledError += error => unhandledError = error;
            socket.Connect();

            Assert.DoesNotThrow(socket.Dispose);

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Transport)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo(
                        "WebSocket close failed during disposal"
                    )
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(closeException)
                );
                Assert.That(socket.OnUnhandledError, Is.Null);
            });
        }

        [Test]
        public void TeardownCloseFailureIsContainedAndReportedTest()
        {
            var closeException = new InvalidOperationException(
                "transport close failed"
            );
            var executor = new TrackingDelayedExecutor();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new ThrowingCloseFactory(closeException),
                new Socket.Options(new JsonMessageSerializer())
                {
                    DelayedExecutor = executor,
                    HeartbeatInterval = null,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            PhoenixError? unhandledError = null;
            socket.OnUnhandledError += error => unhandledError = error;
            socket.Connect();

            Assert.DoesNotThrow(() => socket.Disconnect());

            Assert.Multiple(() =>
            {
                Assert.That(unhandledError, Is.Not.Null);
                Assert.That(
                    unhandledError?.Kind,
                    Is.EqualTo(PhoenixErrorKind.Transport)
                );
                Assert.That(
                    unhandledError?.Message,
                    Is.EqualTo("WebSocket close failed during teardown")
                );
                Assert.That(
                    unhandledError?.Exception,
                    Is.SameAs(closeException)
                );
                Assert.That(executor.PendingCount, Is.EqualTo(1));
            });

            for (var attempt = 0; attempt < 4; attempt++)
            {
                executor.ExecuteLast();
            }

            Assert.That(socket.Conn, Is.Null);
        }

        [Test]
        public void ThrowingUnhandledErrorSubscriberIsContainedWithoutRecursionTest()
        {
            var logger = new CapturingLogger();
            var (socket, factory) = CreateConnectedSocket(
                new JsonMessageSerializer(),
                logger
            );
            var channel = socket.Channel("room:unhandled");
            channel.On(
                "explode",
                _ => throw new InvalidOperationException("subscription failed")
            );
            var subscriberException = new ApplicationException(
                "unhandled subscriber failed"
            );
            var callbackCount = 0;
            socket.OnUnhandledError += _ =>
            {
                callbackCount++;
                throw subscriberException;
            };

            Assert.DoesNotThrow(() =>
                factory.LastCreatedWebsocket!.SimulateMessage(
                    BuildPhxMessage(
                        null,
                        "wire-ref",
                        "room:unhandled",
                        "explode"
                    )
                )
            );

            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.EqualTo(1));
                Assert.That(
                    logger.Entries,
                    Does.Contain(
                        (
                            "OnUnhandledError callback threw exception",
                            subscriberException
                        )
                    )
                );
            });
        }

        [Test]
        public void UnobservedSwallowedErrorsWarnOncePerSocketTest()
        {
            var output = CaptureConsoleError(() =>
            {
                var (socket, factory) = CreateConnectedSocket();
                var channel = socket.Channel("room:unhandled");
                channel.On(
                    "explode",
                    _ => throw new InvalidOperationException(
                        "subscription failed"
                    )
                );
                var rawMessage = BuildPhxMessage(
                    null,
                    "wire-ref",
                    "room:unhandled",
                    "explode"
                );

                factory.LastCreatedWebsocket!.SimulateMessage(rawMessage);
                factory.LastCreatedWebsocket.SimulateMessage(rawMessage);
            });

            Assert.Multiple(() =>
            {
                Assert.That(CountNonEmptyLines(output), Is.EqualTo(1));
                Assert.That(
                    output,
                    Does.Contain("PhoenixSharp swallowed an unhandled error")
                );
                Assert.That(output, Does.Contain("Dispatch"));
                Assert.That(output, Does.Contain("OnUnhandledError"));
                Assert.That(output, Does.Contain("Options.Logger"));
            });
        }

        [Test]
        public void PoisonWarningDoesNotConsumeNeverSilentWarningTest()
        {
            var output = CaptureConsoleError(() =>
            {
                var logger = new ThrowingLogLogger();
                var (socket, factory) = CreateConnectedSocket(
                    new JsonMessageSerializer(),
                    logger
                );
                var channel = socket.Channel("room:unhandled");
                channel.On(
                    "explode",
                    _ => throw new InvalidOperationException(
                        "subscription failed"
                    )
                );

                factory.LastCreatedWebsocket!.SimulateMessage(
                    BuildPhxMessage(
                        null,
                        "wire-ref",
                        "room:unhandled",
                        "explode"
                    )
                );
            });

            Assert.Multiple(() =>
            {
                Assert.That(CountNonEmptyLines(output), Is.EqualTo(2));
                Assert.That(
                    output,
                    Does.Contain("PhoenixSharp logger disabled")
                );
                Assert.That(
                    output,
                    Does.Contain("PhoenixSharp swallowed an unhandled error")
                );
            });
        }

        private static (
            Socket Socket,
            MockWebsocketFactoryWithCallbackTracking Factory
        ) CreateConnectedSocket()
        {
            return CreateConnectedSocket(new JsonMessageSerializer());
        }

        private static (
            Socket Socket,
            MockWebsocketFactoryWithCallbackTracking Factory
        ) CreateConnectedSocket(
            IMessageSerializer serializer,
            ILogger? logger = null
        )
        {
            var factory = new MockWebsocketFactoryWithCallbackTracking();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                factory,
                new Socket.Options(serializer)
                {
                    HeartbeatInterval = null,
                    Logger = logger,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
            socket.Connect();
            return (socket, factory);
        }

        private static string CaptureConsoleError(Action action)
        {
            var original = Console.Error;
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            Console.SetError(writer);
            try
            {
                action();
                return writer.ToString();
            }
            finally
            {
                Console.SetError(original);
            }
        }

        private static int CountNonEmptyLines(string value)
        {
            return value.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Length;
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<(string Message, Exception? Exception)> Entries { get; } =
                new List<(string Message, Exception? Exception)>();

            public bool IsEnabled(LogLevel level, string source)
            {
                return true;
            }

            public void Log(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                Entries.Add((message, exception));
            }
        }

        private sealed class ThrowingLogLogger : ILogger
        {
            public bool IsEnabled(LogLevel level, string source)
            {
                return true;
            }

            public void Log(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                throw new InvalidOperationException("sink failed");
            }
        }

        private sealed class ThrowingBuildFactory : IWebsocketFactory
        {
            private readonly Exception _exception;

            public ThrowingBuildFactory(Exception exception)
            {
                _exception = exception;
            }

            public IWebsocket Build(WebsocketConfiguration config)
            {
                throw _exception;
            }
        }

        private sealed class ThrowingCloseFactory : IWebsocketFactory
        {
            private readonly Exception _exception;

            public ThrowingCloseFactory(Exception exception)
            {
                _exception = exception;
            }

            public IWebsocket Build(WebsocketConfiguration config)
            {
                return new ThrowingCloseWebsocket(config, _exception);
            }
        }

        private sealed class ThrowingCloseWebsocket : IWebsocket
        {
            private readonly WebsocketConfiguration _config;
            private readonly Exception _exception;
            private WebsocketState _state = WebsocketState.Closed;

            public ThrowingCloseWebsocket(
                WebsocketConfiguration config,
                Exception exception
            )
            {
                _config = config;
                _exception = exception;
            }

            public WebsocketState State => _state;

            public void Connect()
            {
                _state = WebsocketState.Open;
                _config.OnOpenCallback(this);
            }

            public void Send(string message)
            {
            }

            public void Close(
                ushort? code = null,
                string? message = null
            )
            {
                throw _exception;
            }
        }

        private sealed class ThrowingDeserializeSerializer : IMessageSerializer
        {
            private readonly Exception _exception;

            public ThrowingDeserializeSerializer(Exception exception)
            {
                _exception = exception;
            }

            public string Serialize(object? element)
            {
                return string.Empty;
            }

            public T Deserialize<T>(string message)
            {
                throw _exception;
            }

            public IJsonBox Box(object? element)
            {
                return JsonBox.Serialize(element);
            }
        }

        private sealed class NullDeserializeSerializer : IMessageSerializer
        {
            public string Serialize(object? element)
            {
                return string.Empty;
            }

            public T Deserialize<T>(string message)
            {
                return default!;
            }

            public IJsonBox Box(object? element)
            {
                return JsonBox.Serialize(element);
            }
        }
    }
}
