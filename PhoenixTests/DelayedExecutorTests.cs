using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Phoenix;

namespace PhoenixTests
{
    [TestFixture]
    public class DelayedExecutorTests
    {
        #region TaskDelayedExecutor Tests

        [Test]
        public void DelayedExecutorInvocationTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

            Assert.IsFalse(works);
            Assert.That(() => works, Is.True.After(100, 1));
        }

        [Test]
        public void DelayedExecutorResetTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            var execution = executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

            Assert.IsFalse(works);
            execution.Cancel();
            Thread.Sleep(10);
            Assert.IsFalse(works);
        }

        [Test]
        public void DelayedExecutorZeroDelayTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            executor.Execute(() => works = true, TimeSpan.Zero);

            Assert.That(() => works, Is.True.After(50, 1));
        }

        [Test]
        public void DelayedExecutorMultipleConcurrentExecutionsTest()
        {
            var executor = new TaskDelayedExecutor();
            var results = new List<int>();
            var lockObj = new object();

            executor.Execute(() => { lock (lockObj) results.Add(1); }, TimeSpan.FromMilliseconds(30));
            executor.Execute(() => { lock (lockObj) results.Add(2); }, TimeSpan.FromMilliseconds(10));
            executor.Execute(() => { lock (lockObj) results.Add(3); }, TimeSpan.FromMilliseconds(20));

            Assert.That(() =>
            {
                lock (lockObj) return results.Count;
            }, Is.EqualTo(3).After(100, 5));

            // Verify order based on delay (shorter delay executes first)
            lock (lockObj)
            {
                Assert.AreEqual(2, results[0]); // 10ms
                Assert.AreEqual(3, results[1]); // 20ms
                Assert.AreEqual(1, results[2]); // 30ms
            }
        }

        [Test]
        public void DelayedExecutorCancelAfterExecutionStartedHasNoEffectTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            var execution = executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

            // Wait for execution to complete
            Assert.That(() => works, Is.True.After(50, 1));

            // Cancel after execution - should have no effect
            execution.Cancel();
            Assert.IsTrue(works);
        }

        [Test]
        public void DelayedExecutorMultipleCancellationsAreSafeTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            var execution = executor.Execute(() => works = true, TimeSpan.FromMilliseconds(100));

            // Multiple cancellations should not throw
            Assert.DoesNotThrow(() =>
            {
                execution.Cancel();
                execution.Cancel();
                execution.Cancel();
            });

            Thread.Sleep(150);
            Assert.IsFalse(works);
        }

        [Test]
        public void DelayedExecutorThrowsOnNullActionTest()
        {
            var executor = new TaskDelayedExecutor();
            Assert.Throws<ArgumentNullException>(() => executor.Execute(null!, TimeSpan.FromMilliseconds(1)));
        }

        #endregion

        #region TaskExecution Tests

        [Test]
        public void TaskExecutionCancelledPropertyStartsFalseTest()
        {
            var execution = new TaskExecution();
            Assert.IsFalse(execution.Cancelled);
        }

        [Test]
        public void TaskExecutionCancelledPropertySetToTrueAfterCancelTest()
        {
            var execution = new TaskExecution();
            execution.Cancel();
            Assert.IsTrue(execution.Cancelled);
        }

        #endregion

        #region Scheduler Tests

        [Test]
        public void SchedulerExecutesCallbackAfterScheduleTimeoutTest()
        {
            var callbackExecuted = false;
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => callbackExecuted = true,
                _ => TimeSpan.FromMilliseconds(1),
                executor
            );

            scheduler.ScheduleTimeout();

            Assert.That(() => callbackExecuted, Is.True.After(50, 1));
        }

        [Test]
        public void SchedulerProgressiveBackoffTest()
        {
            var callCount = 0;
            var receivedTries = new List<int>();
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => callCount++,
                tries =>
                {
                    receivedTries.Add(tries);
                    return TimeSpan.FromMilliseconds(1);
                },
                executor
            );

            // Schedule multiple timeouts
            scheduler.ScheduleTimeout(); // tries = 1
            Thread.Sleep(20);

            scheduler.ScheduleTimeout(); // tries = 2
            Thread.Sleep(20);

            scheduler.ScheduleTimeout(); // tries = 3
            Thread.Sleep(20);

            Assert.AreEqual(3, callCount);
            Assert.AreEqual(new List<int> { 1, 2, 3 }, receivedTries);
        }

        [Test]
        public void SchedulerResetClearsTriesTest()
        {
            var receivedTries = new List<int>();
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => { },
                tries =>
                {
                    receivedTries.Add(tries);
                    return TimeSpan.FromMilliseconds(1);
                },
                executor
            );

            scheduler.ScheduleTimeout(); // tries = 1
            Thread.Sleep(20);
            scheduler.ScheduleTimeout(); // tries = 2
            Thread.Sleep(20);

            scheduler.Reset();

            scheduler.ScheduleTimeout(); // tries = 1 again (reset)
            Thread.Sleep(20);

            Assert.AreEqual(new List<int> { 1, 2, 1 }, receivedTries);
        }

        [Test]
        public void SchedulerResetCancelsPendingExecutionTest()
        {
            var callbackExecuted = false;
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => callbackExecuted = true,
                _ => TimeSpan.FromMilliseconds(50),
                executor
            );

            scheduler.ScheduleTimeout();
            scheduler.Reset();

            Thread.Sleep(100);
            Assert.IsFalse(callbackExecuted);
        }

        [Test]
        public void SchedulerScheduleTimeoutCancelsPreviousPendingExecutionTest()
        {
            var callCount = 0;
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => callCount++,
                _ => TimeSpan.FromMilliseconds(50),
                executor
            );

            // Schedule multiple times rapidly - only the last should execute
            scheduler.ScheduleTimeout();
            scheduler.ScheduleTimeout();
            scheduler.ScheduleTimeout();

            Thread.Sleep(100);
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void SchedulerExponentialBackoffPatternTest()
        {
            var receivedDelays = new List<TimeSpan>();
            var callCount = 0;
            var mockExecutor = new MockDelayedExecutor();
            mockExecutor.OnExecute = delay => receivedDelays.Add(delay);

            var scheduler = new Scheduler(
                () => callCount++,
                tries => TimeSpan.FromMilliseconds(100 * tries), // Linear backoff for simplicity
                mockExecutor
            );

            scheduler.ScheduleTimeout(); // delay = 100ms (tries=1)
            mockExecutor.ExecutePending();

            scheduler.ScheduleTimeout(); // delay = 200ms (tries=2)
            mockExecutor.ExecutePending();

            scheduler.ScheduleTimeout(); // delay = 300ms (tries=3)
            mockExecutor.ExecutePending();

            Assert.AreEqual(TimeSpan.FromMilliseconds(100), receivedDelays[0]);
            Assert.AreEqual(TimeSpan.FromMilliseconds(200), receivedDelays[1]);
            Assert.AreEqual(TimeSpan.FromMilliseconds(300), receivedDelays[2]);
        }

        [Test]
        public void SchedulerConstructorThrowsOnNullCallbackTest()
        {
            var executor = new TaskDelayedExecutor();
            Assert.Throws<ArgumentNullException>(() =>
                new Scheduler(null!, _ => TimeSpan.FromMilliseconds(1), executor));
        }

        [Test]
        public void SchedulerConstructorThrowsOnNullTimerCalcTest()
        {
            var executor = new TaskDelayedExecutor();
            Assert.Throws<ArgumentNullException>(() =>
                new Scheduler(() => { }, null!, executor));
        }

        [Test]
        public void SchedulerConstructorThrowsOnNullDelayedExecutorTest()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Scheduler(() => { }, _ => TimeSpan.FromMilliseconds(1), null!));
        }

        [Test]
        public void SchedulerResetCanBeCalledMultipleTimesTest()
        {
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => { },
                _ => TimeSpan.FromMilliseconds(1),
                executor
            );

            // Should not throw
            Assert.DoesNotThrow(() =>
            {
                scheduler.Reset();
                scheduler.Reset();
                scheduler.Reset();
            });
        }

        [Test]
        public void SchedulerResetWithNoPendingExecutionTest()
        {
            var executor = new TaskDelayedExecutor();
            var scheduler = new Scheduler(
                () => { },
                _ => TimeSpan.FromMilliseconds(1),
                executor
            );

            // Reset without any pending execution should not throw
            Assert.DoesNotThrow(() => scheduler.Reset());
        }

        #endregion
    }

    #region Mock Delayed Executor for Testing

    /// <summary>
    /// Mock delayed executor that allows synchronous execution for testing
    /// </summary>
    public sealed class MockDelayedExecution : IDelayedExecution
    {
        public bool Cancelled { get; private set; }
        public Action? Action { get; set; }

        public void Cancel()
        {
            Cancelled = true;
        }

        public void Execute()
        {
            if (!Cancelled)
            {
                Action?.Invoke();
            }
        }
    }

    public sealed class MockDelayedExecutor : IDelayedExecutor
    {
        public Action<TimeSpan>? OnExecute { get; set; }
        private MockDelayedExecution? _pendingExecution;

        public IDelayedExecution Execute(Action action, TimeSpan delay)
        {
            OnExecute?.Invoke(delay);
            _pendingExecution = new MockDelayedExecution { Action = action };
            return _pendingExecution;
        }

        public void ExecutePending()
        {
            _pendingExecution?.Execute();
        }
    }

    #endregion
}
