#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Threading;

using Dalamud.Interface.Animation;
using Dalamud.Interface.Animation.EasingFunctions;
using Dalamud.Interface.Internal.Windows;

using Xunit;

namespace Dalamud.Test.Interface
{
    // The hidden title screen menu hands the easings of one frame to the next instead of building new ones.
    // A kept easing must be indistinguishable from a new one.
    public class TitleScreenMenuEasingReuseTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void TheKeptEasingIsInTheStateOfANewOne()
        {
            var used = new InOutCubic(TimeSpan.FromMilliseconds(350)) { IsInverse = true };
            used.Restart();
            Thread.Sleep(20);
            used.Update();
            Assert.True(used.IsRunning);
            Assert.NotEqual(0, used.ValueUnclamped);

            var easings = new Dictionary<Guid, InOutCubic> { [Guid.NewGuid()] = used };
            InOutCubic? spare = null;
            TitleScreenMenuWindow.ClearKeepingOne(easings, ref spare);

            Assert.Same(used, spare);
            Assert.Empty(easings);
            Assert.Equal(StateOf(new InOutCubic(TimeSpan.FromMilliseconds(350))), StateOf(spare!));
            Assert.False(spare!.IsRunning);
            Assert.False(spare.IsDone);
            Assert.Equal(0, spare.ValueClamped);
        }

        [Fact]
        public void AFinishedEasingIsAlsoPutBack()
        {
            var done = new InOutCubic(TimeSpan.FromMilliseconds(1));
            done.Restart();
            Thread.Sleep(20);
            done.Update();
            done.Stop();
            Assert.True(done.IsDone);

            var easings = new Dictionary<Guid, InOutCubic> { [Guid.NewGuid()] = done };
            InOutCubic? spare = null;
            TitleScreenMenuWindow.ClearKeepingOne(easings, ref spare);

            Assert.Equal(StateOf(new InOutCubic(TimeSpan.FromMilliseconds(1))), StateOf(spare!));
        }

        [Fact]
        public void AnOccupiedSpareIsLeftAloneAndTheDictionaryIsStillEmptied()
        {
            var kept = new InOutCubic(TimeSpan.FromMilliseconds(350));
            var other = new InOutCubic(TimeSpan.FromMilliseconds(350)) { IsInverse = true };
            other.Restart();
            var easings = new Dictionary<Guid, InOutCubic> { [Guid.NewGuid()] = other, [Guid.NewGuid()] = new(TimeSpan.FromMilliseconds(350)) };
            InOutCubic? spare = kept;

            TitleScreenMenuWindow.ClearKeepingOne(easings, ref spare);

            Assert.Same(kept, spare);
            Assert.Empty(easings);
            Assert.True(other.IsRunning);
            Assert.True(other.IsInverse);
        }

        [Fact]
        public void NothingToKeepLeavesTheSpareEmpty()
        {
            InOutCubic? spare = null;
            TitleScreenMenuWindow.ClearKeepingOne(new Dictionary<Guid, InOutCubic>(), ref spare);
            Assert.Null(spare);
        }

        [Fact]
        public void HandingOneEasingFromFrameToFrameAllocatesNothing()
        {
            var id = Guid.NewGuid();
            var easings = new Dictionary<Guid, InOutCubic>();
            InOutCubic? spare = null;

            // One hidden frame: the entry takes the spare (or a new easing), runs it, and everything is dropped again.
            void Frame()
            {
                var easing = spare ?? new InOutCubic(TimeSpan.FromMilliseconds(350));
                spare = null;
                easings.Add(id, easing);
                easing.Restart();
                easing.Update();
                TitleScreenMenuWindow.ClearKeepingOne(easings, ref spare);
            }

            for (var i = 0; i < 2000; i++)
                Frame();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                Frame();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Every field of the easing, with the stopwatch replaced by what it shows.
        private static List<string> StateOf(Easing easing)
        {
            var state = new List<string>();
            for (var type = easing.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly))
                {
                    var value = field.GetValue(easing);
                    state.Add(value switch
                    {
                        Stopwatch timer => $"{field.Name}=running:{timer.IsRunning},ticks:{timer.ElapsedTicks}",
                        Vector2 vector => $"{field.Name}={vector.X},{vector.Y}",
                        null => $"{field.Name}=null",
                        _ => $"{field.Name}={value}",
                    });
                }
            }

            Assert.True(state.Count >= 7, "the fields of Easing were not found");
            return state;
        }
    }
}
