#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Utility.Internal;

using Xunit;

using NativeThread = Dalamud.Utility.Internal.GameNativeThreadReport.NativeThread;

namespace Dalamud.Test.Utility
{
    // Counts threads by the image their start address lies in. The real test stands in kernel32's Sleep for the game:
    // a native thread that starts there, is counted, ends, and is then counted as gone.
    public class GameNativeThreadReportTests
    {
        private const uint CreateSuspended = 0x4;
        private const uint GetModuleHandleExFlagFromAddress = 0x4;
        private const uint GetModuleHandleExFlagUnchangedRefcount = 0x2;
        private static readonly long Minute = TimeSpan.TicksPerMinute;

        [Fact]
        public void WithoutAnEarlierCountNothingIsGoneOrNew()
        {
            var result = GameNativeThreadReport.Compare(null, [new(1, 0x10, 5 * Minute)], 0);

            Assert.Single(result.Current);
            Assert.Null(result.Gone);
            Assert.Null(result.New);
            Assert.Equal(1, result.Late);
        }

        [Fact]
        public void AnEndedThreadIsGoneAndOnlyLateCreationsCountAsLate()
        {
            var stays = new NativeThread(1, 0x10, 1 * Minute);
            var ends = new NativeThread(2, 0x10, 2 * Minute);
            var starts = new NativeThread(3, 0x20, 9 * Minute);

            var result = GameNativeThreadReport.Compare(new HashSet<NativeThread> { stays, ends }, [stays, starts], 5 * Minute);

            Assert.Equal([ends], result.Gone);
            Assert.Equal(1, result.New);
            Assert.Equal(1, result.Late);
        }

        [Fact]
        public void AnIdHandedToANewThreadCountsAsOneGoneAndOneNew()
        {
            var old = new NativeThread(7, 0x10, 1 * Minute);
            var reused = new NativeThread(7, 0x10, 30 * Minute);

            var result = GameNativeThreadReport.Compare(new HashSet<NativeThread> { old }, [reused], 5 * Minute);

            Assert.Equal([old], result.Gone);
            Assert.Equal(1, result.New);
        }

        [Fact]
        public void AFailedCountIsLoggedAndTheNextOneHasNothingToCompareWith()
        {
            var fail = false;
            var threads = new List<NativeThread> { new(1, 0x10, 0) };
            var report = new GameNativeThreadReport(
                Stopwatch.GetTimestamp(),
                TimeSpan.FromHours(1),
                "test.exe",
                0,
                0,
                () => fail ? throw new InvalidOperationException("no") : new GameNativeThreadReport.Snapshot(threads, 1, 0));
            Assert.Null(report.LastResult!.Gone);
            report.ReportUnfinished(Stopwatch.GetTimestamp());
            Assert.Empty(report.LastResult!.Gone!);

            fail = true;
            report.ReportUnfinished(Stopwatch.GetTimestamp());
            Assert.Null(report.LastResult);

            fail = false;
            report.ReportUnfinished(Stopwatch.GetTimestamp());
            Assert.Null(report.LastResult!.Gone);
        }

