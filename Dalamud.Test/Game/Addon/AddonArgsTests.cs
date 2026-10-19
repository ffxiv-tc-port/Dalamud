using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.NativeWrapper;

using FFXIVClientStructs.FFXIV.Component.GUI;

using Xunit;

namespace Dalamud.Test.Game.Addon
{
    // A zeroed block the size of AtkUnitBase stands in for an addon; only its name buffer is ever read.
    public sealed class AddonArgsTests : IDisposable
    {
        private static readonly int NameOffset =
            typeof(AtkUnitBase).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)!
                               .GetCustomAttribute<FieldOffsetAttribute>()!.Value;

        private readonly nint addon = Marshal.AllocHGlobal(Unsafe.SizeOf<AtkUnitBase>());

        public AddonArgsTests()
        {
            Marshal.Copy(new byte[Unsafe.SizeOf<AtkUnitBase>()], 0, this.addon, Unsafe.SizeOf<AtkUnitBase>());
        }

        public void Dispose() => Marshal.FreeHGlobal(this.addon);

        [Fact]
        public void IsAddonMatchesTheNativeNameExactly()
        {
            var args = this.ArgsFor("Talk");

            Assert.True(args.IsAddon("Talk"));
            Assert.False(args.IsAddon("talk"));
            Assert.False(args.IsAddon("Tal"));
            Assert.False(args.IsAddon("TalkX"));
            Assert.False(args.IsAddon(string.Empty));
            Assert.False(args.IsAddon(new string('A', 33)));
        }

        [Fact]
        public void IsAddonIsFalseForANullOrNamelessAddon()
        {
            var args = this.ArgsFor(string.Empty);
            Assert.False(args.IsAddon("Talk"));

            args.Clear();
            Assert.False(args.IsAddon("Talk"));
        }

        [Fact]
        public void IsAddonAcceptsAFullBufferName()
        {
            var name = new string('B', 32);
            var args = this.ArgsFor(name);

            Assert.True(args.IsAddon(name));
            Assert.False(args.IsAddon(name[..31]));
        }

        [Fact]
        public void AddonNameIsInvalidForANullOrNamelessAddon()
        {
            var args = this.ArgsFor(string.Empty);
            Assert.Equal(AddonArgs.InvalidAddon, args.AddonName);

            args.Clear();
            Assert.Equal(AddonArgs.InvalidAddon, args.AddonName);
        }

        // The cache keeps the first name read, but a name that has since gone empty still reads as invalid.
        [Fact]
        public void AddonNameKeepsItsCacheSemantics()
        {
            var args = this.ArgsFor("Talk");
            Assert.Equal("Talk", args.AddonName);

            this.WriteName("SelectYesno");
            Assert.Equal("Talk", args.AddonName);
            Assert.True(args.IsAddon("SelectYesno"));

            this.WriteName(string.Empty);
            Assert.Equal(AddonArgs.InvalidAddon, args.AddonName);

            this.WriteName("ContextMenu");
            args.Clear();
            args.Addon = new AtkUnitBasePtr(this.addon);
            Assert.Equal("ContextMenu", args.AddonName);
        }

        [Fact]
        public void MatchingAndRepeatedNameReadsAllocateNothing()
        {
            var args = this.ArgsFor("_ParameterWidget");
            Assert.Equal("_ParameterWidget", args.AddonName);
            args.IsAddon("Talk");

            var before = GC.GetAllocatedBytesForCurrentThread();
            var hits = 0;
            for (var i = 0; i < 1000; i++)
            {
                if (args.IsAddon("_ParameterWidget"))
                    hits++;
                if (args.IsAddon("Talk"))
                    hits++;
                if (args.AddonName.Length == 16)
                    hits++;
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(2000, hits);
        }

        private AddonDrawArgs ArgsFor(string name)
        {
            this.WriteName(name);
#pragma warning disable CS0618
            var args = new AddonDrawArgs();
#pragma warning restore CS0618
            args.Clear();
            args.Addon = new AtkUnitBasePtr(this.addon);
            return args;
        }

        private void WriteName(string name)
        {
            var buffer = new byte[32];
            Encoding.UTF8.GetBytes(name, buffer);
            Marshal.Copy(buffer, 0, this.addon + NameOffset, buffer.Length);
        }
    }
}
