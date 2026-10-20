#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Xunit;

namespace Dalamud.Test.Game.Gui
{
    // FlyTextGui skips the second encoding of a text whose bytes did not change. Observable through Original: unchanged
    // texts pass the game's own pointers on, any changed text makes the detour pass freshly allocated copies of both.
    public sealed class FlyTextReencodeTests : IDisposable
    {
        private readonly List<nint> blocks = [];

        public delegate void Change(ref SeString text1, ref SeString text2);

        public static IEnumerable<object[]> Cases()
        {
            yield return Case("untouched", null, (ref SeString _, ref SeString _) => { });
            yield return Case("read only: TextValue, Encode, Payload.Encode", null, (ref SeString t1, ref SeString t2) =>
            {
                _ = t1.TextValue;
                _ = t2.Encode();
                foreach (var p in t1.Payloads)
                    _ = p.Encode();
            });
            yield return Case("text1 swapped for the same bytes re-parsed", null, (ref SeString t1, ref SeString _) => t1 = SeString.Parse(t1.Encode()));
            yield return Case("text1 swapped for other text", "旋風斬", (ref SeString t1, ref SeString _) => t1 = new SeString(new TextPayload("旋風斬")));
            yield return Case("text1 setter leaves it dirty", "旋風斬", (ref SeString t1, ref SeString _) => ((TextPayload)t1.Payloads[0]).Text = "旋風斬");
            yield return Case("text1 setter, then encoded again", "旋風斬", (ref SeString t1, ref SeString _) =>
            {
                ((TextPayload)t1.Payloads[0]).Text = "旋風斬";
                t1.Encode();
            });
            yield return Case("text1 setter with the same value", null, (ref SeString t1, ref SeString _) =>
            {
                var text = (TextPayload)t1.Payloads[0];
                text.Text = text.Text;
            });
            yield return Case("text1 cached bytes changed in place", "旋風斬", (ref SeString t1, ref SeString _) =>
            {
                var cached = t1.Payloads[0].Encode();
                var other = Encoding.UTF8.GetBytes("旋風斬");
                other.CopyTo(cached, 0);
            });
            yield return Case("only text2 changed", "暴風斬", (ref SeString _, ref SeString t2) => t2 = new SeString(new TextPayload("+")));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void OriginalGetsTheGamesTextsUnlessOneChanged(string name, string? newText1, Change change)
        {
            var flyText = (FlyTextGui)RuntimeHelpers.GetUninitializedObject(typeof(FlyTextGui));
            var original = DetourHarness.InstallFakeHook(flyText, "createFlyTextHook", (nint)0x77);
            flyText.FlyTextCreated += (ref FlyTextKind _, ref int _, ref int _, ref SeString text1, ref SeString text2, ref uint _, ref uint _, ref uint _, ref float _, ref bool _) =>
                change(ref text1, ref text2);
            var text1 = this.Native("暴風斬");
            var text2 = this.Native("1234");

            var ret = DetourHarness.Invoke(flyText, "CreateFlyTextDetour", nint.Zero, 1, 2, 3, text2, 4u, 5u, 6u, text1, 0f);

            Assert.Equal((nint)0x77, ret);
            var call = Assert.Single(original.Calls);
            if (newText1 == null)
            {
                Assert.True(text1 == (nint)call[8]!, name);
                Assert.True(text2 == (nint)call[4]!, name);
            }
            else
            {
                Assert.True(text1 != (nint)call[8]!, name);
                Assert.Equal(newText1, Marshal.PtrToStringUTF8((nint)call[8]!));
            }
        }

        public void Dispose()
        {
            foreach (var block in this.blocks)
                Marshal.FreeHGlobal(block);
        }

        private static object[] Case(string name, string? newText1, Change change) => [name, newText1!, change];

        private nint Native(string text)
        {
            var block = DetourHarness.Bytes([.. Encoding.UTF8.GetBytes(text), 0]);
            this.blocks.Add(block);
            return block;
        }
    }
}
