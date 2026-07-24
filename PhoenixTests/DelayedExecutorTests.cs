using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NUnit.Framework;
using Phoenix;
using PhoenixTests.TestDoubles;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
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
        public void DelayedExecutorRunsActionOnThreadPoolThreadTest()
        {
            using var actionCompleted = new ManualResetEventSlim();
            var ranOnThreadPoolThread = false;
            var executor = new TaskDelayedExecutor();

            executor.Execute(() =>
            {
                ranOnThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                actionCompleted.Set();
            }, TimeSpan.Zero);

            Assert.That(actionCompleted.Wait(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(ranOnThreadPoolThread, Is.True);
        }

        [Test]
        public void DelayedExecutorCancelBeforeDelayNeverInvokesActionTest()
        {
            using var actionInvoked = new ManualResetEventSlim();
            var executor = new TaskDelayedExecutor();
            // A long delay guarantees Cancel() runs before the delay elapses,
            // even on a starved CI runner; the negative wait below bounds runtime.
            var execution = executor.Execute(
                actionInvoked.Set,
                TimeSpan.FromSeconds(30)
            );

            execution.Cancel();

            Assert.That(actionInvoked.Wait(TimeSpan.FromMilliseconds(100)), Is.False);
        }

        [Test]
        public void DelayedExecutorResetTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            var execution = executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

            Assert.IsFalse(works);
            execution.Cancel();
            Assert.That(() => works, Is.False.After(10, 1));
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

            Assert.That(() => works, Is.False.After(150, 5));
        }

        [Test]
        public void DelayedExecutorCancelFromWithinActionIsSafeTest()
        {
            using var actionCompleted = new ManualResetEventSlim();
            IDelayedExecution? execution = null;
            Exception? cancelException = null;
            var executor = new TaskDelayedExecutor();

            execution = executor.Execute(() =>
            {
                try
                {
                    execution!.Cancel();
                }
                catch (Exception ex)
                {
                    cancelException = ex;
                }
                finally
                {
                    actionCompleted.Set();
                }
            }, TimeSpan.FromMilliseconds(10));

            Assert.That(actionCompleted.Wait(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(cancelException, Is.Null);
        }

        [Test]
        public void DelayedExecutorRapidCancelChurnReleasesExecutionsWithoutFiringTest()
        {
            const int executionCount = 1_000;
            var callbackCount = 0;
            var references = new List<WeakReference>(executionCount);
            var executor = new TaskDelayedExecutor();

            for (var index = 0; index < executionCount; index++)
            {
                references.Add(ScheduleAndCancel(
                    executor,
                    () => Interlocked.Increment(ref callbackCount)
                ));
            }

            Assert.That(
                () =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    return CountAlive(references);
                },
                Is.EqualTo(0).After(2_000, 20)
            );
            Assert.That(Volatile.Read(ref callbackCount), Is.EqualTo(0));
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

            // Schedule multiple timeouts with waits for each to complete
            scheduler.ScheduleTimeout(); // tries = 1
            Assert.That(() => callCount, Is.EqualTo(1).After(20, 1));

            scheduler.ScheduleTimeout(); // tries = 2
            Assert.That(() => callCount, Is.EqualTo(2).After(20, 1));

            scheduler.ScheduleTimeout(); // tries = 3
            Assert.That(() => callCount, Is.EqualTo(3).After(20, 1));

            Assert.AreEqual(new List<int> { 1, 2, 3 }, receivedTries);
        }

        [Test]
        public void SchedulerResetClearsTriesTest()
        {
            var receivedTries = new List<int>();
            var callCount = 0;
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

            // Schedule and wait for callback to execute
            scheduler.ScheduleTimeout(); // tries = 1
            Assert.That(() => callCount, Is.EqualTo(1).After(20, 1));

            scheduler.ScheduleTimeout(); // tries = 2
            Assert.That(() => callCount, Is.EqualTo(2).After(20, 1));

            scheduler.Reset();

            scheduler.ScheduleTimeout(); // tries = 1 again (reset)
            Assert.That(() => callCount, Is.EqualTo(3).After(20, 1));

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

            Assert.That(() => callbackExecuted, Is.False.After(100, 5));
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

            Assert.That(() => callCount, Is.EqualTo(1).After(100, 5));
        }

        [Test]
        public void SchedulerResetMakesCapturedExecutionStaleTest()
        {
            var callCount = 0;
            var receivedTries = new List<int>();
            var executor = new TrackingDelayedExecutor();
            var scheduler = new Scheduler(
                () => callCount++,
                tries =>
                {
                    receivedTries.Add(tries);
                    return TimeSpan.FromMilliseconds(tries);
                },
                executor
            );

            scheduler.ScheduleTimeout();
            var staleAction = executor.Executions[0].Action!;
            scheduler.Reset();

            staleAction();
            scheduler.ScheduleTimeout();

            Assert.That(callCount, Is.EqualTo(0));
            Assert.That(receivedTries, Is.EqualTo(new[] { 1, 1 }));
        }

        [Test]
        public void SchedulerSupersededExecutionIsNoOpTest()
        {
            var callCount = 0;
            var executor = new TrackingDelayedExecutor();
            var scheduler = new Scheduler(
                () => callCount++,
                _ => TimeSpan.Zero,
                executor
            );

            scheduler.ScheduleTimeout();
            var firstAction = executor.Executions[0].Action!;
            scheduler.ScheduleTimeout();
            var secondAction = executor.Executions[1].Action!;

            firstAction();
            Assert.That(callCount, Is.EqualTo(0));

            secondAction();
            Assert.That(callCount, Is.EqualTo(1));
        }

        [Test]
        public void SchedulerGenuineFireAdvancesNextBackoffAttemptTest()
        {
            var receivedTries = new List<int>();
            var executor = new TrackingDelayedExecutor();
            var scheduler = new Scheduler(
                () => { },
                tries =>
                {
                    receivedTries.Add(tries);
                    return TimeSpan.FromMilliseconds(tries);
                },
                executor
            );

            scheduler.ScheduleTimeout();
            executor.Executions[0].Action!();
            scheduler.ScheduleTimeout();
            executor.Executions[1].Action!();
            scheduler.ScheduleTimeout();

            Assert.That(receivedTries, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void SchedulerCancelsExecutionCreatedAfterReentrantResetTest()
        {
            var callbackCount = 0;
            var executor = new ReentrantDelayedExecutor();
            Scheduler? scheduler = null;
            scheduler = new Scheduler(
                () => callbackCount++,
                _ => TimeSpan.Zero,
                executor
            );
            executor.OnExecute = scheduler.Reset;

            scheduler.ScheduleTimeout();

            Assert.That(executor.LastExecution, Is.Not.Null);
            Assert.That(executor.LastExecution!.IsCancelled, Is.True);
            executor.LastExecution.Action!();
            Assert.That(callbackCount, Is.EqualTo(0));
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ScheduleAndCancel(
            TaskDelayedExecutor executor,
            Action callback
        )
        {
            var execution = executor.Execute(callback, TimeSpan.FromSeconds(30));
            var reference = new WeakReference(execution);
            execution.Cancel();
            return reference;
        }

        private static int CountAlive(List<WeakReference> references)
        {
            var count = 0;
            foreach (var reference in references)
            {
                if (reference.IsAlive)
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class ReentrantDelayedExecutor : IDelayedExecutor
        {
            public TrackedDelayedExecution? LastExecution { get; private set; }
            public Action? OnExecute { get; set; }

            public IDelayedExecution Execute(Action action, TimeSpan delay)
            {
                LastExecution = new TrackedDelayedExecution(action, delay);
                OnExecute?.Invoke();
                return LastExecution;
            }
        }
    }
}
