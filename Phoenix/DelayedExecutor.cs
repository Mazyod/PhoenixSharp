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
        private readonly Func<int, TimeSpan> _timerCalc;
        private IDelayedExecution _execution;
        private int _tries;

        public Scheduler(Action callback, Func<int, TimeSpan> timerCalc, IDelayedExecutor delayedExecutor)
        {
            _callback = callback;
            _timerCalc = timerCalc;
            _delayedExecutor = delayedExecutor;
        }

        public void Reset()
        {
            _tries = 0;
            _execution?.Cancel();
            _execution = null;
        }

        public void ScheduleTimeout()
        {
            _execution?.Cancel();
            _execution = _delayedExecutor.Execute(() =>
            {
                _tries += 1;
                _callback();
            }, _timerCalc(_tries + 1));
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