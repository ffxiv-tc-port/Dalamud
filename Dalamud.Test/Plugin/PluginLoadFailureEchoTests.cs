#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

using Dalamud.Plugin.Internal;

using Xunit;

namespace Dalamud.Test.Plugin
{
    public class PluginLoadFailureEchoTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly PluginLoadFailureEcho echo = new();
        private readonly List<string> chat = [];
        private readonly List<string> notified = [];

        [Fact]
        public void DescribePeelsConstructorWrappersAndKeepsFirstLine()
        {
            var inner = new InvalidOperationException("[AutoRetainer] 遊戲版面驗證失敗，插件不載入（避免遊戲崩潰）：X\r\n第二行");
            var wrapped = new AggregateException(
                "Failed to create AutoRetainer.AutoRetainer (ctor invocation)",
                new TargetInvocationException(new TypeInitializationException("T", inner)));

            Assert.Equal("[AutoRetainer] 遊戲版面驗證失敗，插件不載入（避免遊戲崩潰）：X", PluginLoadFailureEcho.Describe(wrapped));
        }

        [Fact]
        public void DescribeKeepsAggregateWithSeveralInners()
        {
            var aggregate = new AggregateException("outer", new Exception("a"), new Exception("b"));

            Assert.StartsWith("outer", PluginLoadFailureEcho.Describe(aggregate));
        }

        [Fact]
        public void DescribeFallsBackToTypeNameAndTruncates()
        {
            Assert.Equal(nameof(FormatException), PluginLoadFailureEcho.Describe(new FormatException(" ")));

            var longText = PluginLoadFailureEcho.Describe(new Exception(new string('字', 500)));
            Assert.Equal(PluginLoadFailureEcho.MaxDetailLength, longText.Length);
            Assert.EndsWith("…", longText);

            // A surrogate pair straddling the cut is dropped whole rather than split.
            var emoji = char.ConvertFromUtf32(0x1F600);
            var straddle = new string('a', PluginLoadFailureEcho.MaxDetailLength - 2) + emoji + "tail";
            var cut = PluginLoadFailureEcho.Describe(new Exception(straddle));
            Assert.False(char.IsHighSurrogate(cut[^2]));
            Assert.EndsWith("…", cut);
        }

        [Fact]
        public void SameVersionIsReportedOnceButANewVersionAgain()
        {
            Assert.True(this.echo.Enqueue("AutoRetainer", "AutoRetainer", "7.25.0.1", new Exception("x")));
            Assert.False(this.echo.Enqueue("AutoRetainer", "AutoRetainer", "7.25.0.1", new Exception("y")));
            Assert.True(this.echo.Enqueue("AutoRetainer", "AutoRetainer", "7.25.0.2", new Exception("z")));
        }

        [Fact]
        public void ChatWaitsForLoginDelayAndPrintsOnce()
        {
            this.echo.Enqueue("ICE", "ICE", "1", new Exception("boom"));

            Assert.Equal(0, this.Pump(loggedIn: false, T0));
            Assert.Single(this.notified);

            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(10)));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(14)));
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(15)));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(16)));

            Assert.Equal(new[] { "插件「ICE」載入失敗，目前無法使用：boom（詳情請輸入 /xllog 查看）" }, this.chat);
            Assert.Single(this.notified);
        }

        [Fact]
        public void LoggingOutRestartsTheDelay()
        {
            this.echo.Enqueue("A", "A", "1", new Exception("a"));
            this.Pump(loggedIn: true, T0);
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(6)));

            this.echo.Enqueue("B", "B", "1", new Exception("b"));
            this.Pump(loggedIn: false, T0.AddSeconds(100));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(101)));
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(106)));
        }

        [Fact]
        public void NothingIsDeliveredWhileUnloading()
        {
            this.echo.Enqueue("A", "A", "1", new Exception("a"));

            Assert.Equal(0, this.Pump(loggedIn: true, T0, unloading: true));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(60), unloading: true));
            Assert.Empty(this.chat);
            Assert.Empty(this.notified);
        }

        [Fact]
        public void NotificationIsRetriedUntilAvailable()
        {
            this.echo.Enqueue("A", "A", "1", new Exception("a"));
            var available = false;
            var attempts = 0;

            bool Notify(string name, string detail)
            {
                attempts++;
                return available;
            }

            this.echo.Pump(false, false, T0, this.chat.Add, Notify);
            this.echo.Pump(false, false, T0, this.chat.Add, Notify);
            available = true;
            this.echo.Pump(false, false, T0, this.chat.Add, Notify);
            this.echo.Pump(false, false, T0, this.chat.Add, Notify);

            Assert.Equal(3, attempts);
        }

        [Fact]
        public void ThrowingPrintDoesNotRepeat()
        {
            this.echo.Enqueue("A", "A", "1", new Exception("a"));
            this.Pump(loggedIn: true, T0);

            Assert.Throws<InvalidOperationException>(
                () => this.echo.Pump(false, true, T0.AddSeconds(6), _ => throw new InvalidOperationException(), (_, _) => true));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(7)));
        }

        [Fact]
        public void WaitingForChatAfterNotifyingAllocatesNothing()
        {
            this.echo.Enqueue("A", "A", "1", new Exception("a"));
            Action<string> print = this.chat.Add;
            Func<string, string, bool> notify = (_, _) => true;

            for (var i = 0; i < 200; i++)
                this.echo.Pump(false, i % 3 != 0, T0.AddSeconds(i % 4), print, notify);

            var before = AllocatedBytes.Start();
            for (var i = 0; i < 2000; i++)
                this.echo.Pump(false, i % 3 != 0, T0.AddSeconds(i % 4), print, notify);
            var allocated = AllocatedBytes.Since(before);

            Assert.Empty(this.chat);
            Assert.Equal(0, allocated);

            // Control: a pump that does deliver allocates, so the zero above is not a blind measurement.
            this.echo.Pump(false, true, T0, print, notify);
            before = AllocatedBytes.Start();
            Assert.Equal(1, this.echo.Pump(false, true, T0.AddSeconds(10), print, notify));
            Assert.True(AllocatedBytes.Since(before) > 0);
        }

        [Fact]
        public void AnEntryQueuedWhileWaitingIsStillNotified()
        {
            this.echo.Enqueue("A", "A", "1", new Exception("a"));
            Assert.Equal(0, this.Pump(loggedIn: false, T0));
            Assert.Equal(0, this.Pump(loggedIn: false, T0.AddSeconds(1)));

            this.echo.Enqueue("B", "B", "1", new Exception("b"));
            Assert.Equal(0, this.Pump(loggedIn: false, T0.AddSeconds(2)));

            Assert.Equal(new[] { "A", "B" }, this.notified);
        }

        private int Pump(bool loggedIn, DateTime now, bool unloading = false)
            => this.echo.Pump(unloading, loggedIn, now, this.chat.Add, (name, detail) =>
            {
                this.notified.Add(name);
                return true;
            });
    }
}
