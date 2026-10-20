#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.IoC.Internal;
using Dalamud.Test.Game;
using Dalamud.Utility.Internal;

using Xunit;


namespace Dalamud.Test.Utility
{
    public class HookTargetsTests
    {
        public HookTargetsTests()
        {
            // Reporting the notice looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void AFunctionIsKeptOnlyInsideTheGamesCode()
        {
            var targets = HookTargetsTestKit.Synthetic("Test");

            Assert.Equal((nint)0x1800, targets.Function("InText", "甲", 0x1800));
            Assert.Equal(nint.Zero, targets.Function("Zero", "乙", 0));
            Assert.Equal(nint.Zero, targets.Function("InRData", "乙", 0x3800));
            Assert.Equal(nint.Zero, targets.Function("PastText", "丙", 0x2000));

            Assert.Equal(["Zero", "InRData", "PastText"], targets.SkippedMembers);
            Assert.Equal(["乙", "丙"], targets.DisabledFeatures);
        }

        [Fact]
        public void WithoutSectionsNothingIsKept()
        {
            var targets = HookTargetsTestKit.NoSections("Test");

            Assert.Equal(nint.Zero, targets.Function("Any", "甲", 0x1800));
            Assert.Equal(nint.Zero, targets.Data("Any", "甲", 0x5800, 4));
            Assert.False(targets.InCode(0x1800, 1));
            Assert.Equal(["Any", "Any"], targets.SkippedMembers);
        }

        [Fact]
        public void DataMustFitInsideDotData()
        {
            var targets = HookTargetsTestKit.Synthetic("Test");

            Assert.Equal((nint)0x5800, targets.Data("Fits", "甲", 0x5800, 0x800));
            Assert.Equal(nint.Zero, targets.Data("CrossesTheEnd", "甲", 0x5800, 0x801));
            Assert.Equal(nint.Zero, targets.Data("InText", "甲", 0x1800, 4));
            Assert.Equal(["CrossesTheEnd", "InText"], targets.SkippedMembers);
        }

        [Fact]
        public void AVtableMustLieWhollyInsideRData()
        {
            var targets = HookTargetsTestKit.Synthetic("Test");

            Assert.True(targets.InReadOnlyData(0x3000, 0x1000));
            Assert.False(targets.InReadOnlyData(0x3001, 0x1000));
            Assert.False(targets.InReadOnlyData(0x1800, 8));
            Assert.False(targets.InReadOnlyData(0, 8));
            Assert.False(HookTargetsTestKit.NoSections("Test").InReadOnlyData(0x3000, 8));
            Assert.Empty(targets.SkippedMembers);
        }

        [Fact]
        public void ASlotOfARejectedVtableIsLeftOutWithoutLookingAtItsValue()
        {
            var targets = HookTargetsTestKit.Synthetic("Test");

            // void* parameters are passed as nint through the harness.
            Assert.Equal(nint.Zero, (nint)DetourHarness.Invoke(targets, "Slot", "Rejected", "甲", nint.Zero, (nint)0x1800)!);
            Assert.Equal((nint)0x1800, (nint)DetourHarness.Invoke(targets, "Slot", "Kept", "乙", (nint)0x3800, (nint)0x1800)!);
            Assert.Equal(nint.Zero, (nint)DetourHarness.Invoke(targets, "Slot", "PointsIntoData", "丙", (nint)0x3800, (nint)0x5800)!);
            Assert.Equal(["Rejected", "PointsIntoData"], targets.SkippedMembers);
        }

        [Fact]
        public void AGroupIsKeptOnlyWhole()
        {
            var whole = HookTargetsTestKit.Synthetic("Test");
            Assert.True(whole.AllOrNone("甲", ("A", 0x1800), ("B", 0x1900)));
            Assert.Empty(whole.SkippedMembers);

            var partial = HookTargetsTestKit.Synthetic("Test");
            Assert.False(partial.AllOrNone("甲", ("A", 0x1800), ("B", 0), ("C", 0x1A00)));

            // The missing one was recorded by its own check; the group adds the ones that were fine.
            Assert.Equal(["A", "C"], partial.SkippedMembers);
            Assert.Equal(["甲"], partial.DisabledFeatures);
        }

        [Fact]
        public void TheNoticeListsEachFeatureOnceAndCanEndDifferently()
        {
            var targets = HookTargetsTestKit.Synthetic("Test");
            Assert.Null(targets.FormatChatLine());

            targets.Skip("A", "甲", "x");
            targets.Skip("B", "乙", "x");
            targets.Skip("C", "甲", "x");

            Assert.Equal(string.Format(HookTargets.ChatLineFormat, "甲、乙", HookTargets.DefaultChatTail), targets.FormatChatLine());
            Assert.EndsWith("：甲、乙。改用別的方法。", targets.FormatChatLine("改用別的方法。"));
        }

        [Fact]
        public void TheNoticeIsQueuedOncePerServiceAndOnlyIfSomethingIsOff()
        {
            var quiet = HookTargetsTestKit.Synthetic("HookTargetsTests.Quiet");
            quiet.ReportDisabled();
            Assert.True(FeatureDisabledEcho.Instance.Enqueue(HookTargets.EchoKeyPrefix + "HookTargetsTests.Quiet", "first"));

            var loud = HookTargetsTestKit.Synthetic("HookTargetsTests.Loud");
            loud.Skip("A", "甲", "x");
            loud.ReportDisabled();
            Assert.False(FeatureDisabledEcho.Instance.Enqueue(HookTargets.EchoKeyPrefix + "HookTargetsTests.Loud", "again"));
        }

        [Fact]
        public void AScannerWhoseSectionsCannotBeReadLeavesEverythingOut()
        {
            var targets = HookTargets.For("Test", new HookTargetsTestKit.FakeScanner(new Dictionary<string, nint>()));

            Assert.Equal(nint.Zero, targets.Function("Any", "甲", 0x1800));
        }
    }
}
