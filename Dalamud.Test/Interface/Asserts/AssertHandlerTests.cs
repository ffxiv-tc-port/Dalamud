using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Interface.Internal.Asserts;
using Dalamud.Plugin.Internal.Loader;
using Dalamud.Plugin.Internal.Types;

using Xunit;

namespace Dalamud.Test.Interface.Asserts
{
    public class AssertHandlerTests : IDisposable
    {
        private const string ExprText = "(g.CurrentWindowStack.Size > 1) && \"Calling End() too many times!\"";

        // Calls the registered callback through its native entry point, the way cimgui does.
        private static readonly Action<nint, nint, nint, int> CallNative = BuildNativeCall();

        private readonly AssertSiteCounter counter = new(TimeSpan.FromHours(1));
        private readonly AssertHandler handler;
        private readonly nint callback;
        private readonly nint expr = Marshal.StringToHGlobalAnsi(ExprText);
        private readonly nint file = Marshal.StringToHGlobalAnsi("C:\\build\\lib\\cimgui\\imgui\\imgui.cpp");

        public AssertHandlerTests()
        {
            this.handler = new AssertHandler(this.counter);
            var managed = (Delegate)typeof(AssertHandler)
                                    .GetField("callback", BindingFlags.Instance | BindingFlags.NonPublic)!
                                    .GetValue(this.handler)!;
            this.callback = Marshal.GetFunctionPointerForDelegate(managed);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(this.expr);
            Marshal.FreeHGlobal(this.file);
            GC.KeepAlive(this.handler);
        }

        [Fact]
        public void ASilentAssertAllocatesNothingOnceItsSiteIsKnown()
        {
            for (var i = 0; i < 100; i++)
                CallNative(this.callback, this.expr, this.file, 6789);

            var start = AllocatedBytes.Start();
            for (var i = 0; i < 1000; i++)
                CallNative(this.callback, this.expr, this.file, 6789);

            Assert.Equal(0, AllocatedBytes.Since(start));
            Assert.Equal(1100, this.counter.CloseWindow(Stopwatch.GetTimestamp())!.Total);
        }

        [Fact]
        public void EveryFailureIsCountedAtItsOwnSite()
        {
            for (var i = 0; i < 37; i++)
                CallNative(this.callback, this.expr, this.file, 6789);
            for (var i = 0; i < 5; i++)
                CallNative(this.callback, this.expr, this.file, 1234);

            var report = this.counter.CloseWindow(Stopwatch.GetTimestamp())!;

            Assert.Equal(42, report.Total);
            Assert.Equal(2, report.SiteCount);
            Assert.Equal(0, report.Untracked);
            Assert.Equal(new[] { 6789, 1234 }, new[] { report.Sites[0].Line, report.Sites[1].Line });
            Assert.Equal(37, report.Sites[0].Count);
            Assert.Equal(5, report.Sites[1].Count);
            Assert.Equal(ExprText, report.Sites[0].Expr);
            Assert.Equal("imgui.cpp", report.Sites[0].File);
        }

        [Fact]
        public void AWindowWithoutFailuresReportsNothingAndSessionCountsCarryOver()
        {
            for (var i = 0; i < 3; i++)
                CallNative(this.callback, this.expr, this.file, 6789);
            Assert.NotNull(this.counter.CloseWindow(Stopwatch.GetTimestamp()));

            Assert.Null(this.counter.CloseWindow(Stopwatch.GetTimestamp()));

            CallNative(this.callback, this.expr, this.file, 6789);
            var report = this.counter.CloseWindow(Stopwatch.GetTimestamp())!;
            Assert.Equal(1, report.Sites[0].Count);
            Assert.Equal(4, report.Sites[0].SessionCount);
        }

        [Fact]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TheFirstFailureInEachWindowSaysWhereItCameFrom()
        {
            CallNative(this.callback, this.expr, this.file, 6789);
            CallNative(this.callback, this.expr, this.file, 6789);
            var context = this.counter.CloseWindow(Stopwatch.GetTimestamp())!.Sites[0].Context;

            Assert.NotNull(context);
            Assert.Contains("no plugin on the stack", context);
            Assert.Contains(nameof(this.TheFirstFailureInEachWindowSaysWhereItCameFrom), context);
            Assert.DoesNotContain(nameof(AssertHandler) + ".", context);

            CallNative(this.callback, this.expr, this.file, 6789);
            Assert.NotNull(this.counter.CloseWindow(Stopwatch.GetTimestamp())!.Sites[0].Context);
        }

