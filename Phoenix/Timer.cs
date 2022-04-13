using System;
using System.Collections.Generic;
using System.Timers;


namespace Phoenix {
	public struct DelayedExecution {

		private readonly uint id;
		private readonly IDelayedExecutor executor;

		public DelayedExecution(uint id, IDelayedExecutor executor) {
			this.id = id;
			this.executor = executor;
		}

		public void Cancel() {
			executor.Cancel(id);
		}
	}

	/**
	* IDelayedExecutor
	* This class is equivalent to javascript setTimeout/clearTimeout functions.
	*/
	public interface IDelayedExecutor {

		DelayedExecution Execute(Action action, TimeSpan delay);
		void Cancel(uint id);
	}

	/** 
	* Scheduler
	* This class is equivalent to the Timer class in the Phoenix JS library.
	*/
	public sealed class Scheduler {

		private readonly Action callback;
		private readonly Func<int, TimeSpan> timerCalc;
		private readonly IDelayedExecutor delayedExecutor;
		private DelayedExecution? execution = null;
		private int tries = 0;

		public Scheduler(Action callback, Func<int, TimeSpan> timerCalc, IDelayedExecutor delayedExecutor) {
			this.callback = callback;
			this.timerCalc = timerCalc;
			this.delayedExecutor = delayedExecutor;
		}

		public void Reset() {
			tries = 0;
			execution?.Cancel();
			execution = null;
		}

		public void ScheduleTimeout() {
			execution?.Cancel();
			execution = delayedExecutor.Execute(() => {
				tries += 1;
				callback();
			}, timerCalc(tries + 1));
		}
	}

	// Provide a default delayed executor that uses a System timer.

	public sealed class TimerBasedExecutor : IDelayedExecutor {
		// Please ensure that you always start from 1, and leave 0 for uninitialized id
		private uint id = 1;
		private readonly Dictionary<uint, Timer> timers = new Dictionary<uint, Timer>();


		public DelayedExecution Execute(Action action, TimeSpan delay) {

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

