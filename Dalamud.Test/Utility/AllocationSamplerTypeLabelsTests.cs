#nullable enable

using System;

using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    // Type ids here are made-up integers: the labels only ever compare them, never read through them.
    public class AllocationSamplerTypeLabelsTests
    {
        private const string Suffix = AllocationSampler.TypeLabels.DalamudSuffix;

        [Fact]
        public void AMarkedIdGetsTheSuffixAndAnyOtherIdKeepsItsName()
        {
            var labels = new AllocationSampler.TypeLabels([0x1000, 0x2000], Suffix);

            Assert.Equal("Id (Dalamud)", labels.Label("Id", 0x1000));
            Assert.Equal("EndConditionally (Dalamud)", labels.Label("EndConditionally", 0x2000));
            Assert.Equal("Id", labels.Label("Id", 0x3000));
            Assert.Equal("Id", labels.Label("Id", 0));
        }

        [Fact]
        public void AnUnmarkedNameIsHandedBackAsIs()
        {
            var labels = new AllocationSampler.TypeLabels([0x1000], Suffix);
            var name = new string("Id".AsSpan());

            Assert.Same(name, labels.Label(name, 0x3000));
            Assert.Same(name, labels.Label(name, 0));
        }

        [Fact]
        public void TheSameInputGetsTheSameStringWithoutAllocating()
        {
            var labels = new AllocationSampler.TypeLabels([0x1000, 0x2000], Suffix);
            var first = labels.Label("Id", 0x1000);
            var sameValue = new string("Id".AsSpan());
            labels.Label(sameValue, 0x1000);
            labels.Label("Style", 0x3000);

            var before = GC.GetAllocatedBytesForCurrentThread();
            string? again = null;
            for (var i = 0; i < 1000; i++)
            {
                again = labels.Label(sameValue, 0x1000);
                labels.Label("Style", 0x3000);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Same(first, again);
        }

        [Fact]
        public void ANewNameForAMarkedIdIsMarkedAnew()
        {
            var labels = new AllocationSampler.TypeLabels([0x1000], Suffix);

            Assert.Equal("Id (Dalamud)", labels.Label("Id", 0x1000));
            Assert.Equal("ImRaii+Id (Dalamud)", labels.Label("ImRaii+Id", 0x1000));
        }

        [Fact]
        public void TheShippedLabelsMarkDalamudsImRaiiTypesOnly()
        {
            var labels = AllocationSampler.TypeLabels.ForDalamudImRaii();

            Assert.Equal("Id (Dalamud)", labels.Label("Id", typeof(ImRaii.Id).TypeHandle.Value));
            Assert.Equal("EndConditionally (Dalamud)", labels.Label("EndConditionally", typeof(ImRaii.EndConditionally).TypeHandle.Value));
            Assert.Equal("EndUnconditionally (Dalamud)", labels.Label("EndUnconditionally", typeof(ImRaii.EndUnconditionally).TypeHandle.Value));
            Assert.Equal("Color (Dalamud)", labels.Label("Color", typeof(ImRaii.Color).TypeHandle.Value));
            Assert.Equal("Style (Dalamud)", labels.Label("Style", typeof(ImRaii.Style).TypeHandle.Value));
            Assert.Equal("PlotStyle (Dalamud)", labels.Label("PlotStyle", typeof(ImRaii.PlotStyle).TypeHandle.Value));
            Assert.Equal("Id", labels.Label("Id", typeof(LookAlike.Id).TypeHandle.Value));
            Assert.Equal("String", labels.Label("String", typeof(string).TypeHandle.Value));
        }

        private static class LookAlike
        {
            public sealed class Id
            {
            }
        }
    }
}
