using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit"), NonParallelizable]
    public sealed class DefaultLoggerTests
    {
        [Test]
        public void ConsoleLoggerDefaultsToInfoAndFiltersLowerLevelsTest()
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var timestamp = new DateTimeOffset(
                2026,
                7,
                25,
                10,
                11,
                12,
                TimeSpan.Zero
            );
            var logger = new ConsoleLogger(writer, () => timestamp);

            logger.Log(
                LogLevel.Debug,
                LogSource.Socket,
                "suppressed",
                null
            );
            logger.Log(
                LogLevel.Info,
                LogSource.Socket,
                "connected",
                null
            );

            Assert.Multiple(() =>
            {
                Assert.That(logger.MinimumLevel, Is.EqualTo(LogLevel.Info));
                Assert.That(
                    logger.IsEnabled(LogLevel.Debug, LogSource.Socket),
                    Is.False
                );
                Assert.That(
                    logger.IsEnabled(LogLevel.Info, LogSource.Socket),
                    Is.True
                );
                Assert.That(
                    writer.ToString(),
                    Is.EqualTo(
                        "2026-07-25T10:11:12.0000000+00:00 "
                        + "[Info] [socket] connected"
                        + Environment.NewLine
                    )
                );
            });
        }

        [Test]
        public void ConsoleLoggerRendersExceptionIdentityMessageAndStackTest()
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var logger = new ConsoleLogger(
                writer,
                () => new DateTimeOffset(
                    2026,
                    7,
                    25,
                    10,
                    11,
                    12,
                    TimeSpan.Zero
                )
            )
            {
                MinimumLevel = LogLevel.Trace
            };
            var exception = CaptureThrownException();

            logger.Log(
                LogLevel.Error,
                LogSource.Transport,
                "connection failed",
                exception
            );

            Assert.Multiple(() =>
            {
                Assert.That(
                    writer.ToString(),
                    Does.StartWith(
                        "2026-07-25T10:11:12.0000000+00:00 "
                        + "[Error] [transport] connection failed"
                        + Environment.NewLine
                    )
                );
                Assert.That(
                    writer.ToString(),
                    Does.Contain(
                        "System.InvalidOperationException: transport boom"
                    )
                );
                Assert.That(
                    writer.ToString(),
                    Does.Contain(nameof(ThrowLoggedException))
                );
            });
        }

        [Test]
        public void ConsoleLoggerReadsConsoleErrorAtWriteTimeTest()
        {
            var original = Console.Error;
            using var firstWriter = new StringWriter(
                CultureInfo.InvariantCulture
            );
            using var secondWriter = new StringWriter(
                CultureInfo.InvariantCulture
            );
            var logger = new ConsoleLogger();

            try
            {
                Console.SetError(firstWriter);
                logger.Log(
                    LogLevel.Info,
                    LogSource.Socket,
                    "first destination",
                    null
                );

                Console.SetError(secondWriter);
                logger.Log(
                    LogLevel.Info,
                    LogSource.Socket,
                    "second destination",
                    null
                );
            }
            finally
            {
                Console.SetError(original);
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    firstWriter.ToString(),
                    Does.Contain("first destination")
                );
                Assert.That(
                    firstWriter.ToString(),
                    Does.Not.Contain("second destination")
                );
                Assert.That(
                    secondWriter.ToString(),
                    Does.Contain("second destination")
                );
            });
        }

        [TestCase(LogLevel.Trace, "Log")]
        [TestCase(LogLevel.Debug, "Log")]
        [TestCase(LogLevel.Info, "Log")]
        [TestCase(LogLevel.Warn, "Warning")]
        [TestCase(LogLevel.Error, "Error")]
        public void UnityLoggerLevelMappingIsStableTest(
            LogLevel level,
            string expected
        )
        {
            Assert.That(
                UnityLogLevelMapping.GetOutput(level).ToString(),
                Is.EqualTo(expected)
            );
        }

        [Test]
        public void UnityLoggerIsAbsentFromNetStandardAssemblyTest()
        {
            Assert.That(
                typeof(Socket).Assembly.GetType("Phoenix.UnityLogger"),
                Is.Null
            );
        }

        [Test]
        public void UnityLoggerHasAnEngineEnabledAssemblyBoundaryTest()
        {
            var repositoryRoot = FindRepositoryRoot();
            var runtimeDirectory = Path.Combine(
                repositoryRoot,
                "src",
                "PhoenixSharp.Unity",
                "Assets",
                "Plugins",
                "PhoenixSharp",
                "Runtime"
            );
            var unityDirectory = Path.Combine(runtimeDirectory, "Unity");
            using var coreAssemblyDefinition = JsonDocument.Parse(
                File.ReadAllText(
                    Path.Combine(runtimeDirectory, "PhoenixSharp.asmdef")
                )
            );
            using var unityAssemblyDefinition = JsonDocument.Parse(
                File.ReadAllText(
                    Path.Combine(
                        unityDirectory,
                        "Phoenix.UnityLogger.asmdef"
                    )
                )
            );
            var unitySourcePath = Path.Combine(
                unityDirectory,
                "UnityLogger.cs"
            );
            var coreSourceFiles = Directory
                .EnumerateFiles(
                    runtimeDirectory,
                    "*.cs",
                    SearchOption.AllDirectories
                )
                .Where(path =>
                    !path.StartsWith(
                        unityDirectory + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                )
                .ToArray();

            Assert.Multiple(() =>
            {
                // The Unity asmdef references the core assembly BY NAME; renaming
                // the core assembly would dangle that reference undetected by CI.
                Assert.That(
                    coreAssemblyDefinition.RootElement
                        .GetProperty("name")
                        .GetString(),
                    Is.EqualTo("Phoenix")
                );
                Assert.That(
                    coreAssemblyDefinition.RootElement
                        .GetProperty("noEngineReferences")
                        .GetBoolean(),
                    Is.True
                );
                Assert.That(
                    unityAssemblyDefinition.RootElement
                        .GetProperty("references")
                        .EnumerateArray()
                        .Select(reference => reference.GetString()),
                    Does.Contain("Phoenix")
                );
                Assert.That(
                    unityAssemblyDefinition.RootElement
                        .GetProperty("noEngineReferences")
                        .GetBoolean(),
                    Is.False
                );
                Assert.That(
                    File.Exists(
                        Path.Combine(runtimeDirectory, "UnityLogger.cs")
                    ),
                    Is.False
                );
                Assert.That(
                    File.Exists(unitySourcePath),
                    Is.True
                );
                Assert.That(
                    File.ReadAllText(unitySourcePath),
                    Does.Contain("UnityEngine")
                );
                Assert.That(
                    coreSourceFiles.Any(path =>
                        File.ReadAllText(path).Contains(
                            "UnityEngine",
                            StringComparison.Ordinal
                        )
                    ),
                    Is.False
                );
            });
        }

        [Test]
        public void ThrowingIsEnabledIsContainedAndPoisonsSinkTest()
        {
            var logger = new ThrowingIsEnabledLogger();
            var output = CaptureConsoleError(() =>
            {
                var socket = CreateSocket(logger);

                Assert.DoesNotThrow(() =>
                {
                    socket.Push(new Message("room", "first"));
                    socket.Push(new Message("room", "second"));
                });
            });

            Assert.Multiple(() =>
            {
                Assert.That(logger.IsEnabledCallCount, Is.EqualTo(1));
                Assert.That(
                    CountNonEmptyLines(output),
                    Is.EqualTo(1)
                );
                Assert.That(output, Does.Contain("PhoenixSharp logger disabled"));
                Assert.That(
                    output,
                    Does.Contain(nameof(ThrowingIsEnabledLogger))
                );
                Assert.That(output, Does.Contain("IsEnabled"));
                Assert.That(output, Does.Contain("gate failed"));
            });
        }

        [Test]
        public void ThrowingLogIsContainedAndPoisonsSinkTest()
        {
            var logger = new ThrowingLogLogger();
            var output = CaptureConsoleError(() =>
            {
                var socket = CreateSocket(logger);

                Assert.DoesNotThrow(() =>
                {
                    socket.Push(new Message("room", "first"));
                    socket.Push(new Message("room", "second"));
                });
            });

            Assert.Multiple(() =>
            {
                Assert.That(logger.IsEnabledCallCount, Is.EqualTo(1));
                Assert.That(logger.LogCallCount, Is.EqualTo(1));
                Assert.That(
                    CountNonEmptyLines(output),
                    Is.EqualTo(1)
                );
                Assert.That(output, Does.Contain("PhoenixSharp logger disabled"));
                Assert.That(
                    output,
                    Does.Contain(nameof(ThrowingLogLogger))
                );
                Assert.That(output, Does.Contain("Log"));
                Assert.That(output, Does.Contain("write failed"));
            });
        }

        private static Socket CreateSocket(ILogger logger)
        {
            return new Socket(
                "ws://localhost:1234",
                null,
                new MockWebsocketFactoryWithCallbackTracking(),
                new Socket.Options(new JsonMessageSerializer())
                {
                    HeartbeatInterval = null,
                    Logger = logger,
                    ReconnectAfter = null,
                    RejoinAfter = null
                }
            );
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

        private static Exception CaptureThrownException()
        {
            try
            {
                ThrowLoggedException();
                throw new InvalidOperationException("unreachable");
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowLoggedException()
        {
            throw new InvalidOperationException("transport boom");
        }

        private sealed class ThrowingIsEnabledLogger : ILogger
        {
            public int IsEnabledCallCount { get; private set; }

            public bool IsEnabled(LogLevel level, string source)
            {
                IsEnabledCallCount++;
                throw new InvalidOperationException("gate failed");
            }

            public void Log(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                Assert.Fail("A failed gate must not reach Log");
            }
        }

        private sealed class ThrowingLogLogger : ILogger
        {
            public int IsEnabledCallCount { get; private set; }
            public int LogCallCount { get; private set; }

            public bool IsEnabled(LogLevel level, string source)
            {
                IsEnabledCallCount++;
                return true;
            }

            public void Log(
                LogLevel level,
                string source,
                string message,
                Exception? exception
            )
            {
                LogCallCount++;
                throw new InvalidOperationException("write failed");
            }
        }
    }
}
