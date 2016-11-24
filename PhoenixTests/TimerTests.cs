using System;
using NUnit.Framework;
using Phoenix;


namespace PhoenixTests {

	[TestFixture()]
	public class TimerTests {

		[Test()]
		public void TimerInvokationTest() {

			var works = false;
			var timer = new Timer(() => works = true, TimeSpan.FromMilliseconds(1));
			timer.ScheduleTimeout();

			Assert.IsFalse(works);
			System.Threading.Thread.Sleep(10);
			Assert.IsTrue(works);
		}

		[Test()]
		public void TimerResetTest() {

			var works = false;
			var timer = new Timer(() => works = true, TimeSpan.FromMilliseconds(1));
			timer.ScheduleTimeout();

			Assert.IsFalse(works);
			timer.Reset();
			System.Threading.Thread.Sleep(10);
			Assert.IsFalse(works);
		}
	}
}

