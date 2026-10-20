#nullable enable

using System.Linq;
using System.Reflection;

using Dalamud.Game;
using Dalamud.Game.Gui.PartyFinder;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class PartyFinderGuiHookTargetsTests
    {
        public PartyFinderGuiHookTargetsTests()
        {
            // Reporting the notice looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void TheHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("PartyFinderGui");

            Assert.Equal(nint.Zero, PartyFinderGui.FindHookTarget(targets));
            Assert.Equal(["InfoProxyCrossRealm.ReceiveListing"], targets.SkippedMembers);
        }

        [Fact]
        public void ItStartsWithoutTheHook()
        {
            Kit.AssertConstructorUsesHookTargets(typeof(PartyFinderGui), nameof(PartyFinderGui.FindHookTarget));

            var ctor = typeof(PartyFinderGui).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var partyFinder = (PartyFinderGui)ctor.Invoke([new TargetSigScanner()]);

            Assert.Null(typeof(PartyFinderGui).GetField("receiveListingHook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(partyFinder));
            Assert.False(FeatureDisabledEcho.Instance.Enqueue(HookTargets.EchoKeyPrefix + "PartyFinderGui", "again"));

            ((IInternalDisposableService)partyFinder).DisposeService();
        }
    }
}
