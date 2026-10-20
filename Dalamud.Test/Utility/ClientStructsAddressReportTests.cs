#nullable enable

using System;
using System.Linq;

using Dalamud.Utility.Internal;

using InteropGenerator.Runtime;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class ClientStructsAddressReportTests
    {
        [Fact]
        public void ListsOnlyUnresolvedAddressesByShortName()
        {
            var addresses = new[]
            {
                Make("FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance", 0),
                Make("FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentHUD.OpenSystemMenu", 0x140001000),
                Make("FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseAction", 0),
            };

            var unresolved = ClientStructsAddressReport.CollectUnresolved(addresses);

            Assert.Equal(new[] { "ActionManager.UseAction", "Telepo.Instance" }, unresolved);
            Assert.Equal(
                "2 of 3 ClientStructs addresses are unresolved: ActionManager.UseAction, Telepo.Instance",
                ClientStructsAddressReport.FormatLogLine(unresolved, addresses.Length));
        }

        [Fact]
        public void AllResolvedPrintsZero()
        {
            var addresses = new[]
            {
                Make("FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance", 0x140002000),
                Make("FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseAction", 0x140003000),
            };

            var unresolved = ClientStructsAddressReport.CollectUnresolved(addresses);

            Assert.Empty(unresolved);
            Assert.Equal("0 of 2 ClientStructs addresses are unresolved", ClientStructsAddressReport.FormatLogLine(unresolved, addresses.Length));
        }

        [Theory]
        [InlineData("FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance", "Telepo.Instance")]
        [InlineData("FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase+Nested.Member", "Nested.Member")]
        [InlineData("Type.Member", "Type.Member")]
        [InlineData("NoDot", "NoDot")]
        public void ShortNameMatchesTheClientStructsExceptionForm(string full, string expected)
        {
            Assert.Equal(expected, ClientStructsAddressReport.ShortName(full));
        }

        [Fact]
        public void TheRealRegistrationIsUnresolvedOutsideTheGame()
        {
            // The test process never runs the resolver. Not asserted for every address: another test class
            // temporarily points PlayerState.Instance at test memory while tests run in parallel.
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            var all = Resolver.GetInstance.Addresses.ToArray();

            var unresolved = ClientStructsAddressReport.CollectUnresolved(all);

            Assert.True(all.Length > 1000, $"only {all.Length} addresses registered");
            Assert.True(unresolved.Count >= all.Length - 1, $"{unresolved.Count} of {all.Length} listed");
            Assert.Contains("Telepo.Instance", unresolved);
            Assert.Contains("AgentHUD.OpenSystemMenu", unresolved);
        }

        [Fact]
        public void ChatLineCarriesTheCount()
        {
            Assert.Contains(" 7 個遊戲位址", ClientStructsAddressReport.FormatChatLine(7));
        }

        private static Address Make(string name, long value)
            => new(name, "E8 ?? ?? ?? ??", Array.Empty<ushort>(), new ulong[] { 0xE8 }, new ulong[] { 0xFF }, (nint)value);
    }
}
