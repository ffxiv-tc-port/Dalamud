#nullable enable

using System.Linq;
using System.Reflection;

using Dalamud.Game;
using Dalamud.Game.Gui.Toast;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class ToastGuiHookTargetsTests
    {
        public ToastGuiHookTargetsTests()
        {
            // Reporting the notice looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void EveryHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("ToastGui");

            Assert.Equal((nint.Zero, nint.Zero, nint.Zero), ToastGui.FindHookTargets(targets));
            Assert.Equal(["UIModule.ShowWideText", "UIModule.ShowText", "UIModule.ShowErrorText"], targets.SkippedMembers);
        }

        [Fact]
        public void ItStartsWithoutHooksAndDropsWhatItCannotShow()
        {
            Kit.AssertConstructorUsesHookTargets(typeof(ToastGui), nameof(ToastGui.FindHookTargets));

            var ctor = typeof(ToastGui).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var toasts = (ToastGui)ctor.Invoke([new TargetSigScanner()]);

            foreach (var name in new[] { "showNormalToastHook", "showQuestToastHook", "showErrorToastHook" })
                Assert.Null(typeof(ToastGui).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(toasts));
            Assert.False(FeatureDisabledEcho.Instance.Enqueue(HookTargets.EchoKeyPrefix + "ToastGui", "again"));

            toasts.ShowNormal("normal");
            toasts.ShowQuest("quest");
            toasts.ShowError("error");
            toasts.UpdateQueue();
            toasts.UpdateQueue();

            ((IInternalDisposableService)toasts).DisposeService();
        }
    }
}
