#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

using Dalamud.Common;
using Dalamud.Common.Game;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class GameVersionGateTests : IDisposable
    {
        private const string Current = "2026.09.14.0000.0000";
        private const string Older = "2026.07.22.0000.0000";

        private readonly string dir = Path.Combine(Path.GetTempPath(), "d5w_versiongate_" + Guid.NewGuid().ToString("N"));
        private readonly List<string> asked = [];

        public GameVersionGateTests()
        {
            Directory.CreateDirectory(this.dir);
        }

        public void Dispose()
        {
            Directory.Delete(this.dir, true);
        }

        [Fact]
        public void ParseSkipsCommentsBlankLinesAndSurroundingWhitespace()
        {
            var (versions, invalid) = GameVersionGate.Parse(
                "# header\r\n\r\n   \r\n  " + Current + "  \r\n\t" + Older + "\t# 7.20\r\n#2026.01.01.0000.0000\r\n");

            Assert.Equal(new[] { Current, Older }, versions);
            Assert.Empty(invalid);
        }

        [Fact]
        public void ParseAcceptsLfOnlyAndALeadingByteOrderMark()
        {
            var (versions, invalid) = GameVersionGate.Parse("\uFEFF" + Current + "\n" + Older);

            Assert.Equal(new[] { Current, Older }, versions);
            Assert.Empty(invalid);
        }

        [Fact]
        public void ParseOnlyAcceptsTheExactFfxivgameVerShape()
        {
            var (versions, invalid) = GameVersionGate.Parse("2026.9.14.0.0\r\nany\r\n2026.09.14\r\n" + Current + "x\r\n");

            Assert.Empty(versions);
            Assert.Equal(new[] { "2026.9.14.0.0", "any", "2026.09.14", Current + "x" }, invalid);
        }

        [Fact]
        public void TheCurrentVersionInTheListIsVerified()
        {
            var e = GameVersionGate.Evaluate(GameVersion.Parse(Current), () => Older + "\r\n" + Current + "\r\n");

            Assert.Equal(GameVersionGate.Verdict.Verified, e.Verdict);
            Assert.Equal(Current, e.Current);
        }

        [Fact]
        public void AVersionNotInTheListIsNotVerified()
        {
            var e = GameVersionGate.Evaluate(GameVersion.Parse("2026.11.17.0000.0000"), () => Current);

            Assert.Equal(GameVersionGate.Verdict.NotVerified, e.Verdict);
            Assert.Equal(new[] { Current }, e.Verified);
        }

        [Fact]
        public void AMissingListIsReportedAsMissing()
        {
            Assert.Equal(
                GameVersionGate.Verdict.ListMissing,
                GameVersionGate.Evaluate(GameVersion.Parse(Current), () => throw new FileNotFoundException()).Verdict);
            Assert.Equal(
                GameVersionGate.Verdict.ListMissing,
                GameVersionGate.Evaluate(GameVersion.Parse(Current), () => throw new DirectoryNotFoundException()).Verdict);
        }

        [Fact]
        public void AnUnreadableListIsReportedAsUnreadable()
        {
            Assert.Equal(
                GameVersionGate.Verdict.ListUnreadable,
                GameVersionGate.Evaluate(GameVersion.Parse(Current), () => throw new IOException("locked")).Verdict);
            Assert.Equal(
                GameVersionGate.Verdict.ListUnreadable,
                GameVersionGate.Evaluate(GameVersion.Parse(Current), () => throw new UnauthorizedAccessException()).Verdict);
        }

        [Fact]
        public void AListWithoutAnyVersionIsEmpty()
        {
            var e = GameVersionGate.Evaluate(GameVersion.Parse(Current), () => "# only a comment\r\n2026.9.14\r\n");

            Assert.Equal(GameVersionGate.Verdict.ListEmpty, e.Verdict);
            Assert.Equal(new[] { "2026.9.14" }, e.InvalidLines);
        }

        [Fact]
        public void AnUnknownOrIncompleteGameVersionDoesNotReadTheList()
        {
            var reads = 0;
            string Read()
            {
                reads++;
                return Current;
            }

            Assert.Equal(GameVersionGate.Verdict.VersionUnknown, GameVersionGate.Evaluate(null, Read).Verdict);
            Assert.Equal(GameVersionGate.Verdict.VersionUnknown, GameVersionGate.Evaluate(GameVersion.Any, Read).Verdict);
            Assert.Equal(GameVersionGate.Verdict.VersionUnknown, GameVersionGate.Evaluate(GameVersion.Parse("2026.09.14"), Read).Verdict);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void OnlyANotVerifiedVersionAsks()
        {
            foreach (var verdict in new[]
                     {
                         GameVersionGate.Verdict.Verified,
                         GameVersionGate.Verdict.VersionUnknown,
                         GameVersionGate.Verdict.ListMissing,
                         GameVersionGate.Verdict.ListUnreadable,
                         GameVersionGate.Verdict.ListEmpty,
                     })
            {
                var e = new GameVersionGate.Evaluation(verdict, Current, [Current], [], null);
                Assert.True(GameVersionGate.Apply(e, "list", this.Ask(true)));
            }

            Assert.Empty(this.asked);
        }

        [Fact]
        public void ANotVerifiedVersionFollowsTheAnswer()
        {
            var e = new GameVersionGate.Evaluation(GameVersionGate.Verdict.NotVerified, "2026.11.17.0000.0000", [Current], [], null);

            Assert.False(GameVersionGate.Apply(e, "list", this.Ask(false)));
            Assert.True(GameVersionGate.Apply(e, "list", this.Ask(true)));
            Assert.Equal(2, this.asked.Count);
            Assert.Contains("2026.11.17.0000.0000", this.asked[0]);
            Assert.Contains(Current, this.asked[0]);
        }

        [Fact]
        public void AQuestionThatThrowsCountsAsNo()
        {
            var e = new GameVersionGate.Evaluation(GameVersionGate.Verdict.NotVerified, "2026.11.17.0000.0000", [Current], [], null);

            Assert.False(GameVersionGate.Apply(e, "list", (_, _) => throw new InvalidOperationException("no desktop")));
        }

        [Fact]
        public void ThePromptNamesTheNewestThreeVerifiedVersions()
        {
            var text = GameVersionGate.FormatPrompt(
                "2026.11.17.0000.0000",
                ["2026.01.01.0000.0000", Older, Current, "2026.03.03.0000.0000", Current]);

            Assert.Contains("2026.11.17.0000.0000", text);
            Assert.Contains($"{Current}、{Older}、2026.03.03.0000.0000 等 4 個", text);
            Assert.DoesNotContain("2026.01.01.0000.0000", text);
        }

        [Fact]
        public void CheckReadsTheListFromTheWorkingDirectory()
        {
            File.WriteAllText(Path.Combine(this.dir, GameVersionGate.FileName), Current + "\r\n");

            Assert.True(GameVersionGate.Check(this.Info(Current), this.Ask(false)));
            Assert.False(GameVersionGate.Check(this.Info("2026.11.17.0000.0000"), this.Ask(false)));
            Assert.True(GameVersionGate.Check(this.Info("2026.11.17.0000.0000"), this.Ask(true)));
            Assert.Equal(2, this.asked.Count);
        }

        [Fact]
        public void CheckStartsWhenTheListOrTheVersionIsMissing()
        {
            Assert.True(GameVersionGate.Check(this.Info(Current), this.Ask(false)));
            Assert.True(GameVersionGate.Check(new DalamudStartInfo { WorkingDirectory = this.dir }, this.Ask(false)));
            Assert.True(GameVersionGate.Check(new DalamudStartInfo(), this.Ask(false)));
            Assert.Empty(this.asked);
        }

        [Fact]
        public void TheShippedListIsCopiedNextToDalamudAndVerifiesAVersion()
        {
            var shipped = Path.Combine(Path.GetDirectoryName(typeof(GameVersionGate).Assembly.Location)!, GameVersionGate.FileName);
            var (versions, invalid) = GameVersionGate.Parse(File.ReadAllText(shipped));

            Assert.NotEmpty(versions);
            Assert.Empty(invalid);
        }

        private DalamudStartInfo Info(string version)
            => new() { WorkingDirectory = this.dir, GameVersion = GameVersion.Parse(version) };

        private Func<string, string, bool> Ask(bool answer)
            => (text, caption) =>
            {
                Assert.Equal(GameVersionGate.PromptCaption, caption);
                this.asked.Add(text);
                return answer;
            };
    }
}
