using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;


namespace PhoenixTests {

	[TestFixture()]
	public class ReplySerializationTests {

		private Reply sampleReply = new Reply() {
			status = Reply.Status.Error,
			response = new Dictionary<string, object>() {
				{ "test", "one" },
			}
		};

		[Test()]
		public void DeserializationTest() {

			var serialized = new Dictionary<string, object>() {
				{ "status", Reply.Status.Error.AsString() }, 
				{"response", new Dictionary<string, object>() {
						{ "test", "one" }}
				},
			};

			var deserialized = ReplySerialization.Deserialize(serialized);

			Assert.AreEqual(sampleReply, deserialized);
			CollectionAssert.AreEquivalent(sampleReply.response, deserialized.response);
		}
	}
}

