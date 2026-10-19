using System.Runtime.CompilerServices;

using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Xunit;

namespace Dalamud.Test.Game.Gui.Dtr
{
    public class DtrBarEntryTests
    {
        [Fact]
        public void FirstWriteAlwaysHappens()
        {
            var entry = NewEntry();
            entry.Text = Text("12:34");

            var encoded = entry.EncodeIfChanged(entry.Text);

            Assert.NotNull(encoded);
            Assert.Equal(entry.Text.EncodeWithNullTerminator(), encoded);
        }

        [Fact]
        public void SameContentInANewInstanceIsNotWrittenAgain()
        {
            var entry = NewEntry();
            entry.Text = Text("Subs 3/4");
            Assert.True(Render(entry));

            entry.Text = Text("Subs 3/4");

            Assert.True(entry.Dirty);
            Assert.False(Render(entry));
        }

        [Fact]
        public void SameInstanceSetAgainIsNotWrittenAgain()
        {
            var entry = NewEntry();
            var text = Text("Subs 3/4");
            entry.Text = text;
            Assert.True(Render(entry));

            entry.Text = text;

            Assert.False(Render(entry));
        }

        [Fact]
        public void DifferentTextIsWritten()
        {
            var entry = NewEntry();
            entry.Text = Text("Subs 3/4");
            Assert.True(Render(entry));

            entry.Text = Text("Subs 4/4");

            Assert.True(Render(entry));
            Assert.Equal(Text("Subs 4/4").EncodeWithNullTerminator(), entry.AppliedText);
        }

        [Fact]
        public void ShorterAndLongerTextIsWritten()
        {
            var entry = NewEntry();
            entry.Text = Text("ab");
            Assert.True(Render(entry));

            entry.Text = Text("a");
            Assert.True(Render(entry));

            entry.Text = Text("ab");
            Assert.True(Render(entry));
        }

        [Fact]
        public void SameInstanceChangedInPlaceIsWritten()
        {
            var entry = NewEntry();
            var payload = new TextPayload("x");
            var text = new SeString(payload);
            entry.Text = text;
            Assert.True(Render(entry));

            payload.Text = "y";
            entry.Text = text;

            Assert.True(Render(entry));
            Assert.Equal(Text("y").EncodeWithNullTerminator(), entry.AppliedText);
        }

        [Fact]
        public void SamePlainTextWithDifferentPayloadsIsWritten()
        {
            var entry = NewEntry();
            entry.Text = Text("HP");
            Assert.True(Render(entry));

            entry.Text = new SeStringBuilder().AddUiForeground(500).AddText("HP").AddUiForegroundOff().Build();

            Assert.True(Render(entry));
        }

        [Fact]
        public void SameBytesSplitIntoOtherPayloadsIsNotWrittenAgain()
        {
            var entry = NewEntry();
            entry.Text = Text("ab");
            Assert.True(Render(entry));

            entry.Text = new SeString(new TextPayload("a"), new TextPayload("b"));

            Assert.False(Render(entry));
        }

        [Fact]
        public void NullAndBackToTheShownTextIsNotWrittenAgain()
        {
            var entry = NewEntry();
            entry.Text = Text("12:34");
            Assert.True(Render(entry));

            // DtrBar leaves the node as it is while the text is null.
            entry.Text = null;
            Assert.True(entry.Dirty);

            entry.Text = Text("12:34");
            Assert.False(Render(entry));

            entry.Text = null;
            entry.Text = Text("12:35");
            Assert.True(Render(entry));
        }

        [Fact]
        public void ResetAppliedTextForcesAWrite()
        {
            var entry = NewEntry();
            entry.Text = Text("12:34");
            Assert.True(Render(entry));

            entry.AppliedText = null;

            Assert.True(Render(entry));
        }

        [Fact]
        public void EmptyTextMatchesOnlyTheTerminator()
        {
            var empty = new SeString();

            Assert.True(DtrBarEntry.EncodesTo(empty, new byte[] { 0 }));
            Assert.False(DtrBarEntry.EncodesTo(empty, new byte[] { 0x41, 0 }));
            Assert.False(DtrBarEntry.EncodesTo(Text("A"), new byte[] { 0 }));
        }

        [Fact]
        public void AppliedBytesWithoutTerminatorNeverMatch()
        {
            Assert.False(DtrBarEntry.EncodesTo(Text("A"), new byte[] { 0x41 }));
            Assert.False(DtrBarEntry.EncodesTo(new SeString(), System.Array.Empty<byte>()));
        }

        private static DtrBarEntry NewEntry() =>
            (DtrBarEntry)RuntimeHelpers.GetUninitializedObject(typeof(DtrBarEntry));

        private static SeString Text(string text) => new SeStringBuilder().AddText(text).Build();

        // What DtrBar.Update does with a dirty, visible entry; returns whether it wrote to the node.
        private static bool Render(DtrBarEntry entry)
        {
            var encoded = entry.EncodeIfChanged(entry.Text);
            entry.Dirty = false;
            if (encoded is null)
                return false;

            entry.AppliedText = encoded;
            return true;
        }
    }
}
