using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Phoenix;

namespace PhoenixTests
{
    [TestFixture, Category("Unit")]
    public class PresencePerformanceTests
    {
        private const int DiffCount = 100;
        private const int IterationsPerRun = 5;
        private const long MaxAllocatedBytesPerSync = 1_000_000;
        private const int MeasurementRuns = 5;
        private const int PresenceCount = 500;
        private const int WarmupIterations = 3;

        [Test, Category("Performance")]
        public void StateAndDiffSyncAllocationStaysWithinBudgetTest()
        {
            var currentState = CreateCurrentState();
            var newState = CreateNewState();
            var diff = CreateDiff();

            DriveSyncs(
                currentState,
                newState,
                diff,
                WarmupIterations
            );

            var allocatedBytes = new long[MeasurementRuns];
            var elapsedTicks = new long[MeasurementRuns];
            for (var run = 0; run < MeasurementRuns; run++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                var beforeTicks = Stopwatch.GetTimestamp();
                DriveSyncs(
                    currentState,
                    newState,
                    diff,
                    IterationsPerRun
                );
                elapsedTicks[run] = Stopwatch.GetTimestamp() - beforeTicks;
                allocatedBytes[run] =
                    GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            }

            Array.Sort(allocatedBytes);
            Array.Sort(elapsedTicks);
            var medianAllocatedBytes =
                allocatedBytes[MeasurementRuns / 2] / (double)IterationsPerRun;
            var medianElapsedMilliseconds =
                elapsedTicks[MeasurementRuns / 2]
                * 1_000.0
                / Stopwatch.Frequency
                / IterationsPerRun;
            var measurement =
                $"Median 500x3 state sync plus 100-entry diff: "
                + $"{medianAllocatedBytes:F0} bytes/sync, "
                + $"{medianElapsedMilliseconds:F3} ms/sync.";
            TestContext.Out.WriteLine(measurement);

            Assert.That(
                medianAllocatedBytes,
                Is.LessThanOrEqualTo(MaxAllocatedBytesPerSync),
                measurement
            );
        }

        private static Dictionary<string, PresencePayload> CreateCurrentState()
        {
            var state = new Dictionary<string, PresencePayload>(PresenceCount);
            for (var presence = 0; presence < PresenceCount; presence++)
            {
                state.Add(
                    PresenceKey(presence),
                    PresenceWithRefs(
                        StableRef(presence, 0),
                        StableRef(presence, 1),
                        $"{presence}:departed"
                    )
                );
            }

            return state;
        }

        private static Dictionary<string, PresencePayload> CreateNewState()
        {
            var state = new Dictionary<string, PresencePayload>(PresenceCount);
            for (var presence = 0; presence < PresenceCount; presence++)
            {
                state.Add(
                    PresenceKey(presence),
                    PresenceWithRefs(
                        StableRef(presence, 0),
                        StableRef(presence, 1),
                        $"{presence}:joined"
                    )
                );
            }

            return state;
        }

        private static Presence.Diff CreateDiff()
        {
            var joins = new Dictionary<string, PresencePayload>(DiffCount);
            var leaves = new Dictionary<string, PresencePayload>(DiffCount);
            for (var presence = 0; presence < DiffCount; presence++)
            {
                var key = PresenceKey(presence);
                joins.Add(
                    key,
                    PresenceWithRefs($"{presence}:diff-joined")
                );
                leaves.Add(
                    key,
                    PresenceWithRefs(StableRef(presence, 0))
                );
            }

            return new Presence.Diff
            {
                Joins = joins,
                Leaves = leaves
            };
        }

        private static void DriveSyncs(
            Dictionary<string, PresencePayload> currentState,
            Dictionary<string, PresencePayload> newState,
            Presence.Diff diff,
            int iterations
        )
        {
            var checksum = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var state = Presence.SyncState(currentState, newState);
                state = Presence.SyncDiff(state, diff);
                checksum += state.Count;
                checksum += state["presence-0"].Metas.Count;
            }

            GC.KeepAlive(checksum);
        }

        private static string PresenceKey(int presence)
        {
            return $"presence-{presence}";
        }

        private static PresencePayload PresenceWithRefs(params string[] refs)
        {
            var metas = new List<PresenceMeta>(refs.Length);
            foreach (var phxRef in refs)
            {
                metas.Add(new PresenceMeta { PhxRef = phxRef });
            }

            return new PresencePayload { Metas = metas };
        }

        private static string StableRef(int presence, int meta)
        {
            return $"{presence}:stable-{meta}";
        }
    }
}
