#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.Player;
using Dalamud.IoC.Internal;

using FFXIVClientStructs.FFXIV.Client.Game;

using Xunit;

using CSPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using CSStatus = FFXIVClientStructs.FFXIV.Client.Game.Status;
using Status = Dalamud.Game.ClientState.Statuses.Status;

namespace Dalamud.Test.Game.ClientState
{
    // Enumerating a StatusList skips empty slots before a wrapper is made for them. Two zeroed blocks stand in for
    // the game's StatusManager and PlayerState; every offset is read from the ClientStructs definitions. Statuses are
    // compared as text: printing a wrapper for a failure message reads properties that wait for services forever.
    public sealed class StatusListEnumerationTests : IDisposable
    {
        private const int Slots = 60;

        private static readonly int CountOffset = Offset(typeof(StatusManager), nameof(StatusManager.NumValidStatuses));
        private static readonly int IdOffset = Offset(typeof(CSStatus), nameof(CSStatus.StatusId));
        private static readonly int ParamOffset = Offset(typeof(CSStatus), nameof(CSStatus.Param));
        private static readonly int TimeOffset = Offset(typeof(CSStatus), nameof(CSStatus.RemainingTime));
        private static readonly int SourceOffset = Offset(typeof(CSStatus), nameof(CSStatus.SourceObject));
        private static readonly int LoadedOffset = Offset(typeof(CSPlayerState), nameof(CSPlayerState.IsLoaded));

        private readonly nint manager = Zeroed(Unsafe.SizeOf<StatusManager>());
        private readonly nint playerState = Zeroed(Unsafe.SizeOf<CSPlayerState>());
        private readonly nint previousPlayerState;
        private readonly nint[] slot = new nint[Slots];
        private readonly StatusList list;

        public StatusListEnumerationTests()
        {
            // Service<T> waits for the container; the status wrappers ask the PlayerState service whether a player is loaded.
            Service<ServiceContainer>.Provide(new ServiceContainer());
            Service<PlayerState>.Provide((PlayerState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerState)));
            this.previousPlayerState = CSPlayerState.Addresses.Instance.Value;
            CSPlayerState.Addresses.Instance.Value = this.playerState;

            this.list = new StatusList(this.manager);
            this.Set(Slots, loaded: true);
            for (var i = 0; i < Slots; i++)
                this.slot[i] = this.list.GetStatusAddress(i);
            Assert.NotEqual(nint.Zero, this.slot[0]);
            Assert.Equal(Unsafe.SizeOf<CSStatus>(), (int)(this.slot[1] - this.slot[0]));
        }

        public void Dispose()
        {
            CSPlayerState.Addresses.Instance.Value = this.previousPlayerState;
            Marshal.FreeHGlobal(this.manager);
            Marshal.FreeHGlobal(this.playerState);
        }

        [Fact]
        public void RandomListsEnumerateExactlyAsBefore()
        {
            var random = new Random(20261011);
            int[] lengths = [0, 1, 2, 29, 30, 31, 59, 60];
            double[] emptyShare = [0, 0.3, 0.7, 0.95, 1];
            var yielded = 0;
            for (var trial = 0; trial < 4000; trial++)
            {
                var length = trial % 3 == 0 ? random.Next(Slots + 1) : lengths[random.Next(lengths.Length)];
                var share = emptyShare[random.Next(emptyShare.Length)];

                // All 60 slots are filled, also the ones beyond the length, which must not show up.
                for (var i = 0; i < Slots; i++)
                    this.Fill(i, random.NextDouble() < share ? (ushort)0 : (ushort)random.Next(1, ushort.MaxValue + 1), random);

                this.Set(length, loaded: trial % 7 != 0);

                var expected = Describe(this.EnumerateAsBefore());
                Assert.Equal(expected, Describe(this.list));
                Assert.Equal(expected, Describe(NonGeneric(this.list)));
                yielded += expected.Count;
            }

            Assert.True(yielded > 20000, $"only {yielded} statuses were compared");
        }

