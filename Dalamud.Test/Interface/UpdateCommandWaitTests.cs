#nullable enable

using System;
using System.Threading.Tasks;

using Dalamud.Interface.Internal;

using Xunit;

namespace Dalamud.Test.Interface
{
    // /xlupdate waits for plugin loading and repository loading with this helper instead of giving up at once.
    public class UpdateCommandWaitTests
    {
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

        [Fact]
        public async Task AlreadyReadyReturnsAtOnceWithOneRead()
        {
            var reads = 0;
            var ok = await DalamudCommands.WaitUntilAsync(() => { reads++; return true; }, DateTime.UtcNow.AddMinutes(1), Poll);

            Assert.True(ok);
            Assert.Equal(1, reads);
        }

        [Fact]
        public async Task BecomingReadyBeforeTheDeadlineReturnsTrue()
        {
            var reads = 0;
            var ok = await DalamudCommands.WaitUntilAsync(() => ++reads >= 4, DateTime.UtcNow.AddMinutes(1), Poll);

            Assert.True(ok);
            Assert.Equal(4, reads);
        }

        [Fact]
        public async Task NeverReadyGivesUpAfterTheDeadline()
        {
            var start = DateTime.UtcNow;
            var ok = await DalamudCommands.WaitUntilAsync(() => false, start.AddMilliseconds(100), Poll);

            Assert.False(ok);
            Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public async Task ReadyWinsOverAPassedDeadline()
        {
            var ok = await DalamudCommands.WaitUntilAsync(() => true, DateTime.UtcNow.AddMinutes(-1), Poll);

            Assert.True(ok);
        }
    }
}
