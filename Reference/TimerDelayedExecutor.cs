using System.Collections.Generic;
using System.Timers;


namespace Phoenix {

  /**
   * TimerDelayedExecutor
   * Sample implementation of IDelayedExecutor that relies on timers.
   */
    public sealed class TimerDelayedExecutor : IDelayedExecutor {
        // Please ensure that you always start from 1, and leave 0 for uninitialized id
        private uint id = 1;
        private readonly Dictionary<uint, Timer> timers = new Dictionary<uint, Timer>();


        public IDelayedExecution Execute(Action action, TimeSpan delay) {

            var id = this.id++;
            var timer = new Timer {
                Interval = delay.TotalMilliseconds,
                AutoReset = false
            };
            timer.Elapsed += (sender, e) => {
                action();
                timers.Remove(id);
            };

            timer.Start();

            timers[id] = timer;
            return new DelayedExecution(id, this);
        }

        public void Cancel(uint id) {

            if (timers.ContainsKey(id)) {
                timers[id].Stop();
                timers.Remove(id);
            }
        }
    }
}
