#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Xunit;

namespace Dalamud.Test.Game.ClientState
{
    // The test process never runs the CS resolver, so Telepo is unresolved here exactly as on a client whose Telepo
    // signature stopped matching. The ObjectTable stand-in is never initialized: touching it would throw, which also
    // proves the list gives up on the missing Telepo before it asks for the local player.
    public class AetheryteListUnresolvedTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void ConstructionNoLongerTouchesTelepo()
        {
            // The constructor itself logs through Util.DescribeAddress, which needs Dalamud.Boot.dll and so cannot
            // run here; its IL is checked instead.
            var telepoInstance = typeof(Telepo).GetMethod(nameof(Telepo.Instance), BindingFlags.Public | BindingFlags.Static)!;
            var ctor = typeof(AetheryteList).GetConstructor(AnyInstance, Type.EmptyTypes)!;

            var called = CalledMethods(ctor);
            Assert.Contains(typeof(Util).GetMethod(nameof(Util.DescribeAddress), [typeof(nint)]), called);
            Assert.DoesNotContain(telepoInstance, called);
            Assert.DoesNotContain(typeof(AetheryteList).GetFields(AnyInstance), f => f.FieldType.IsPointer);

            // The same scan does see the call where it moved to.
            var getTelepo = typeof(AetheryteList).GetMethod("GetTelepo", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Contains(telepoInstance, CalledMethods(getTelepo));
        }

        [Fact]
        public void TheListIsEmptyWhileTelepoIsUnresolved()
        {
            Assert.Equal(nint.Zero, Telepo.Addresses.Instance.Value);

            var list = (AetheryteList)RuntimeHelpers.GetUninitializedObject(typeof(AetheryteList));
            typeof(AetheryteList).GetField("objectTable", AnyInstance)!
                                 .SetValue(list, RuntimeHelpers.GetUninitializedObject(typeof(ObjectTable)));

            Assert.Equal(0, list.Length);
            Assert.Null(list[0]);
            Assert.Empty(list.ToList());

            // Asked again after the one-time warning: still empty, still no exception.
            Assert.Empty(list);
        }

        private static MethodBase?[] CalledMethods(MethodBase method)
        {
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var found = new System.Collections.Generic.List<MethodBase?>();
            for (var i = 0; i + 4 < il.Length; i++)
            {
                // call / callvirt / newobj followed by a method token; a misread token simply fails to resolve.
                if (il[i] is not (0x28 or 0x6F or 0x73))
                    continue;

                try
                {
                    found.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)));
                }
                catch (ArgumentException)
                {
                }
            }

            return found.ToArray();
        }
    }
}
