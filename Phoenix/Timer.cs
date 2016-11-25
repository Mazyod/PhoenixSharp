using System;


namespace Phoenix {

	// Creates a timer that accepts a `timerCalc` function to perform
	// calculated timeout retries, such as exponential backoff.
	//
	// ## Examples
	//
	//    let reconnectTimer = new Timer(() => this.connect(), function(tries){
	//      return [1000, 5000, 10000][tries - 1] || 10000
	//    })
	//    reconnectTimer.scheduleTimeout() // fires after 1000
	//    reconnectTimer.scheduleTimeout() // fires after 5000
	//    reconnectTimer.reset()
	//    reconnectTimer.scheduleTimeout() // fires after 1000
	//
	internal sealed class Timer {

		private int tries = 0;
		private Func<int, TimeSpan> delayFunction;
		private System.Timers.Timer timer;

		public bool isActive {
			get { return timer.Enabled; }
		}

		public Timer(Action action, TimeSpan fixedDelay)
			: this(action, (_) => fixedDelay) {
		}

		public Timer(Action callback, Func<int, TimeSpan> delayFunction) {

			this.delayFunction = delayFunction;

			timer = new System.Timers.Timer();
			timer.AutoReset = false;
			timer.Elapsed += (sender, e) => {
				tries += 1;
				callback();
			};
		}

		public void Reset() {
			tries = 0;
			timer.Stop();
		}

		public void ScheduleTimeout() {
			
			timer.Stop();
			timer.Interval = delayFunction(tries).TotalMilliseconds;
			timer.Start();
		}
	}
}

