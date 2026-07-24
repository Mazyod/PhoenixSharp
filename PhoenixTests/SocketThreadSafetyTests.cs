using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class SocketThreadSafetyTests : PhoenixTestBase
    {
        [Test]
        public void MakeRefReturnsUniqueRefsWhenCalledConcurrently()
        {
            using var socket = CreateBasicSocket();
            const int threadCount = 16;
            const int refsPerThread = 25_000;
            var startGate = new ManualResetEventSlim();
            var readyGate = new CountdownEvent(threadCount);
            var refs = new string[threadCount][];
            var threads = new Thread[threadCount];

            for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
            {
                var capturedThreadIndex = threadIndex;
                refs[threadIndex] = new string[refsPerThread];
                threads[threadIndex] = new Thread(() =>
                {
                    readyGate.Signal();
                    startGate.Wait();

                    for (var refIndex = 0; refIndex < refsPerThread; refIndex++)
                    {
                        refs[capturedThreadIndex][refIndex] = socket.MakeRef();
                    }
                })
                {
                    IsBackground = true
                };
                threads[threadIndex].Start();
            }

            readyGate.Wait();
            startGate.Set();

            foreach (var thread in threads)
            {
                thread.Join();
            }

            var uniqueRefs = new HashSet<string>();
            foreach (var threadRefs in refs)
            {
                uniqueRefs.UnionWith(threadRefs);
            }

            Assert.That(
                uniqueRefs.Count,
                Is.EqualTo(threadCount * refsPerThread),
                "Socket.MakeRef returned duplicate refs during concurrent calls."
            );
        }
    }
}
