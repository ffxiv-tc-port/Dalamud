#nullable enable

using System;
using System.Threading.Tasks;

using Dalamud.Plugin.Internal.Types;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // An empty main repository URL is the default: reloading it must not touch the network or services,
    // must finish, and must not be reported as a failed repository.
    public class PluginRepositoryEmptyUrlTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task EmptyUrlSucceedsWithEmptyListing(string url)
        {
            var repo = new PluginRepository(null!, url, true);

            // Task.Run: a regression blocks synchronously inside Service<HappyHttpClient>.Get().
            var reload = Task.Run(repo.ReloadPluginMasterAsync);
            var finished = await Task.WhenAny(reload, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(reload, finished);
            await reload;
            Assert.Equal(PluginRepositoryState.Success, repo.State);
            Assert.NotNull(repo.PluginMaster);
            Assert.Empty(repo.PluginMaster!);
        }
    }
}
