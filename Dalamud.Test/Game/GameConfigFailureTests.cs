#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Dalamud.Game.Config;

using Xunit;

namespace Dalamud.Test.Game
{
    // Before the fix a failed initialization left the section tasks pending forever, so these reads would hang; each is
    // therefore run on the pool and given a few seconds before the test fails instead of hanging.
    public class GameConfigFailureTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task SectionsNotReadYetThrowInsteadOfBlocking()
        {
            var config = NewConfig();
            var cause = new InvalidOperationException("Framework instance was null");

            config.FailInitialization(cause);

            foreach (var read in new Func<GameConfigSection>[] { () => config.System, () => config.UiConfig, () => config.UiControl })
            {
                var thrown = await Assert.ThrowsAsync<AggregateException>(() => Finishes(Task.Run(read)));
                var unavailable = Assert.IsType<InvalidOperationException>(thrown.InnerException);
                Assert.Same(cause, unavailable.InnerException);
                Assert.Contains("GameConfig", unavailable.Message);
            }

            Assert.True(config.InitializationTask.IsFaulted);
        }

        [Fact]
        public async Task ASectionReadBeforeTheFailureStaysUsable()
        {
            var config = NewConfig();
            var system = (GameConfigSection)RuntimeHelpers.GetUninitializedObject(typeof(GameConfigSection));
            Tcs(config, "tcsSystem").SetResult(system);

            config.FailInitialization(new InvalidOperationException("hook address missing"));

            Assert.Same(system, await Finishes(Task.Run(() => config.System)));
            await Assert.ThrowsAsync<AggregateException>(() => Finishes(Task.Run(() => config.UiConfig)));
        }

        [Fact]
        public async Task PluginStyleTryGetThrowsInsteadOfBlocking()
        {
            var config = NewConfig();
            config.FailInitialization(new InvalidOperationException("x"));

            await Assert.ThrowsAsync<AggregateException>(
                () => Finishes(Task.Run(() => config.TryGet((SystemConfigOption)0, out uint _))));
        }

        private static GameConfig NewConfig()
        {
            var config = (GameConfig)RuntimeHelpers.GetUninitializedObject(typeof(GameConfig));
            typeof(GameConfig).GetField("tcsInitialization", AnyInstance)!
                              .SetValue(config, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            foreach (var name in new[] { "tcsSystem", "tcsUiConfig", "tcsUiControl" })
            {
                typeof(GameConfig).GetField(name, AnyInstance)!
                                  .SetValue(config, new TaskCompletionSource<GameConfigSection>(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return config;
        }

        private static TaskCompletionSource<GameConfigSection> Tcs(GameConfig config, string name)
            => (TaskCompletionSource<GameConfigSection>)typeof(GameConfig).GetField(name, AnyInstance)!.GetValue(config)!;

        private static async Task<T> Finishes<T>(Task<T> task)
        {
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(Patience)));
            return await task;
        }
    }
}
