#nullable enable

using System.Linq;
using System.Reflection;

using Dalamud.Game;
using Dalamud.Game.Gui.FlyText;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class FlyTextGuiHookTargetsTests
    {
        public FlyTextGuiHookTargetsTests()
        {
            // Reporting the notice looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void TheHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("FlyTextGui");

            Assert.Equal(nint.Zero, FlyTextGui.FindHookTarget(targets));
            Assert.Equal(["AddonFlyText.CreateFlyText"], targets.SkippedMembers);
        }

        [Fact]
        public void ItStartsWithoutTheHook()
        {
            Kit.AssertConstructorUsesHookTargets(typeof(FlyTextGui), nameof(FlyTextGui.FindHookTarget));

            var ctor = typeof(FlyTextGui).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var flyText = (FlyTextGui)ctor.Invoke([new TargetSigScanner()]);

            Assert.Null(typeof(FlyTextGui).GetField("createFlyTextHook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flyText));
            Assert.False(FeatureDisabledEcho.Instance.Enqueue(HookTargets.EchoKeyPrefix + "FlyTextGui", "again"));

            ((IInternalDisposableService)flyText).DisposeService();
        }
    }
}
