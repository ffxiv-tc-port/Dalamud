#nullable enable

using System;
using System.Collections.Generic;

using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class FeatureDisabledEchoTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly FeatureDisabledEcho echo = new();
        private readonly List<string> chat = [];

        [Fact]
        public void SameKeyIsQueuedOnce()
        {
            Assert.True(this.echo.Enqueue("ContextMenu.Layout", "a"));
            Assert.False(this.echo.Enqueue("ContextMenu.Layout", "b"));
            Assert.True(this.echo.Enqueue("Other", "c"));
        }

        [Fact]
        public void ChatWaitsForLoginDelayAndPrintsOnce()
        {
            this.echo.Enqueue("K", "line");

            Assert.Equal(0, this.Pump(loggedIn: false, T0));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(10)));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(14)));
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(15)));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(16)));

            Assert.Equal(new[] { "line" }, this.chat);
        }

        [Fact]
        public void LoggingOutRestartsTheDelay()
        {
            this.echo.Enqueue("A", "a");
            this.Pump(loggedIn: true, T0);
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(6)));

            this.echo.Enqueue("B", "b");
            this.Pump(loggedIn: false, T0.AddSeconds(100));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(101)));
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(106)));
        }

        [Fact]
        public void ALaterReportAfterReloggingWaitsForTheNewLogin()
        {
            this.echo.Enqueue("A", "a");
            this.Pump(loggedIn: true, T0);
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(6)));

            // Nothing pending while the player relogs, so the pump never saw the logout.
            this.echo.Enqueue("B", "b");
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(200)));
            Assert.Equal(1, this.Pump(loggedIn: true, T0.AddSeconds(205)));
        }

        [Fact]
        public void NothingIsPrintedWhileUnloading()
        {
            this.echo.Enqueue("A", "a");

            Assert.Equal(0, this.Pump(loggedIn: true, T0, unloading: true));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(60), unloading: true));
            Assert.Empty(this.chat);
        }

        [Fact]
        public void ThrowingPrintDoesNotRepeat()
        {
            this.echo.Enqueue("A", "a");
            this.Pump(loggedIn: true, T0);

            Assert.Throws<InvalidOperationException>(
                () => this.echo.Pump(false, true, T0.AddSeconds(6), _ => throw new InvalidOperationException()));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(7)));
        }

        private int Pump(bool loggedIn, DateTime now, bool unloading = false)
            => this.echo.Pump(unloading, loggedIn, now, this.chat.Add);
    }
}
