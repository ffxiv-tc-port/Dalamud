#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Utility.Internal;

using Xunit;

using Kind = Dalamud.Utility.Internal.AllocationSampler.ThreadKind;

namespace Dalamud.Test.Utility
{
    // The per-thread type table of other threads, and the labels of threads without a description.
    public class AllocationSamplerOtherThreadsTests
    {
        private const long Mb = 1024 * 1024;
        private const string Prefix = "Allocation samples over the last 60 min: ";
        private const string OtherTypes = "(other types)";

        [Fact]
        public void PairTableStopsGrowingAtItsLimitAndKeepsEveryByte()
        {
            var window = new AllocationSampler.Window();
            window.Add(Kind.Other, "A", "Early", 10);
            for (var i = 0; i < AllocationSampler.MaxThreadTypes + 500; i++)
                window.Add(Kind.Other, "A", $"T{i}", 1);
            window.Add(Kind.Other, "A", "Early", 5);
            window.Add(Kind.Other, "B", "Late", 7);
            window.Add(Kind.Other, "B", "Late", 7);

            Assert.Equal(AllocationSampler.MaxThreadTypes + 2, window.OtherThreadTypes.Count);
            Assert.Equal(15, window.OtherThreadTypes[("A", "Early")]);
            Assert.Equal(501, window.OtherThreadTypes[("A", OtherTypes)]);
            Assert.Equal(14, window.OtherThreadTypes[("B", OtherTypes)]);
            Assert.False(window.OtherThreadTypes.ContainsKey(("B", "Late")));
            foreach (var thread in window.OtherThreads.Keys)
                Assert.Equal(window.OtherThreads[thread], window.OtherThreadTypes.Where(x => x.Key.Thread == thread).Sum(x => x.Value));
        }

        [Fact]
        public void OnlyOtherThreadsGetPairs()
        {
            var window = new AllocationSampler.Window();
            window.Add(Kind.Main, null, "M", Mb);
            window.Add(Kind.Pool, ".NET TP Worker", "P", Mb);
            window.Add(Kind.Self, null, "S", Mb);
            window.Add(Kind.Other, null, "O", Mb);

            Assert.Equal(new[] { (("(unnamed)", "O"), Mb) }, window.OtherThreadTypes.Select(x => (x.Key, x.Value)));
        }

        [Fact]
        public void TopThreeThreadsGetTheirTopFiveTypes()
        {
            var window = new AllocationSampler.Window();
            for (var t = 1; t <= 4; t++)
            {
                for (var i = 1; i <= 7; i++)
                    window.Add(Kind.Other, $"Thread{t}", $"T{t}{i}", i * t * Mb);
            }

            var lines = AllocationSamplerReportTests.Report(window, (8, 5, 0, 3, 5));
            var typed = lines.Where(x => x.Contains("types of other thread rank ", StringComparison.Ordinal)).ToList();

            Assert.Equal(15, typed.Count);
            Assert.Equal(Prefix + "types of other thread rank 1 Thread4: #1 T47 28.0 MB (25.0% of that thread)", typed[0]);
            Assert.Equal(Prefix + "types of other thread rank 1 Thread4: #5 T43 12.0 MB (10.7% of that thread)", typed[4]);
            Assert.Equal(Prefix + "types of other thread rank 2 Thread3: #1 T37 21.0 MB (25.0% of that thread)", typed[5]);
            Assert.Equal(Prefix + "types of other thread rank 3 Thread2: #5 T23 6.0 MB (10.7% of that thread)", typed[14]);
            Assert.DoesNotContain(typed, x => x.Contains("Thread1", StringComparison.Ordinal));
            Assert.Equal(lines.Count - 15, lines.FindIndex(x => x.Contains("types of other thread rank ", StringComparison.Ordinal)));
            Assert.Empty(AllocationSamplerReportTests.Report(window, (8, 5, 0, 0, 5)).Where(x => x.Contains("types of other thread", StringComparison.Ordinal)));
        }

        [Fact]
        public void TypeLinesStayOutOfTheOlderLineShapes()
        {
            var window = new AllocationSampler.Window();
            window.Add(Kind.Other, "(unnamed, coreclr.dll)", "System.String", 3 * Mb);

            var typed = AllocationSamplerReportTests.Report(window, (8, 5, 25, 3, 5))
                                                    .Where(x => x.Contains("types of other thread", StringComparison.Ordinal))
                                                    .ToList();

            Assert.Single(typed);
            foreach (var shape in new[] { "type #", "other thread #", "main thread", " MB = ", "%)" })
                Assert.DoesNotContain(typed, x => x.Contains(shape, StringComparison.Ordinal));
        }

