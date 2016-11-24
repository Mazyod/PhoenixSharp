using System;
using NUnit.Framework;
using Phoenix;


namespace PhoenixTests {

	[TestFixture()]
	public class ReplyTests {

		[Test()]
		public void ReplyEventNameTest() {
			Assert.AreEqual("chan_reply_test", Reply.EventName("test"));
		}
	}
}

