#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class UnresolvedAddressLogTests
    {
        private readonly List<(string Key, string Line)> echoes = [];

        [Fact]
        public void AGeneratedMemberFunctionWithoutAnAddressIsRecognized()
        {
            // Never call into the game from a test: only an unresolved address is safe to call.
            Assert.Equal(0L, (long)UIGlobals.Addresses.PlaySoundEffect.Value);
            var ex = Assert.Throws<InvalidOperationException>(() => UIGlobals.PlaySoundEffect(1));

            Assert.True(UnresolvedAddressLog.TryGetUnresolvedAddresses(ex, out var names));
            Assert.Equal(["UIGlobals.PlaySoundEffect"], names);
        }

        [Fact]
        public void AGeneratedStaticAddressCalledThroughReflectionIsRecognized()
        {
            Assert.Equal(0L, (long)ActionManager.Addresses.Instance.Value);
            var instance = typeof(ActionManager).GetMethod(nameof(ActionManager.Instance), BindingFlags.Public | BindingFlags.Static)!;
            var ex = Assert.Throws<TargetInvocationException>(() => instance.Invoke(null, null));

            Assert.True(UnresolvedAddressLog.TryGetUnresolvedAddresses(ex, out var names));
            Assert.Equal(["ActionManager.Instance"], names);
        }

        [Fact]
        public void TheThrowHelperItselfIsRecognized()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => InteropGenerator.Runtime.ThrowHelper.ThrowNullAddress("Outer.Inner", "E8 ?? ?? ?? ??"));

            Assert.True(UnresolvedAddressLog.TryParseAddressName(ex, out var name));
            Assert.Equal("Outer.Inner", name);
        }

        [Theory]
        [InlineData("Sequence contains no elements")]
        [InlineData("Address for Missing is null. The resolver was either uninitialized or failed to resolve address with signature AA.")]
        [InlineData("Address for A.B is not null.")]
        [InlineData("Address for A B.C is null.")]
        [InlineData("Address for .B is null.")]
        [InlineData("Address for A. is null.")]
        [InlineData("address for A.B is null.")]
        public void OtherInvalidOperationsAreNotRecognized(string message)
        {
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new InvalidOperationException(message), out var names));
            Assert.Null(names);
        }

        [Fact]
        public void OtherExceptionTypesWithTheSameMessageAreNotRecognized()
        {
            const string message = "Address for A.B is null. The resolver was either uninitialized or failed to resolve address with signature AA.";

            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new NullReferenceException(), out _));
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new ArgumentException(message), out _));
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new DerivedInvalidOperation(message), out _));
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new ObjectDisposedException("Address for A.B is null."), out _));
        }

        [Fact]
        public void WrappersAndAggregatesAreSearchedThrough()
        {
            Assert.True(UnresolvedAddressLog.TryGetUnresolvedAddresses(new TypeInitializationException("T", Failure("A.B")), out var wrapped));
            Assert.Equal(["A.B"], wrapped);

            // Ocelot rethrows a frame's module failures as one AggregateException of wrapped exceptions.
            var frame = new AggregateException(new Exception("Module X threw", Failure("A.B")), Failure("C.D"), Failure("A.B"));
            Assert.True(UnresolvedAddressLog.TryGetUnresolvedAddresses(frame, out var both));
            Assert.Equal(["A.B", "C.D"], both);
        }

        [Fact]
        public void AnyOtherFailureInsideMakesItNotRecognized()
        {
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new AggregateException(Failure("A.B"), new NullReferenceException()), out _));
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new AggregateException(), out _));
            Assert.False(UnresolvedAddressLog.TryGetUnresolvedAddresses(new Exception("outer"), out _));
        }

        [Fact]
        public void EachPluginAndAddressIsLoggedOnceAndCountedAfterwards()
        {
            var log = this.NewLog(8);

            Assert.Equal(["A.B"], log.Record("PluginA", ["A.B"]).Fresh);
            Assert.Null(log.Record("PluginA", ["A.B"]).Fresh);
            Assert.Null(log.Record("PluginA", ["A.B"]).Fresh);
            Assert.Equal(["C.D"], log.Record("PluginA", ["A.B", "C.D"]).Fresh);
            Assert.Equal(["A.B"], log.Record("PluginB", ["A.B"]).Fresh);
            Assert.Equal(["A.B"], log.Record(null, ["A.B"]).Fresh);

            Assert.Equal(4L, log.CountOf("PluginA", "A.B"));
            Assert.Equal(1L, log.CountOf("PluginA", "C.D"));
            Assert.Equal(1L, log.CountOf("PluginB", "A.B"));
            Assert.Equal(1L, log.CountOf(null, "A.B"));
        }

        [Fact]
        public void EachPluginGetsOneChatLine()
        {
            var log = this.NewLog(8);
            log.RememberDisplayName("InventoryTools", "Allagan Tools");

            Assert.True(log.TryAbsorb(Failure("A.B"), "InventoryTools", "IFramework::Update"));
            Assert.True(log.TryAbsorb(Failure("A.B"), "InventoryTools", "IFramework::Update"));
            Assert.True(log.TryAbsorb(Failure("C.D"), "InventoryTools", "IUiBuilder::Draw"));
            Assert.True(log.TryAbsorb(Failure("C.D"), "Unnamed", "IUiBuilder::Draw"));
            Assert.True(log.TryAbsorb(Failure("C.D"), null, "PostDraw listener in OnAddonDraw"));

            Assert.Equal(3, this.echoes.Count);
            Assert.Equal(UnresolvedAddressLog.EchoKeyPrefix + "InventoryTools", this.echoes[0].Key);
            Assert.Contains("「Allagan Tools」", this.echoes[0].Line);
            Assert.Contains("A.B", this.echoes[0].Line);
            Assert.Contains("「Unnamed」", this.echoes[1].Line);
            Assert.DoesNotContain("「", this.echoes[2].Line);
            Assert.NotEqual(this.echoes[1].Key, this.echoes[2].Key);
        }

        [Fact]
        public void OtherExceptionsAreLeftToTheCaller()
        {
            var log = this.NewLog(8);

            Assert.False(log.TryAbsorb(new InvalidOperationException("from the plugin"), "PluginA", "IFramework::Update"));
            Assert.False(log.TryAbsorb(new NullReferenceException(), "PluginA", "IFramework::Update"));

            Assert.Empty(this.echoes);
            Assert.Equal(0L, log.CountOf("PluginA", "from the plugin"));
        }

        [Fact]
        public void AFullTableOnlyCounts()
        {
            var log = this.NewLog(2);
            log.Record("PluginA", ["A.B"]);
            log.Record("PluginA", ["C.D"]);

            var first = log.Record("PluginB", ["A.B"]);
            var second = log.Record("PluginB", ["C.D"]);
            var tracked = log.Record("PluginA", ["A.B"]);

            Assert.Null(first.Fresh);
            Assert.Null(first.EchoAddress);
            Assert.True(first.OverflowStarted);
            Assert.False(second.OverflowStarted);
            Assert.Null(tracked.Fresh);
            Assert.Equal(2L, log.UntrackedCount);
            Assert.Equal(2L, log.CountOf("PluginA", "A.B"));
            Assert.Equal(0L, log.CountOf("PluginB", "A.B"));

            Assert.True(log.TryAbsorb(Failure("E.F"), "PluginC", "IFramework::Update"));
            Assert.Empty(this.echoes);
            Assert.Equal(3L, log.UntrackedCount);
        }

        [Fact]
        public void DisplayNamesAreBounded()
        {
            var log = this.NewLog(1);
            log.RememberDisplayName("PluginA", "Plugin A");
            log.RememberDisplayName("PluginB", "Plugin B");
            log.RememberDisplayName("PluginA", "Plugin A2");
            log.RememberDisplayName(null, "ignored");

            log.TryAbsorb(Failure("A.B"), "PluginA", "IFramework::Update");

            Assert.Contains("「Plugin A2」", Assert.Single(this.echoes).Line);
        }

        private static InvalidOperationException Failure(string name)
            => new($"Address for {name} is null. The resolver was either uninitialized or failed to resolve address with signature AA ?? BB.");

        private UnresolvedAddressLog NewLog(int capacity) => new(capacity, (key, line) => this.echoes.Add((key, line)));

        private sealed class DerivedInvalidOperation(string message) : InvalidOperationException(message);
    }
}
