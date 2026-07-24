#nullable enable
using System;
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
        internal bool Cancelled;

        public void Cancel()
        {
            Cancelled = true;
        }
    }


    public sealed class TaskDelayedExecutor : IDelayedExecutor
    {
        public IDelayedExecution Execute(Action action, TimeSpan delay)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var execution = new TaskExecution();
            Task.Delay(delay).GetAwaiter().OnCompleted(() =>
            {
                if (!execution.Cancelled)
                {
                    action();
                }
            });

            return execution;
        }
    }
}
