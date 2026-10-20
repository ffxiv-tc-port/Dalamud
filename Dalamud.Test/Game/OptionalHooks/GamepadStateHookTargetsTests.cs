#nullable enable

using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Game;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

using ClientStateService = Dalamud.Game.ClientState.ClientState;
using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class GamepadStateHookTargetsTests
    {
        public GamepadStateHookTargetsTests()
        {
            // Reporting the notice looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void ThePollHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("GamepadState");

            Assert.Equal(nint.Zero, GamepadState.FindHookTarget(targets));
            Assert.Equal(["PadDevice.Poll"], targets.SkippedMembers);
        }

        [Fact]
        public void ItStartsWithoutTheHookAndReadsNothing()
        {
            Kit.AssertConstructorUsesHookTargets(typeof(GamepadState), nameof(GamepadState.FindHookTarget));

            var ctor = typeof(GamepadState).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var clientState = RuntimeHelpers.GetUninitializedObject(typeof(ClientStateService));
            var gamepad = (GamepadState)ctor.Invoke([clientState, new TargetSigScanner()]);

            Assert.Null(typeof(GamepadState).GetField("gamepadPoll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gamepad));
            Assert.Equal(Vector2.Zero, gamepad.LeftStick);
            Assert.Equal(nint.Zero, gamepad.GamepadInputAddress);
            Assert.False(FeatureDisabledEcho.Instance.Enqueue(HookTargets.EchoKeyPrefix + "GamepadState", "again"));

            ((IInternalDisposableService)gamepad).DisposeService();
        }
    }
}
