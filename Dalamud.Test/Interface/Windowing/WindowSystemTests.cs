using System;
using System.Collections.Generic;

using Dalamud.Interface.Windowing;
using Dalamud.IoC.Internal;

using Xunit;

namespace Dalamud.Test.Interface.Windowing
{
    // Closed windows never reach ImGui: DrawInternal only calls PreOpenCheck on them, which is the hook used here.
    public class WindowSystemTests
    {
        private readonly List<string> drawn = new();

        public WindowSystemTests()
        {
            // WindowSystem.Draw looks up optional services; without a container that lookup blocks forever.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void WindowRemovedDuringDrawIsStillDrawnThatFrame()
        {
            var ws = new WindowSystem();
            var a = this.Add(ws, "A");
            this.Add(ws, "B");
            var c = this.Add(ws, "C");
            a.Once(() => ws.RemoveWindow(c));

            Assert.Equal(new[] { "A", "B", "C" }, this.Frame(ws));
            Assert.Equal(new[] { "A", "B" }, Names(ws));
            Assert.Equal(new[] { "A", "B" }, this.Frame(ws));
        }

        [Fact]
        public void WindowAddedDuringDrawIsDrawnFromTheNextFrame()
        {
            var ws = new WindowSystem();
            this.Add(ws, "A");
            var b = this.Add(ws, "B");
            b.Once(() => ws.AddWindow(new ProbeWindow("D", this.drawn)));

            Assert.Equal(new[] { "A", "B" }, this.Frame(ws));
            Assert.Equal(new[] { "A", "B", "D" }, Names(ws));
            Assert.Equal(new[] { "A", "B", "D" }, this.Frame(ws));
        }

        [Fact]
        public void RemovingAllWindowsDuringDrawFinishesTheFrame()
        {
            var ws = new WindowSystem();
            var a = this.Add(ws, "A");
            this.Add(ws, "B");
            this.Add(ws, "C");
            a.Once(ws.RemoveAllWindows);

            Assert.Equal(new[] { "A", "B", "C" }, this.Frame(ws));
            Assert.Empty(ws.Windows);
            Assert.Empty(this.Frame(ws));
        }

        [Fact]
        public void WindowRemovingItselfDuringDrawKeepsTheRestOfTheFrame()
        {
            var ws = new WindowSystem();
            var a = this.Add(ws, "A");
            this.Add(ws, "B");
            a.Once(() =>
            {
                ws.RemoveWindow(a);
                ws.AddWindow(new ProbeWindow("E", this.drawn));
            });

            Assert.Equal(new[] { "A", "B" }, this.Frame(ws));
            Assert.Equal(new[] { "B", "E" }, Names(ws));
            Assert.Equal(new[] { "B", "E" }, this.Frame(ws));
        }

        [Fact]
        public void NestedDrawSeesTheListAsItIsThen()
        {
            var ws = new WindowSystem();
            var a = this.Add(ws, "A");
            var b = this.Add(ws, "B");
            a.Once(() =>
            {
                ws.RemoveWindow(b);
                ws.Draw();
            });

            // Outer frame: A, then the nested frame (A only), then B from the outer snapshot.
            Assert.Equal(new[] { "A", "A", "B" }, this.Frame(ws));
            Assert.Equal(new[] { "A" }, this.Frame(ws));
        }

        private static List<string> Names(WindowSystem ws)
        {
            var names = new List<string>();
            foreach (var w in ws.Windows)
                names.Add(w.WindowName);
            return names;
        }

        private ProbeWindow Add(WindowSystem ws, string name)
        {
            var window = new ProbeWindow(name, this.drawn);
            ws.AddWindow(window);
            return window;
        }

        private List<string> Frame(WindowSystem ws)
        {
            this.drawn.Clear();
            ws.Draw();
            return new List<string>(this.drawn);
        }

        private sealed class ProbeWindow : Window
        {
            private readonly List<string> drawn;
            private Action once;

            public ProbeWindow(string name, List<string> drawn)
                : base(name)
            {
                this.drawn = drawn;
            }

            public void Once(Action action) => this.once = action;

            public override void PreOpenCheck()
            {
                this.drawn.Add(this.WindowName);
                var action = this.once;
                this.once = null;
                action?.Invoke();
            }

            public override void Draw()
            {
            }
        }
    }
}
