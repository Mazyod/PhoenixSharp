using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;
using PhoenixTests.WebSocketImpl;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class LoggingTests
    {
        [Test]
        public void MinimumLevelFilteringSkipsFormattingAndSinkInvocationTest()
        {
            var logger = new MinimumLevelLogger(LogLevel.Error);
            var payload = new PoisonJsonBox();
            var socket = new Socket(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactory(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    Logger = logger
                }
            );
            var message = new Message(
                topic: "room:filtered",
                @event: "expensive",
                payload: payload
            );

            Assert.DoesNotThrow(() => socket.Push(message));
            Assert.Multiple(() =>
            {
                Assert.That(payload.ToStringCallCount, Is.Zero);
                Assert.That(logger.Entries, Is.Empty);
                Assert.That(
                    logger.IsEnabledCalls,
                    Does.Contain((LogLevel.Debug, LogSource.Push))
                );
            });
        }

        [Test]
        public void JsonBoxToStringRendersCompactUnderlyingJsonTest()
        {
            var box = new JsonBox(
                JObject.Parse(
                    @"{""event"":""new_msg"",""payload"":{""body"":""hello""}}"
                )
            );

            Assert.That(
                box.ToString(),
                Is.EqualTo(
                    @"{""event"":""new_msg"",""payload"":{""body"":""hello""}}"
                )
            );
        }

        [Test]
        public void JsonBoxToStringTruncatesHugeJsonToDocumentedLimitTest()
        {
            var box = JsonBox.Serialize(
                new
                {
                    payload = new string(
                        'x',
                        JsonBox.MaximumToStringLength * 2
                    )
                }
            );

            var rendered = box.ToString();

            Assert.Multiple(() =>
            {
                Assert.That(
                    rendered.Length,
                    Is.EqualTo(JsonBox.MaximumToStringLength)
                );
                Assert.That(rendered, Does.StartWith(@"{""payload"":"""));
                Assert.That(rendered, Does.EndWith("...[truncated]"));
            });
        }

        [Test]
        public void LogSourceVocabularyIsStableTest()
        {
            Assert.Multiple(() =>
            {
                Assert.That(LogSource.Transport, Is.EqualTo("transport"));
                Assert.That(LogSource.Channel, Is.EqualTo("channel"));
                Assert.That(LogSource.Push, Is.EqualTo("push"));
                Assert.That(LogSource.Socket, Is.EqualTo("socket"));
                Assert.That(LogSource.Receive, Is.EqualTo("receive"));
            });
        }

        private sealed class MinimumLevelLogger : ILogger
        {
            private readonly LogLevel _minimumLevel;

            public List<(LogLevel Level, string Source)> IsEnabledCalls { get; } =
                new List<(LogLevel Level, string Source)>();
            public List<string> Entries { get; } = new List<string>();

            public MinimumLevelLogger(LogLevel minimumLevel)
            {
                _minimumLevel = minimumLevel;
            }

            public bool IsEnabled(LogLevel level, string source)
            {
                IsEnabledCalls.Add((level, source));
                return level >= _minimumLevel;
            }

            public void Log(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                Entries.Add(message);
            }
        }

        private sealed class PoisonJsonBox : IJsonBox
        {
            public int ToStringCallCount { get; private set; }

            public T Unbox<T>()
            {
                return default!;
            }

            public override string ToString()
            {
                ToStringCallCount++;
                throw new InvalidOperationException(
                    "Suppressed payload formatting was evaluated"
                );
            }
        }
    }
}
