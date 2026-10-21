#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;

using Xunit;

namespace Dalamud.Test.Game.Addon
{
    public class AddonLifecycleHooksTests
    {
        private const string SetupSig = "4C 8B 88 ?? ?? ?? ?? 66 44 39 BB";
        private const string FinalizeSig = "E8 ?? ?? ?? ?? 48 83 EF 01 75 D5";
        private const string DrawSig = "FF 90 ?? ?? ?? ?? 83 EB 01 79 C4 48 81 EF ?? ?? ?? ?? 48 83 ED 01";
        private const string UpdateSig = "FF 90 ?? ?? ?? ?? 40 88 AF ?? ?? ?? ?? 45 33 D2";
        private const string RequestedUpdateSig = "FF 90 A0 01 00 00 48 8B 5C 24 30";

        private static readonly AddonLifecycleHooks All = new(true, true, true, true, true, true);

        // TC 7.25 results relative to the module base, as cached by the live game.
        private static readonly Dictionary<string, nint> Tc725 = new()
        {
            [SetupSig] = 0x643460,
            [FinalizeSig] = 0x64FEF0,
            [DrawSig] = 0x678BB1,
            [UpdateSig] = 0x64A628,
            [RequestedUpdateSig] = 0x64AD80,
        };

        [Fact]
        public void EverythingUsableIsHooked()
        {
            Assert.Equal(All, AddonLifecycleHooks.Decide(All));
            Assert.Empty(All.MissingChatNames());
        }

        [Theory]
        [InlineData("Setup", "開啟")]
        [InlineData("Draw", "繪製")]
        [InlineData("Update", "更新")]
        [InlineData("Refresh", "重新整理")]
        [InlineData("RequestedUpdate", "資料更新")]
        public void AMissingHookOtherThanFinalizeOnlyLeavesItselfOut(string missing, string chatName)
        {
            var usable = Without(missing);

            var hooks = AddonLifecycleHooks.Decide(usable);

            Assert.Equal(usable, hooks);
            Assert.Equal([chatName], hooks.MissingChatNames());
        }

        [Fact]
        public void WithoutFinalizeNothingIsHooked()
        {
            var hooks = AddonLifecycleHooks.Decide(Without("Finalize"));

            Assert.Equal(default(AddonLifecycleHooks), hooks);
            Assert.Equal(["開啟", "關閉", "繪製", "更新", "重新整理", "資料更新"], hooks.MissingChatNames());
        }

        [Fact]
        public void ReceiveEventIsListedWheneverItCannotBeHooked()
        {
            // ReceiveEvent hooks are made in the OnSetup detour, so without Setup they never exist.
            Assert.Empty(All.DisabledChatNames(unitBaseVtableFound: true));
            Assert.Equal(["操作"], All.DisabledChatNames(unitBaseVtableFound: false));
            Assert.Equal(["開啟", "操作"], Without("Setup").DisabledChatNames(unitBaseVtableFound: true));
            Assert.Equal(["開啟", "操作"], Without("Setup").DisabledChatNames(unitBaseVtableFound: false));
            Assert.Equal(["繪製"], Without("Draw").DisabledChatNames(unitBaseVtableFound: true));
            Assert.Equal(
                ["開啟", "關閉", "繪製", "更新", "重新整理", "資料更新", "操作"],
                AddonLifecycleHooks.Decide(Without("Finalize")).DisabledChatNames(unitBaseVtableFound: true));
        }

        [Fact]
        public void AllSignaturesFoundOnTc725()
        {
            var resolver = new AddonLifecycleAddressResolver();
            resolver.Setup(new FakeScanner(Tc725));

            Assert.Empty(resolver.NotFound);
            Assert.Equal((nint)0x643460, resolver.AddonSetup);
            Assert.Equal((nint)0x64FEF0, resolver.AddonFinalize);
            Assert.Equal((nint)0x678BB1, resolver.AddonDraw);
            Assert.Equal((nint)0x64A628, resolver.AddonUpdate);
            Assert.Equal((nint)0x64AD80, resolver.AddonOnRequestedUpdate);
        }

