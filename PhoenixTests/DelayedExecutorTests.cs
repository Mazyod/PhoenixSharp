using System;
using System.Threading;
using NUnit.Framework;
using Phoenix;

namespace PhoenixTests
{
    [TestFixture]
    public class DelayedExecutorTests
    {
        [Test]
        public void DelayedExecutorInvocationTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

            Assert.IsFalse(works);
            Assert.That(() => works, Is.True.After(10, 1));
        }

        [Test]
        public void DelayedExecutorResetTest()
        {
            var works = false;
            var executor = new TaskDelayedExecutor();
            var execution = executor.Execute(() => works = true, TimeSpan.FromMilliseconds(1));

            Assert.IsFalse(works);
            execution.Cancel();
            Thread.Sleep(10);
            Assert.IsFalse(works);
        }
    }
}