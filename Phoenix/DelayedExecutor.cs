using System;
using System.Threading.Tasks;

namespace Phoenix {

	public interface IDelayedExecution {
		void Cancel();
	}

	/**
	* IDelayedExecutor
	* This class is equivalent to javascript setTimeout/clearTimeout functions.
	*/
	public interface IDelayedExecutor {

		IDelayedExecution Execute(Action action, TimeSpan delay);
	}

	/** 
	* Scheduler
	* This class is equivalent to the Timer class in the Phoenix JS library.
	*/
	public sealed class Scheduler {

		private readonly Action callback;
		private readonly Func<int, TimeSpan> timerCalc;
		private readonly IDelayedExecutor delayedExecutor;
		private IDelayedExecution execution = null;
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

	// Provide a default delayed executor that uses a async / await.

	public sealed class DelayedExecution : IDelayedExecution {

		internal bool cancelled = false;

		public void Cancel() {
			cancelled = true;
		}
	}


	public sealed class TaskDelayedExecutor : IDelayedExecutor {

		public IDelayedExecution Execute(Action action, TimeSpan delay) {

			var execution = new DelayedExecution();
			Task.Delay(delay).ContinueWith(_ => {
				if (!execution.cancelled) {
					action();
				}
			});

			return execution;
		}
	}
}

