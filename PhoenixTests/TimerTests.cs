using System;
using NUnit.Framework;
using Phoenix;


namespace PhoenixTests {

	[TestFixture()]
	public class TimerTests {

		[Test()]
		public void TimerInvokationTest() {

			var works = false;
			var executor = new TimerBasedExecutor();
			executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

			Assert.IsFalse(works);
			Assert.That(() => works, Is.True.After(10, 1));
		}

		[Test()]
		public void TimerResetTest() {

			var works = false;
			var executor = new TimerBasedExecutor();
			var timerId = executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

			Assert.IsFalse(works);
			executor.Cancel(timerId);
			System.Threading.Thread.Sleep(10);
			Assert.IsFalse(works);
		}
	}
}

