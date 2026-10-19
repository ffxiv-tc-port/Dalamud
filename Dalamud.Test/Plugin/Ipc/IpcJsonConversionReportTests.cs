#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Dalamud.Plugin.Ipc.Internal;
using Dalamud.Test.Utility;
using Dalamud.Utility.Internal;

using Serilog;

using Xunit;

using Direction = Dalamud.Utility.Internal.IpcJsonConversionReport.Direction;
using Key = Dalamud.Utility.Internal.IpcJsonConversionReport.Key;

namespace Dalamud.Test.Plugin.Ipc
{
    // Call gates count each JSON conversion by endpoint; the same-type paths count nothing.
    public class IpcJsonConversionReportTests
    {
        private static readonly IpcJsonConversionReport.Counter Shared = IpcJsonConversionReport.Counter.Shared;

        [Fact]
        public void AFunctionArgumentOfAnotherTypeIsCountedOncePerCall()
        {
            var channel = new CallGateChannel("Test.Json.FuncArg") { Func = new Func<long, bool>(x => x == 5) };

            Assert.True(channel.InvokeFunc<bool>([5]));
            Assert.True(channel.InvokeFunc<bool>([5]));

            Assert.Equal(2, Shared.Peek(new Key("Test.Json.FuncArg", Direction.FunctionArgument, "Int32", "Int64")));
        }

        [Fact]
        public void AResultOfAnotherTypeIsCountedAsAFunctionResult()
        {
            var channel = new CallGateChannel("Test.Json.FuncResult") { Func = new Func<int>(() => 3) };

            Assert.Equal(3L, channel.InvokeFunc<long>(null));

            Assert.Equal(1, Shared.Peek(new Key("Test.Json.FuncResult", Direction.FunctionResult, "Int32", "Int64")));
            Assert.Equal(0, Shared.Peek(new Key("Test.Json.FuncResult", Direction.FunctionArgument, "Int32", "Int64")));
        }

        [Fact]
        public void ActionAndMessageArgumentsAreCountedApart()
        {
            var channel = new CallGateChannel("Test.Json.Action") { Action = new Action<long>(_ => { }) };
            channel.Subscribe(new Action<long>(_ => { }));

            channel.InvokeAction([1]);
            channel.SendMessage([2]);

            Assert.Equal(1, Shared.Peek(new Key("Test.Json.Action", Direction.ActionArgument, "Int32", "Int64")));
            Assert.Equal(1, Shared.Peek(new Key("Test.Json.Action", Direction.MessageArgument, "Int32", "Int64")));
        }

        [Fact]
        public void SameTypesAndNullsAreNotCounted()
        {
            const string name = "Test.Json.Same";
            var channel = new CallGateChannel(name) { Func = new Func<int, string?, object?>((x, s) => null) };

            channel.InvokeFunc<object?>([1, null]);
            channel.InvokeFunc<object?>([1, "s"]);

            foreach (var direction in Enum.GetValues<Direction>())
            {
                foreach (var (from, to) in new[] { ("Int32", "Int32"), ("String", "String"), ("Object", "Object") })
                    Assert.Equal(0, Shared.Peek(new Key(name, direction, from, to)));
            }
        }

        [Fact]
        public void TheCounterStopsGrowingAtItsLimitAndKeepsEveryCount()
        {
            var counter = new IpcJsonConversionReport.Counter();
            for (var i = 0; i < IpcJsonConversionReport.Counter.MaxKeys + 10; i++)
                counter.Count($"E{i}", Direction.FunctionArgument, typeof(int), typeof(long));
            counter.Count("E0", Direction.FunctionArgument, typeof(int), typeof(long));
            counter.Count("Late", Direction.FunctionResult, typeof(int), typeof(long));

            var counts = counter.Take();

            Assert.Equal(IpcJsonConversionReport.Counter.MaxKeys + 2, counts.Count);
            Assert.Equal(2, counts[new Key("E0", Direction.FunctionArgument, "Int32", "Int64")]);
            Assert.Equal(10, counts[new Key(IpcJsonConversionReport.Counter.OtherEndpoints, Direction.FunctionArgument, "?", "?")]);
            Assert.Equal(1, counts[new Key(IpcJsonConversionReport.Counter.OtherEndpoints, Direction.FunctionResult, "?", "?")]);
            Assert.Equal(IpcJsonConversionReport.Counter.MaxKeys + 12, counts.Values.Sum());
            Assert.Empty(counter.Take());
        }

        [Fact]
        public void CountingFromManyThreadsLosesNothing()
        {
            var counter = new IpcJsonConversionReport.Counter();

            Parallel.For(0, 64, t =>
            {
                for (var i = 0; i < 5000; i++)
                    counter.Count($"E{i % 7}", (Direction)(t % 4), typeof(int), typeof(long));
            });

            var counts = counter.Take();
            Assert.Equal(64 * 5000, counts.Values.Sum());
            Assert.Equal(28, counts.Count);
        }

        [Fact]
        public void TheReportListsTheTopFive()
        {
            var counts = new Dictionary<Key, long>();
            for (var i = 1; i <= 7; i++)
                counts[new Key($"Plugin.E{i}", Direction.FunctionResult, "Dictionary`2", "Object")] = i * 1000;
            counts[new Key("Plugin.Arg", Direction.FunctionArgument, "Int32", "Nullable`1")] = 3600;

            var lines = Render(counts);

            Assert.Equal(
                new[]
                {
                    "IPC JSON conversions over the last 60 min: 31600 in total (8.8 per second) over 8 endpoint, direction and type combinations",
                    "IPC JSON conversions over the last 60 min: #1 Plugin.E7 function result Dictionary`2 to Object: 7000 times (1.9 per second)",
                    "IPC JSON conversions over the last 60 min: #2 Plugin.E6 function result Dictionary`2 to Object: 6000 times (1.7 per second)",
                    "IPC JSON conversions over the last 60 min: #3 Plugin.E5 function result Dictionary`2 to Object: 5000 times (1.4 per second)",
                    "IPC JSON conversions over the last 60 min: #4 Plugin.E4 function result Dictionary`2 to Object: 4000 times (1.1 per second)",
                    "IPC JSON conversions over the last 60 min: #5 Plugin.Arg function argument Int32 to Nullable`1: 3600 times (1.0 per second)",
                },
                lines);
        }

        [Fact]
        public void AnEmptyWindowIsOneLine()
        {
            Assert.Equal(new[] { "IPC JSON conversions over the last 60 min: none" }, Render(new Dictionary<Key, long>()));
        }

        [Fact]
        public void LinesStayOutOfTheOlderLineShapes()
        {
            var counts = new Dictionary<Key, long> { [new Key("A", Direction.FunctionResult, "Int32", "Int64")] = 5 };

            foreach (var line in Render(counts))
            {
                foreach (var shape in new[] { "Allocation", "type #", "other thread #", "main thread", " MB", "GC over" })
                    Assert.DoesNotContain(shape, line, StringComparison.Ordinal);
            }
        }

        private static List<string> Render(Dictionary<Key, long> counts)
        {
            var sink = new AllocationSamplerReportTests.CaptureSink();
            using (var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger())
                IpcJsonConversionReport.Report(counts, "over the last 60 min", 3600, 5, (template, values) => logger.Information(template, values));
            return sink.Lines;
        }
    }
}