        [Fact]
        public void NothingIsEnumeratedWhileNoPlayerIsLoaded()
        {
            for (var i = 0; i < Slots; i++)
                this.Fill(i, (ushort)(i + 1), new Random(i));

            this.Set(Slots, loaded: false);
            Assert.Empty(Describe(this.list));
            Assert.True(this.list[0] is null);

            this.Set(Slots, loaded: true);
            Assert.Equal(Slots, Describe(this.list).Count);
        }

        [Fact]
        public void ALengthBeyondTheArrayFailsWhereItDidBefore()
        {
            for (var i = 0; i < Slots; i++)
                this.Fill(i, (ushort)(i % 2), new Random(i));
            this.Set(Slots + 1, loaded: true);

            var before = new List<string>();
            var beforeError = Record.Exception(() => before.AddRange(Describe(this.EnumerateAsBefore())));
            var now = new List<string>();
            var nowError = Record.Exception(() =>
            {
                foreach (var status in this.list)
                    now.Add(Describe(status));
            });

            Assert.NotNull(beforeError);
            Assert.IsType(beforeError.GetType(), nowError);
            Assert.Equal(Slots / 2, now.Count);
        }

        [Fact]
        public void EmptySlotsCostNoWrapper()
        {
            this.Set(Slots, loaded: true);
            for (var i = 0; i < Slots; i++)
                this.Fill(i, 0, new Random(i));

            var none = this.MeasureEnumeration();
            this.Fill(7, 50, new Random(7));
            this.Fill(41, 51, new Random(41));
            var two = this.MeasureEnumeration();

            // One wrapper is an object header and an address: 24 bytes. Sixty of them used to be made either way.
            Assert.Equal(2 * 24, two - none);
            Assert.InRange(none, 1, 128);
        }

        private static int Offset(Type type, string field)
            => type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                   .GetCustomAttribute<FieldOffsetAttribute>()!.Value;

        private static nint Zeroed(int size)
        {
            var block = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, block, size);
            return block;
        }

        private static IEnumerable<Status> NonGeneric(IEnumerable list)
        {
            foreach (var status in list)
                yield return (Status)status;
        }

        private static string Describe(Status status)
            => $"{status.Address:X}|{status.StatusId}|{status.Param}|{BitConverter.SingleToInt32Bits(status.RemainingTime):X8}|{status.SourceId:X8}";

        private static List<string> Describe(IEnumerable<Status> statuses)
        {
            var result = new List<string>();
            foreach (var status in statuses)
                result.Add(Describe(status));
            return result;
        }

        // The enumerator as it was: make the indexer's wrapper for every slot, then drop the empty ones.
        private IEnumerable<Status> EnumerateAsBefore()
        {
            for (var i = 0; i < this.list.Length; i++)
            {
                var status = this.list[i];

                if (status == null || status.StatusId == 0)
                    continue;

                yield return status;
            }
        }

        private long MeasureEnumeration()
        {
            long Once()
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                foreach (var status in this.list)
                    GC.KeepAlive(status);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Once();
            return Once();
        }

        private void Set(int length, bool loaded)
        {
            Marshal.WriteByte(this.manager + CountOffset, (byte)length);
            Marshal.WriteByte(this.playerState + LoadedOffset, loaded ? (byte)1 : (byte)0);
        }

        private void Fill(int index, ushort id, Random random)
        {
            Marshal.WriteInt16(this.slot[index] + IdOffset, unchecked((short)id));
            Marshal.WriteInt16(this.slot[index] + ParamOffset, unchecked((short)random.Next(ushort.MaxValue + 1)));
            Marshal.WriteInt32(this.slot[index] + TimeOffset, random.Next());
            Marshal.WriteInt64(this.slot[index] + SourceOffset, random.NextInt64());
        }
    }
}
