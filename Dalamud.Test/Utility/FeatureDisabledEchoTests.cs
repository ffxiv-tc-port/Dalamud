#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Dalamud.Game.Gui;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class FeatureDisabledEchoTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly FeatureDisabledEcho echo = new();
        private readonly List<string> chat = [];
        private readonly List<string> notified = [];

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
            Assert.Empty(this.notified);
        }

        [Fact]
        public void AThrowingPrintFallsBackToANotificationOnce()
        {
            this.echo.Enqueue("A", "a");
            this.echo.Enqueue("B", "b");
            this.Pump(loggedIn: true, T0);

            Assert.Equal(
                2,
                this.echo.Pump(false, FeatureDisabledEcho.ChatState.Ready, true, T0.AddSeconds(6), line =>
                {
                    if (line == "a")
                        throw new InvalidOperationException();
                    this.chat.Add(line);
                }, this.Notify));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(7)));

            Assert.Equal(new[] { "b" }, this.chat);
            Assert.Equal(new[] { "a" }, this.notified);
        }

        [Fact]
        public void WithoutChatLinesBecomeNotificationsWithoutWaitingForLogin()
        {
            this.echo.Enqueue("A", "a");
            this.echo.Enqueue("B", "b");

            Assert.Equal(2, this.Pump(loggedIn: false, T0, chatState: FeatureDisabledEcho.ChatState.Unavailable));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(60), chatState: FeatureDisabledEcho.ChatState.Unavailable));

            Assert.Equal(new[] { "a", "b" }, this.notified);
            Assert.Empty(this.chat);
        }

        [Fact]
        public void NotificationsAreRetriedUntilAvailable()
        {
            this.echo.Enqueue("A", "a");
            var available = false;
            var attempts = 0;

            bool Notify(string line)
            {
                attempts++;
                return available;
            }

            Assert.Equal(0, this.echo.Pump(false, FeatureDisabledEcho.ChatState.Unavailable, false, T0, this.chat.Add, Notify));
            Assert.Equal(0, this.echo.Pump(false, FeatureDisabledEcho.ChatState.Unavailable, false, T0, this.chat.Add, Notify));
            available = true;
            Assert.Equal(1, this.echo.Pump(false, FeatureDisabledEcho.ChatState.Unavailable, false, T0, this.chat.Add, Notify));
            Assert.Equal(0, this.echo.Pump(false, FeatureDisabledEcho.ChatState.Unavailable, false, T0, this.chat.Add, Notify));

            Assert.Equal(3, attempts);
        }

        [Fact]
        public void ANotificationThatThrowsIsDroppedNotRepeated()
        {
            this.echo.Enqueue("A", "a");
            var attempts = 0;

            bool Throw(string line)
            {
                attempts++;
                throw new InvalidOperationException();
            }

            Assert.Equal(1, this.echo.Pump(false, FeatureDisabledEcho.ChatState.Unavailable, false, T0, this.chat.Add, Throw));
            Assert.Equal(0, this.echo.Pump(false, FeatureDisabledEcho.ChatState.Unavailable, false, T0, this.chat.Add, Throw));
            Assert.Equal(1, attempts);
        }

        [Fact]
        public void NothingIsDeliveredWhileChatIsStillLoadingOrWhileUnloading()
        {
            this.echo.Enqueue("A", "a");

            Assert.Equal(0, this.Pump(loggedIn: true, T0, chatState: FeatureDisabledEcho.ChatState.Pending));
            Assert.Equal(0, this.Pump(loggedIn: true, T0.AddSeconds(60), chatState: FeatureDisabledEcho.ChatState.Pending));
            Assert.Equal(0, this.Pump(loggedIn: false, T0, unloading: true, chatState: FeatureDisabledEcho.ChatState.Unavailable));
            Assert.Empty(this.chat);
            Assert.Empty(this.notified);
        }

        [Fact]
        public void AChatGuiWithoutItsPrintHookCannotPrint()
        {
            var chatGui = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));

            Assert.False(chatGui.IsPrintAvailable);
        }

        private int Pump(bool loggedIn, DateTime now, bool unloading = false, FeatureDisabledEcho.ChatState chatState = FeatureDisabledEcho.ChatState.Ready)
            => this.echo.Pump(unloading, chatState, loggedIn, now, this.chat.Add, this.Notify);

        private bool Notify(string line)
        {
            this.notified.Add(line);
            return true;
        }
    }
}
