#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Dalamud.Game;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Game
{
    // The per-plugin forward of Framework.Update keeps its argument array between frames. The scoped service and
    // the error handler are created without their constructors (those need the service container).
    public sealed class FrameworkUpdateForwardTests : IDisposable
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly FrameworkPluginScoped scoped;
        private readonly Action<IFramework> forward;
        private readonly PluginAllocationMeter meter = new("TestForward", PluginAllocationMeter.Update);
        private readonly IFramework first = DispatchProxy.Create<IFramework, FakeFramework>();
        private readonly IFramework second = DispatchProxy.Create<IFramework, FakeFramework>();
        private readonly List<string> seen = [];

        public FrameworkUpdateForwardTests()
        {
            var manifest = new LocalPluginManifest { InternalName = "TestForward", Name = "TestForward" };
            var plugin = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlugin));
            typeof(LocalPlugin).GetField("manifest", Any)!.SetValue(plugin, manifest);

            var errorHandler = (PluginErrorHandler)RuntimeHelpers.GetUninitializedObject(typeof(PluginErrorHandler));
            SetField(errorHandler, "plugin", plugin);
            SetField(errorHandler, "invokerCache", new Dictionary<Type, Delegate>());

            this.scoped = (FrameworkPluginScoped)RuntimeHelpers.GetUninitializedObject(typeof(FrameworkPluginScoped));
            SetField(this.scoped, "pluginErrorHandler", errorHandler);
            SetField(this.scoped, "updateAllocations", this.meter);
            this.forward = typeof(FrameworkPluginScoped).GetMethod("OnUpdateForward", Any)!
                                                        .CreateDelegate<Action<IFramework>>(this.scoped);
            Assert.False(Framework.StatsEnabled);
        }

        public void Dispose() => this.meter.Retire();

        [Fact]
        public void EverySubscriberGetsTheArgumentOfThatCall()
        {
            this.scoped.Update += f => this.seen.Add("h1:" + this.Name(f));
            this.scoped.Update += f => this.seen.Add("h2:" + this.Name(f));

            foreach (var framework in new[] { this.first, this.first, this.second, this.first, this.second, this.second })
                this.forward(framework);

            Assert.Equal(
                [
                    "h1:first", "h2:first", "h1:first", "h2:first", "h1:second", "h2:second",
                    "h1:first", "h2:first", "h1:second", "h2:second", "h1:second", "h2:second",
                ],
                this.seen);
        }

        [Fact]
        public void AForwardStartedFromInsideASubscriberKeepsBothArguments()
        {
            var nested = false;
            this.scoped.Update += f =>
            {
                this.seen.Add("h1:" + this.Name(f));
                if (nested)
                    return;

                nested = true;
                this.forward(this.second);
            };
            this.scoped.Update += f => this.seen.Add("h2:" + this.Name(f));

            this.forward(this.first);

            Assert.Equal(["h1:first", "h1:second", "h2:second", "h2:first"], this.seen);
        }

        [Fact]
        public void AThrowingSubscriberIsCaughtAndTheNextFrameStillRuns()
        {
            var fail = true;
            this.scoped.Update += f =>
            {
                this.seen.Add("h1:" + this.Name(f));
                if (fail)
                    throw new InvalidOperationException("from the subscriber");
            };
            this.scoped.Update += f => this.seen.Add("h2:" + this.Name(f));

            this.forward(this.first);
            fail = false;
            this.forward(this.second);

            Assert.Equal(["h1:first", "h1:second", "h2:second"], this.seen);
        }

        [Fact]
        public void NoSubscriberIsNotAnError()
        {
            this.forward(this.first);
            this.forward(this.second);
        }

        [Fact]
        public void TheForwardIsTimedForTheFrameAndAThrowStillSkipsTheRestOfThePlugin()
        {
            var attribution = new FrameworkUpdateAttribution();
            SetField(this.scoped, "updateAttribution", attribution);
            this.scoped.Update += f =>
            {
                this.seen.Add("h1:" + this.Name(f));
                Thread.Sleep(20);
                throw new InvalidOperationException("from the subscriber");
            };
            this.scoped.Update += f => this.seen.Add("h2:" + this.Name(f));

            attribution.Begin();
            this.forward(this.first);
            var described = attribution.Describe(0);
            attribution.End();

            Assert.Equal(["h1:first"], this.seen);
            Assert.StartsWith("TestForward ", described);
        }

        [Fact]
        public void ATimedSteadyFrameAllocatesNothing()
        {
            var attribution = new FrameworkUpdateAttribution();
            SetField(this.scoped, "updateAttribution", attribution);
            this.scoped.Update += _ => { };
            attribution.Begin();
            this.forward(this.first);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                attribution.Begin();
                this.forward(this.first);
                attribution.End();
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void ASteadyFrameAllocatesNothingAndThePluginIsChargedOnlyForItsOwnBytes()
        {
            var allocate = false;
            this.scoped.Update += _ =>
            {
                if (allocate)
                    GC.KeepAlive(new byte[1000]);
            };

            // The first call compiles the invoker.
            this.forward(this.first);
            this.forward(this.first);

            var charged = this.meter.Bytes;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                this.forward(this.first);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(charged, this.meter.Bytes);

            allocate = true;
            for (var i = 0; i < 100; i++)
                this.forward(this.first);
            Assert.Equal(100 * 1024, this.meter.Bytes - charged);
        }

        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, Any)!.SetValue(target, value);

        private string Name(IFramework framework)
            => ReferenceEquals(framework, this.first) ? "first" : ReferenceEquals(framework, this.second) ? "second" : "other";

        public class FakeFramework : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
        }
    }
}
