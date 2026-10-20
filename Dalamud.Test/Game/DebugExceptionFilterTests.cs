#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Game;

using Xunit;

namespace Dalamud.Test.Game
{
    public class DebugExceptionFilterTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void TheConstructorTailCannotThrowOnAMissingSignature()
        {
            var ctor = typeof(Dalamud).GetConstructors(AnyInstance).Single();
            var called = CalledMethods(ctor);

            Assert.Contains(called, m => m?.DeclaringType == typeof(SigScanner) && m.Name == nameof(SigScanner.TryScanText));
            Assert.DoesNotContain(called, m => m?.Name == nameof(SigScanner.ScanText));
        }

        [Fact]
        public void WithoutTheFilterTheSwitchIsOffAndDoesNothing()
        {
            var dalamud = (Dalamud)RuntimeHelpers.GetUninitializedObject(typeof(Dalamud));

            Assert.False(dalamud.HasDebugExceptionFilter);
            dalamud.UseDebugExceptionHandler();
        }

        private static List<MethodBase?> CalledMethods(MethodBase method)
        {
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var found = new List<MethodBase?>();
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

            return found;
        }
    }
}