        [Theory]
        [InlineData(SetupSig, "AddonSetup")]
        [InlineData(FinalizeSig, "AddonFinalize")]
        [InlineData(DrawSig, "AddonDraw")]
        [InlineData(UpdateSig, "AddonUpdate")]
        [InlineData(RequestedUpdateSig, "AddonOnRequestedUpdate")]
        public void AMissingSignatureOnlyZeroesItsOwnAddress(string missing, string name)
        {
            var found = Tc725.Where(x => x.Key != missing).ToDictionary(x => x.Key, x => x.Value);
            var resolver = new AddonLifecycleAddressResolver();

            resolver.Setup(new FakeScanner(found));

            Assert.Equal([name], resolver.NotFound);
            var addresses = new Dictionary<string, nint>
            {
                ["AddonSetup"] = resolver.AddonSetup,
                ["AddonFinalize"] = resolver.AddonFinalize,
                ["AddonDraw"] = resolver.AddonDraw,
                ["AddonUpdate"] = resolver.AddonUpdate,
                ["AddonOnRequestedUpdate"] = resolver.AddonOnRequestedUpdate,
            };
            foreach (var (key, value) in addresses)
                Assert.Equal(key == name, value == 0);
        }

        [Fact]
        public void NoSignatureFoundDoesNotThrow()
        {
            var resolver = new AddonLifecycleAddressResolver();

            resolver.Setup(new FakeScanner(new Dictionary<string, nint>()));

            Assert.Equal(["AddonSetup", "AddonFinalize", "AddonDraw", "AddonUpdate", "AddonOnRequestedUpdate"], resolver.NotFound);
        }

        private static AddonLifecycleHooks Without(string hook) => hook switch
        {
            "Setup" => All with { Setup = false },
            "Finalize" => All with { Finalize = false },
            "Draw" => All with { Draw = false },
            "Update" => All with { Update = false },
            "Refresh" => All with { Refresh = false },
            "RequestedUpdate" => All with { RequestedUpdate = false },
            _ => throw new ArgumentOutOfRangeException(nameof(hook)),
        };

        // Only what BaseAddressResolver.Setup and the resolver use; anything else would mean the resolver changed.
        private sealed class FakeScanner(Dictionary<string, nint> found) : ISigScanner
        {
            public bool IsCopy => false;

            public bool Is32BitProcess => false;

            public IntPtr SearchBase => throw new NotSupportedException();

            public IntPtr TextSectionBase => throw new NotSupportedException();

            public long TextSectionOffset => throw new NotSupportedException();

            public int TextSectionSize => throw new NotSupportedException();

            public IntPtr DataSectionBase => throw new NotSupportedException();

            public long DataSectionOffset => throw new NotSupportedException();

            public int DataSectionSize => throw new NotSupportedException();

            public IntPtr RDataSectionBase => throw new NotSupportedException();

            public long RDataSectionOffset => throw new NotSupportedException();

            public int RDataSectionSize => throw new NotSupportedException();

            public ProcessModule Module => throw new NotSupportedException();

            public nint GetStaticAddressFromSig(string signature, int offset = 0) => throw new NotSupportedException();

            public bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0) => throw new NotSupportedException();

            public nint ScanData(string signature) => throw new NotSupportedException();

            public bool TryScanData(string signature, out nint result) => throw new NotSupportedException();

            public nint ScanModule(string signature) => throw new NotSupportedException();

            public bool TryScanModule(string signature, out nint result) => throw new NotSupportedException();

            public nint ResolveRelativeAddress(nint nextInstAddr, int relOffset) => throw new NotSupportedException();

            public nint ScanText(string signature) => found.TryGetValue(signature, out var address) ? address : throw new KeyNotFoundException(signature);

            public bool TryScanText(string signature, out nint result) => found.TryGetValue(signature, out result);

            public nint[] ScanAllText(string signature) => throw new NotSupportedException();

            public IEnumerable<nint> ScanAllText(string signature, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
