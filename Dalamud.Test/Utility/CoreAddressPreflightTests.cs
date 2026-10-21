#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using Dalamud.Game.ClientState;
using Dalamud.Utility.Internal;

using Xunit;

using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using Preflight = Dalamud.Utility.Internal.CoreAddressPreflight;
using Utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;

namespace Dalamud.Test.Utility
{
    // The test process never runs the ClientStructs resolver, so every CS address is 0 here, exactly as on a client
    // whose signatures stopped matching.
    public class CoreAddressPreflightTests
    {
        private static readonly Preflight.ModuleLayout Layout = new(
            new(0x1000, 0x1000),
            new(0x3000, 0x1000),
            new(0x5000, 0x1000));

        [Fact]
        public void EveryCoreAddressIsMissingWhileClientStructsIsUnresolved()
        {
            Assert.Equal(nint.Zero, CSFramework.Addresses.StaticVirtualTable.Value);

            var addresses = Preflight.Gather(Layout, _ => 0);
            var missing = Preflight.FindMissing(addresses, Layout).Select(m => m.Name);

            Assert.Equal(
                new[]
                {
                    "Framework.VirtualTable", "Framework.Tick", "Framework.Destroy", "Conditions.Instance",
                    "GameObjectManager.Instance", "ClientState.HandleZoneInitPacket", "Framework.Instance",
                    "Framework.GetNetworkModuleProxy", "Device.Instance", "Utf8String.Ctor_FromSequence",
                },
                missing);
        }

        [Fact]
        public void TheStringHookTargetIsCheckedWhereGameGuiChecksIt()
        {
            // GameGui accepts it only inside .text; the preflight must not be looser.
            var entry = Assert.Single(Preflight.Gather(Layout, _ => 0), a => a.Name == "Utf8String.Ctor_FromSequence");
            Assert.Equal(Preflight.Section.Text, entry.Expected);
            Assert.Equal(Utf8String.Addresses.Ctor_FromSequence.Value, entry.Value);
            Assert.Contains("・Utf8String.Ctor_FromSequence（建立遊戲字串）\n", Preflight.FormatDialog("x", [entry]));
        }

        [Fact]
        public void TheZoneInitHandlerIsScannedWithTheResolversSignature()
        {
            var asked = new List<string>();
            var addresses = Preflight.Gather(Layout, signature =>
            {
                asked.Add(signature);
                return 0x1800;
            });

            Assert.Equal(new[] { ClientStateAddressResolver.HandleZoneInitPacketSignature }, asked);
            Assert.DoesNotContain(Preflight.FindMissing(addresses, Layout), m => m.Name == "ClientState.HandleZoneInitPacket");
        }

        [Fact]
        public void AnAddressOutsideItsSectionIsMissing()
        {
            var addresses = new Preflight.CoreAddress[]
            {
                new("CodeInText", string.Empty, 0x1800, Preflight.Section.Text),
                new("CodeInData", string.Empty, 0x5800, Preflight.Section.Text),
                new("VtableInRData", string.Empty, 0x3800, Preflight.Section.RData),
                new("VtableInText", string.Empty, 0x1800, Preflight.Section.RData),
                new("GlobalInData", string.Empty, 0x5FFF, Preflight.Section.Data),
                new("GlobalPastData", string.Empty, 0x6000, Preflight.Section.Data),
                new("Zero", string.Empty, 0, Preflight.Section.Data),
            };

            Assert.Equal(
                new[] { "CodeInData", "VtableInText", "GlobalPastData", "Zero" },
                Preflight.FindMissing(addresses, Layout).Select(m => m.Name));
        }

        [Fact]
        public void SectionBoundsAreHalfOpenAndCoverTheWholeRead()
        {
            var range = new Preflight.SectionRange(0x1000, 0x100);

            Assert.True(range.Contains(0x1000, 8));
            Assert.True(range.Contains(0x10F8, 8));
            Assert.False(range.Contains(0x10F9, 8));
            Assert.False(range.Contains(0x1100, 1));
            Assert.False(range.Contains(0x0FFF, 1));
            Assert.False(new Preflight.SectionRange(0x1000, 0).Contains(0x1000, 1));
        }

        [Fact]
        public void VtableSlotsAreReadOnlyInsideRData()
        {
            var vtable = Marshal.AllocHGlobal(8 * IntPtr.Size);
            try
            {
                for (var i = 0; i < 8; i++)
                    Marshal.WriteIntPtr(vtable, i * IntPtr.Size, 0x7000 + i);

                var covering = Layout with { RData = new(vtable, 8 * IntPtr.Size) };
                Assert.Equal((nint)0x7004, Preflight.ReadSlot(vtable, 4, covering));
                Assert.Equal((nint)0x7002, Preflight.ReadSlot(vtable, 2, covering));

                // Slot 4 crosses the end of this shorter range, so it is not read.
                var short4 = Layout with { RData = new(vtable, 4 * IntPtr.Size) };
                Assert.Equal(nint.Zero, Preflight.ReadSlot(vtable, 4, short4));

                // Addresses outside .rdata are never dereferenced; reading this one would fault.
                Assert.Equal(nint.Zero, Preflight.ReadSlot(0x10, 4, covering));
                Assert.Equal(nint.Zero, Preflight.ReadSlot(0, 4, Layout with { RData = new(0, 0x100) }));
            }
            finally
            {
                Marshal.FreeHGlobal(vtable);
            }
        }

        [Fact]
        public void TheStopMessageNamesEachMissingAddressAndWhatItIsFor()
        {
            var text = Preflight.FormatDialog(
                "2026.11.17.0000.0000",
                [new("Framework.Tick", "每幀更新", 0, Preflight.Section.Text), new("Device.Instance", "繪圖裝置", 0, Preflight.Section.Data)]);

            Assert.Contains("2026.11.17.0000.0000", text);
            Assert.Contains("・Framework.Tick（每幀更新）\n・Device.Instance（繪圖裝置）\n", text);
            Assert.Contains("遊戲照常運作", text);
            Assert.Contains("[PREFLIGHT]", text);
        }

        [Fact]
        public void ServiceFailureCausesArePeeledDeduplicatedAndCapped()
        {
            var root = new InvalidOperationException("Address for X is null\nsecond line");
            var failure = new AggregateException(
                new AggregateException(new TargetInvocationException(root)),
                new TypeInitializationException("T", new KeyNotFoundException("sig")),
                new TargetInvocationException(new InvalidOperationException("Address for X is null")));

            Assert.Equal(
                new[] { "InvalidOperationException: Address for X is null", "KeyNotFoundException: sig" },
                ServiceFailureNotice.Causes(failure));

            var many = new AggregateException(Enumerable.Range(0, 20).Select(i => new Exception($"e{i}")));
            Assert.Equal(ServiceFailureNotice.MaxCauses, ServiceFailureNotice.Causes(many).Count);
        }

        [Fact]
        public void TheServiceFailureMessageIsChineseFirstAndKeepsTheEnglishLine()
        {
            var text = ServiceFailureNotice.Format(new AggregateException(new InvalidOperationException("boom")));

            Assert.StartsWith("台服 Dalamud 有服務載入失敗", text);
            Assert.Contains("・InvalidOperationException: boom\n", text);
            Assert.Contains("Dalamud failed to load all necessary services.", text);
            Assert.DoesNotContain("失敗原因", ServiceFailureNotice.Format(null));
        }
    }
}
