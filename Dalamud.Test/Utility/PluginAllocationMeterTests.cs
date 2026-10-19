using System;

using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class PluginAllocationMeterTests
    {
        // byte[1000] costs exactly 1024 bytes on x64; the slack covers nothing but rounding in the runtime's counter.
        private const long ArrayBytes = 1024;
        private const long Slack = 4096;

        [Fact]
        public void NestedMeasurementsAreExclusive()
        {
            var outer = new PluginAllocationMeter("TestOuter", PluginAllocationMeter.Update);
            var inner = new PluginAllocationMeter("TestInner", PluginAllocationMeter.AddonEvents);
            var inner2 = new PluginAllocationMeter("TestInner2", PluginAllocationMeter.Chat);
            try
            {
                var outerBegin = PluginAllocationMeter.Begin();
                Allocate(200);
                var innerBegin = PluginAllocationMeter.Begin();
                Allocate(300);
                inner.End(innerBegin);
                Allocate(50);
                var inner2Begin = PluginAllocationMeter.Begin();
                Allocate(100);
                inner2.End(inner2Begin);
                Allocate(50);
                outer.End(outerBegin);

                AssertAbout(300 * ArrayBytes, outer.Bytes);
                AssertAbout(300 * ArrayBytes, inner.Bytes);
                AssertAbout(100 * ArrayBytes, inner2.Bytes);
            }
            finally
            {
                outer.Retire();
                inner.Retire();
                inner2.Retire();
            }
        }

        [Fact]
        public void SameMeterInsideItselfCountsEachByteOnce()
        {
            var meter = new PluginAllocationMeter("TestReentrant", PluginAllocationMeter.Draw);
            try
            {
                var outerBegin = PluginAllocationMeter.Begin();
                Allocate(100);
                var innerBegin = PluginAllocationMeter.Begin();
                Allocate(50);
                meter.End(innerBegin);
                meter.End(outerBegin);

                AssertAbout(150 * ArrayBytes, meter.Bytes);
            }
            finally
            {
                meter.Retire();
            }
        }

        [Fact]
        public void MeasurementsInARowAddUp()
        {
            var first = new PluginAllocationMeter("TestFirst", PluginAllocationMeter.Draw);
            var second = new PluginAllocationMeter("TestSecond", PluginAllocationMeter.Update);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var begin = PluginAllocationMeter.Begin();
                    Allocate(10);
                    first.End(begin);
                    begin = PluginAllocationMeter.Begin();
                    Allocate(20);
                    second.End(begin);
                }

                AssertAbout(30 * ArrayBytes, first.Bytes);
                AssertAbout(60 * ArrayBytes, second.Bytes);
            }
            finally
            {
                first.Retire();
                second.Retire();
            }
        }

        private static void Allocate(int count)
        {
            for (var i = 0; i < count; i++)
                GC.KeepAlive(new byte[1000]);
        }

        private static void AssertAbout(long expected, long actual) =>
            Assert.InRange(actual, expected, expected + Slack);
    }
}