        [Fact]
        public void TheWindowReportsOnlyWhenItIsOver()
        {
            var reads = 0;
            var start = Stopwatch.GetTimestamp();
            var report = new GameNativeThreadReport(
                start,
                TimeSpan.FromHours(1),
                "test.exe",
                0,
                0,
                () =>
                {
                    reads++;
                    return new GameNativeThreadReport.Snapshot([], 0, 0);
                });

            report.Tick(start + (59 * 60 * Stopwatch.Frequency));
            Assert.Null(report.HourlyCount);
            report.Tick(start + (60 * 60 * Stopwatch.Frequency));
            Assert.True(report.HourlyCount!.Wait(10_000));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void ATickBeforeTheWindowIsOverAllocatesNothing()
        {
            var start = Stopwatch.GetTimestamp();
            var report = new GameNativeThreadReport(
                start,
                TimeSpan.FromHours(1),
                "test.exe",
                0,
                0,
                () => new GameNativeThreadReport.Snapshot([], 0, 0));
            for (var i = 0; i < 2000; i++)
                report.Tick(start + i);

            var before = AllocatedBytes.Start();
            for (var i = 0; i < 1000; i++)
                report.Tick(start + i);
            Assert.Equal(0, AllocatedBytes.Since(before));
            Assert.Null(report.HourlyCount);
        }

        [Fact]
        public void ATickWhileTheLastCountStillRunsAllocatesNothingAndStartsNoOtherCount()
        {
            using var release = new System.Threading.ManualResetEventSlim();
            var reads = 0;
            var start = Stopwatch.GetTimestamp();
            var report = new GameNativeThreadReport(
                start,
                TimeSpan.FromHours(1),
                "test.exe",
                0,
                0,
                () =>
                {
                    if (System.Threading.Interlocked.Increment(ref reads) > 1)
                        release.Wait(10_000);
                    return new GameNativeThreadReport.Snapshot([], 0, 0);
                });
            var due = start + (60 * 60 * Stopwatch.Frequency);
            report.Tick(due);
            var running = report.HourlyCount!;
            for (var i = 0; i < 2000; i++)
                report.Tick(due * 2);

            var before = AllocatedBytes.Start();
            for (var i = 0; i < 1000; i++)
                report.Tick(due * 2);
            var allocated = AllocatedBytes.Since(before);
            release.Set();

            Assert.Equal(0, allocated);
            Assert.Same(running, report.HourlyCount);
            Assert.True(running.Wait(10_000));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void ARealNativeThreadStartingInTheImageIsCountedThenGone()
        {
            var sleep = GetProcAddress(GetModuleHandleW("kernel32.dll"), "Sleep");
            Assert.NotEqual(0, sleep);
            Assert.True(GetModuleHandleExW(GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefcount, sleep, out var module));
            Assert.True(K32GetModuleInformation(Process.GetCurrentProcess().Handle, module, out var info, (uint)Marshal.SizeOf<ModuleInfo>()));
            var imageBase = (nuint)info.BaseOfDll;
            var imageSize = (nuint)info.SizeOfImage;
            GetSystemTimeAsFileTime(out var tenMinutesAgo);
            tenMinutesAgo -= 10 * Minute;

            var thread = CreateThread(0, 0, sleep, 0, CreateSuspended, out var id);
            Assert.NotEqual(0, thread);
            try
            {
                var report = new GameNativeThreadReport(
                    Stopwatch.GetTimestamp(),
                    TimeSpan.FromHours(1),
                    "image",
                    imageBase,
                    tenMinutesAgo,
                    () => GameNativeThreadReport.ReadThreadsIn(imageBase, imageSize));
                var mine = report.LastResult!.Current.Single(x => x.Id == id);
                Assert.Equal((nuint)sleep, mine.Start);
                Assert.True(mine.CreatedFileTime > tenMinutesAgo + (5 * Minute));

                Assert.NotEqual(uint.MaxValue, ResumeThread(thread));
                Assert.Equal(0u, WaitForSingleObject(thread, 10_000));

                report.ReportUnfinished(Stopwatch.GetTimestamp());
                Assert.DoesNotContain(report.LastResult!.Current, x => x.Id == id);
                Assert.Contains(mine, report.LastResult.Gone!);
            }
            finally
            {
                CloseHandle(thread);
            }
        }

        [Fact]
        public void TheShippedReportCountsInTheMainExecutable()
        {
            var report = GameNativeThreadReport.ForGame(Stopwatch.GetTimestamp());

            Assert.NotNull(report);
            Assert.NotNull(report.LastResult);
        }

        [Fact]
        public void EveryThreadOfThisProcessCanBeReadAndNoneStartsOutsideEveryImage()
        {
            var snapshot = GameNativeThreadReport.ReadThreadsIn(1, 1);

            Assert.Empty(snapshot.Threads);
            Assert.True(snapshot.Scanned > 0);
            Assert.Equal(0, snapshot.Unreadable);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint CreateThread(nint attributes, nuint stackSize, nint start, nint parameter, uint flags, out uint id);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(nint thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetModuleInformation(nint process, nint module, out ModuleInfo info, uint size);

        [DllImport("kernel32.dll")]
        private static extern void GetSystemTimeAsFileTime(out long time);

        [StructLayout(LayoutKind.Sequential)]
        private struct ModuleInfo
        {
            public nint BaseOfDll;
            public uint SizeOfImage;
            public nint EntryPoint;
        }
    }
}