        [Fact]
        public void AnUnnamedThreadOfThisProcessIsLabelledByItsStartModule()
        {
            var lookUp = AllocationSampler.NewThreadNameLookUp();
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            uint unnamedId = 0, namedId = 0;
            var unnamed = new Thread(() =>
            {
                unnamedId = GetCurrentThreadId();
                started.Set();
                release.Wait();
            });
            var named = new Thread(() =>
            {
                namedId = GetCurrentThreadId();
                release.Wait();
            }) { Name = "Sampler test worker" };

            unnamed.Start();
            named.Start();
            try
            {
                started.Wait();
                SpinWait.SpinUntil(() => Volatile.Read(ref namedId) != 0);

                var label = lookUp(unnamedId, null);
                Assert.Equal("(unnamed, coreclr.dll)", label);
                Assert.Same(label, lookUp(unnamedId, label));
                Assert.Equal("Sampler test worker", lookUp(namedId, null));
            }
            finally
            {
                release.Set();
                unnamed.Join();
                named.Join();
            }
        }

        [Fact]
        public void TheFirstThreadOfThisProcessStartsInTheExecutable()
        {
            using var process = Process.GetCurrentProcess();
            // Other tests start and end threads meanwhile; one that ended while listed has no start time.
            var first = process.Threads.Cast<ProcessThread>().Select(x => (Thread: x, Started: StartedOrNull(x))).Where(x => x.Started is not null).MinBy(x => x.Started)!.Thread;

            Assert.True(GameNativeThreadReport.TryReadStartAddress((uint)first.Id, out var start));
            Assert.Equal(
                Path.GetFileName(process.MainModule!.FileName),
                AllocationSampler.ModuleFileNameAt(start),
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnAddressOutsideEveryModuleHasNoModule()
        {
            var block = Marshal.AllocHGlobal(4096);
            try
            {
                Assert.Null(AllocationSampler.ModuleFileNameAt((nuint)block));
                Assert.Null(AllocationSampler.ModuleFileNameAt(0));
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }

        [Fact]
        public void WithoutAStartAddressOrAModuleTheLabelIsPlainUnnamed()
        {
            var noStart = new AllocationSampler.UnnamedThreadLabels(Fail, _ => "never.dll");
            var noModule = new AllocationSampler.UnnamedThreadLabels(At(0x1000), _ => null);
            var real = new AllocationSampler.UnnamedThreadLabels(GameNativeThreadReport.TryReadStartAddress, AllocationSampler.ModuleFileNameAt);

            Assert.Equal(AllocationSampler.ThreadNames.Unnamed, noStart.Label(1));
            Assert.Equal(AllocationSampler.ThreadNames.Unnamed, noModule.Label(1));
            Assert.Equal(AllocationSampler.ThreadNames.Unnamed, real.Label(uint.MaxValue - 2));
        }

        [Fact]
        public void AModuleIsLookedUpOncePerStartAddress()
        {
            var calls = new List<nuint>();
            var labels = new AllocationSampler.UnnamedThreadLabels(
                (uint id, out nuint start) =>
                {
                    start = id;
                    return true;
                },
                address =>
                {
                    calls.Add(address);
                    return $"m{address}.dll";
                });

            var first = labels.Label(5);
            Assert.Equal("(unnamed, m5.dll)", first);
            Assert.Same(first, labels.Label(5));
            Assert.Equal(new nuint[] { 5 }, calls);

            for (uint i = 100; i < 100 + AllocationSampler.UnnamedThreadLabels.MaxStarts; i++)
                labels.Label(i);
            Assert.True(labels.Count <= AllocationSampler.UnnamedThreadLabels.MaxStarts);
        }

        [Fact]
        public void AMissingEntryPointTurnsTheLookUpOffInsteadOfThrowing()
        {
            var reads = 0;
            var labels = new AllocationSampler.UnnamedThreadLabels(
                (uint _, out nuint start) =>
                {
                    reads++;
                    throw new EntryPointNotFoundException();
                },
                _ => "x.dll");

            Assert.Equal(AllocationSampler.ThreadNames.Unnamed, labels.Label(1));
            Assert.Equal(AllocationSampler.ThreadNames.Unnamed, labels.Label(2));
            Assert.Equal(1, reads);
        }

        private static DateTime? StartedOrNull(ProcessThread thread)
        {
            try
            {
                return thread.StartTime;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        private static bool Fail(uint osThreadId, out nuint start)
        {
            start = 0;
            return false;
        }

        private static AllocationSampler.StartAddressRead At(nuint address) =>
            (uint _, out nuint start) =>
            {
                start = address;
                return true;
            };

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