        [Fact]
        public void AFailureDuringAPluginsUiBuilderDrawNamesThatPlugin()
        {
            AssertHandler.DrawingPlugin = "TestDrawingPlugin";
            try
            {
                CallNative(this.callback, this.expr, this.file, 6789);
            }
            finally
            {
                AssertHandler.DrawingPlugin = null;
            }

            CallNative(this.callback, this.expr, this.file, 1234);
            var report = this.counter.CloseWindow(Stopwatch.GetTimestamp())!;

            Assert.Contains("in the UiBuilder draw of TestDrawingPlugin", report.Sites.Find(x => x.Line == 6789).Context);
            Assert.Contains("in no plugin's UiBuilder draw", report.Sites.Find(x => x.Line == 1234).Context);
        }

        [Fact]
        public void AFailureFromPluginCodeNamesThePluginAndDoesNotKeepItLoaded()
        {
            var weak = this.RaiseFromALoadedPluginAndUnloadIt();

            Assert.False(IsStillAlive(weak), "The assert counter kept the unloaded plugin context alive.");
            var context = this.counter.CloseWindow(Stopwatch.GetTimestamp())!.Sites[0].Context;
            Assert.Contains("plugin Dalamud.Test on the stack", context);
            Assert.Contains(nameof(AssertProbePluginSide) + "." + nameof(AssertProbePluginSide.Raise), context);
        }

        [Fact]
        public void SitesPastTheLimitAreSummedUntilQuietOnesMakeRoom()
        {
            for (var line = 0; line < AssertSiteCounter.MaxSites + 3; line++)
                CallNative(this.callback, this.expr, this.file, line);

            var full = this.counter.CloseWindow(Stopwatch.GetTimestamp())!;
            Assert.Equal(AssertSiteCounter.MaxSites, full.Total);
            Assert.Equal(3, full.Untracked);

            CallNative(this.callback, this.expr, this.file, 0);
            this.counter.CloseWindow(Stopwatch.GetTimestamp());

            CallNative(this.callback, this.expr, this.file, 100000);
            var after = this.counter.CloseWindow(Stopwatch.GetTimestamp())!;
            Assert.Equal(0, after.Untracked);
            Assert.Equal(100000, after.Sites[0].Line);
        }

        [Fact]
        public void AnUnreadableExpressionIsStillCounted()
        {
            CallNative(this.callback, 0, this.file, 6789);

            var report = this.counter.CloseWindow(Stopwatch.GetTimestamp())!;
            Assert.Equal(1, report.Total);
            Assert.Equal("(unreadable)", report.Sites[0].Expr);
        }

        [Fact]
        public void TicksCountFramesAndTheWindowEndsOnTime()
        {
            var counter = new AssertSiteCounter(TimeSpan.FromSeconds(10));
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < 10; i++)
                counter.Tick(start + i);

            counter.Count(this.expr, this.file, 6789);
            Assert.Equal(10, counter.CloseWindow(start + 100)!.Frames);

            counter.Count(this.expr, this.file, 6789);
            counter.Tick(start + 100 + (10 * Stopwatch.Frequency));
            Assert.Null(counter.CloseWindow(start + 101 + (10 * Stopwatch.Frequency)));
        }

        // Loads a copy of this assembly through the production loader, as if it were a plugin, and fails an assert
        // from code in that copy.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference RaiseFromALoadedPluginAndUnloadIt()
        {
            var loader = PluginLoader.CreateFromAssemblyFile(
                typeof(AssertHandlerTests).Assembly.Location,
                config =>
                {
                    LocalPlugin.SetupLoaderConfig(config);
                    config.BeforeUnload = null;
                });
            var side = loader.LoadDefaultAssembly().GetType(typeof(AssertProbePluginSide).FullName!)!;
            Assert.True(side.IsCollectible);
            side.GetMethod(nameof(AssertProbePluginSide.Raise))!.Invoke(null, [CallNative, this.callback, this.expr, this.file, 6789]);

            var weak = new WeakReference(loader.LoadContext);
            loader.Dispose();
            return weak;
        }

        private static bool IsStillAlive(WeakReference weak)
        {
            for (var i = 0; weak.IsAlive && i < 30; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return weak.IsAlive;
        }

        private static Action<nint, nint, nint, int> BuildNativeCall()
        {
            var method = new DynamicMethod(
                "CallAssertCallback",
                typeof(void),
                [typeof(nint), typeof(nint), typeof(nint), typeof(int)],
                typeof(AssertHandlerTests).Module);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldarg_0);
            il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, typeof(void), [typeof(nint), typeof(nint), typeof(int)]);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate<Action<nint, nint, nint, int>>();
        }
    }

    // Runs inside the collectible copy of this assembly.
    public static class AssertProbePluginSide
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Raise(Action<nint, nint, nint, int> call, nint callback, nint expr, nint file, int line) =>
            call(callback, expr, file, line);
    }
}
