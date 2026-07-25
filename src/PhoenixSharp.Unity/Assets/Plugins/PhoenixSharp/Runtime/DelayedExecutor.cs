#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix
{
    public interface IDelayedExecution
    {
        void Cancel();
    }

    /**
     * IDelayedExecutor
     * This class is equivalent to javascript setTimeout/clearTimeout functions.
     */
    public interface IDelayedExecutor
    {
        IDelayedExecution Execute(Action action, TimeSpan delay);
    }

    /**
     * Scheduler
     * This class is equivalent to the Timer class in the Phoenix JS library.
     */
    public sealed class Scheduler
    {
        private readonly Action _callback;
        private readonly IDelayedExecutor _delayedExecutor;
        private readonly object _stateLock = new object();
        private readonly Func<int, TimeSpan> _timerCalc;
        private IDelayedExecution? _execution;
        private long _generation;
        private int _tries;

        public Scheduler(Action callback, Func<int, TimeSpan> timerCalc, IDelayedExecutor delayedExecutor)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _timerCalc = timerCalc ?? throw new ArgumentNullException(nameof(timerCalc));
            _delayedExecutor = delayedExecutor ?? throw new ArgumentNullException(nameof(delayedExecutor));
        }

        public void Reset()
        {
            IDelayedExecution? execution;
            lock (_stateLock)
            {
                _generation++;
                _tries = 0;
                execution = _execution;
                _execution = null;
            }

            execution?.Cancel();
        }

        public void ScheduleTimeout()
        {
            IDelayedExecution? previousExecution;
            long generation;
            int nextTry;
            lock (_stateLock)
            {
                generation = ++_generation;
                nextTry = _tries + 1;
                previousExecution = _execution;
                _execution = null;
            }

            previousExecution?.Cancel();
            var delay = _timerCalc(nextTry);
            var execution = _delayedExecutor.Execute(() => Fire(generation), delay);

            bool keepExecution;
            lock (_stateLock)
            {
                keepExecution = generation == _generation;
                if (keepExecution)
                {
                    _execution = execution;
                }
            }

            if (!keepExecution)
            {
                execution.Cancel();
            }
        }

        private void Fire(long generation)
        {
            lock (_stateLock)
            {
                if (generation != _generation)
                {
                    return;
                }

                _generation++;
                _tries += 1;
                _execution = null;
            }

            _callback();
        }
    }

    // Provide a default delayed executor that uses Tasks API.

    public sealed class TaskExecution : IDelayedExecution
    {
        private const int PendingState = 0;
        private const int RunningState = 1;
        private const int CancelledState = 2;
        private const int CompletedState = 3;

        private readonly CancellationTokenSource _cancellationSource = new CancellationTokenSource();
        private int _disposed;
        private int _state;

        internal TaskExecution()
        {
        }

        internal bool Cancelled => Volatile.Read(ref _state) == CancelledState;

        internal CancellationToken Token => _cancellationSource.Token;

        /// <summary>
        /// Cancels this execution without waiting for an action that has already started.
        /// If cancellation claims a pending execution, its action will not subsequently begin.
        /// </summary>
        public void Cancel()
        {
            if (Interlocked.CompareExchange(
                ref _state,
                CancelledState,
                PendingState
            ) != PendingState)
            {
                return;
            }

            try
            {
                _cancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposal is coordinated below, but cancellation must remain non-throwing.
            }
            catch (AggregateException)
            {
                // Only Task.Delay observes this token; guard the non-throwing contract.
            }
            finally
            {
                DisposeCancellationSource();
            }
        }

        internal bool TryBeginExecution()
        {
            return Interlocked.CompareExchange(
                ref _state,
                RunningState,
                PendingState
            ) == PendingState;
        }

        internal void Complete()
        {
            Interlocked.Exchange(ref _state, CompletedState);
            DisposeCancellationSource();
        }

        private void DisposeCancellationSource()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellationSource.Dispose();
            }
        }
    }


    public sealed class TaskDelayedExecutor : IDelayedExecutor
    {
        public IDelayedExecution Execute(Action action, TimeSpan delay)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var execution = new TaskExecution();
            Task delayTask;
            try
            {
                delayTask = Task.Delay(delay, execution.Token);
                delayTask.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
                {
                    try
                    {
                        delayTask.GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!execution.TryBeginExecution())
                    {
                        return;
                    }

                    try
                    {
                        action();
                    }
                    finally
                    {
                        execution.Complete();
                    }
                });
            }
            catch
            {
                execution.Complete();
                throw;
            }

            return execution;
        }
    }
}
