using System;
using System.Collections.Generic;
using System.Timers;


namespace Phoenix {

	public interface IDelayedExecutor {

		uint Execute(Action action, TimeSpan delay);
		void Cancel(uint id);
	}

	public sealed class TimerBasedExecutor: IDelayedExecutor {
		// Please ensure that you always start from 1, and leave 0 for uninitialized id
		private uint id = 1;
		private Dictionary<uint, Timer> timers = new Dictionary<uint, Timer>();


		public uint Execute(Action action, TimeSpan delay) {

			var id = this.id++;
			var timer = new Timer();
			timer.Interval = delay.TotalMilliseconds;
			timer.AutoReset = false;
			timer.Elapsed += (sender, e) => {
				action();
				timers.Remove(id);
			};

			timer.Start();

			timers[id] = timer;
			return id;
		}

		public void Cancel(uint id) {

			if (timers.ContainsKey(id)) {
				timers[id].Stop();
				timers.Remove(id);
			}
		}
	}
}

