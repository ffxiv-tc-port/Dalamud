#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Dalamud.Data;
using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Data
{
    public class DataManagerFailureTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void TheConstructorNeverEndsTheGameProcess()
        {
            var ctor = typeof(DataManager).GetConstructors(AnyInstance).Single();
            var called = CalledMethods(ctor);

            Assert.DoesNotContain(called, m => m?.DeclaringType == typeof(Util) && m.Name == nameof(Util.Fatal));
            Assert.DoesNotContain(called, m => m?.DeclaringType == typeof(Environment) && m.Name == nameof(Environment.Exit));

            // The scan does see the constructor's calls, and does see a Util.Fatal call where there is one.
            Assert.Contains(called, m => m?.DeclaringType == typeof(RsvResolver) && m.Name == nameof(RsvResolver.Dispose));
            var failureHandler = typeof(Dalamud).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)
                                                .Single(m => m.Name.Contains("HandleServiceInitFailure"));
            Assert.Contains(CalledMethods(failureHandler), m => m?.DeclaringType == typeof(Util) && m.Name == nameof(Util.Fatal));
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
