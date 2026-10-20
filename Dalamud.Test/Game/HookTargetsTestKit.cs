#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

using Dalamud.Game;
using Dalamud.Utility.Internal;

using Xunit;

using Preflight = Dalamud.Utility.Internal.CoreAddressPreflight;

namespace Dalamud.Test.Game
{
    /// <summary>
    /// Shared pieces for the tests of services whose hooks go through <see cref="HookTargets"/>. The test process never
    /// runs the ClientStructs resolver, so every CS address and vtable is 0 here, as on a client whose signatures stopped
    /// matching.
    /// </summary>
    internal static class HookTargetsTestKit
    {
        /// <summary>A made-up layout; nothing in it is ever read.</summary>
        public static readonly Preflight.ModuleLayout Layout = new(new(0x1000, 0x1000), new(0x3000, 0x1000), new(0x5000, 0x1000));

        public const nint InText = 0x1800;

        public const nint InData = 0x5800;

        public static HookTargets NoSections(string service) => new(service, null);

        public static HookTargets Synthetic(string service) => new(service, Layout);

        public static void AssertConstructorUsesHookTargets(Type service, string findMethod)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var ctor = service.GetConstructors(any).Single(c => c.GetCustomAttributes(typeof(ServiceManager.ServiceConstructor), false).Length > 0);
            var called = CalledMethods(ctor);

            Assert.Contains(called, m => m?.DeclaringType == service && m.Name == findMethod);
            Assert.Contains(called, m => m?.DeclaringType == typeof(HookTargets) && m.Name == nameof(HookTargets.ReportDisabled));
            Assert.DoesNotContain(called, m => m?.Name == "get_StaticVirtualTablePointer");
        }

        public static MethodBase?[] CalledMethods(MethodBase method)
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

            return found.ToArray();
        }

        /// <summary>Only what the resolvers use; the section properties throw, which leaves HookTargets.For without sections.</summary>
        public sealed class FakeScanner(Dictionary<string, nint> found) : ISigScanner
        {
            public bool IsCopy => false;

            public bool Is32BitProcess => false;

            public IntPtr SearchBase => throw new NotSupportedException();

            public IntPtr TextSectionBase => throw new NotSupportedException();

            public long TextSectionOffset => throw new NotSupportedException();

            public int TextSectionSize => throw new NotSupportedException();

            public IntPtr DataSectionBase => throw new NotSupportedException();

            public long DataSectionOffset => throw new NotSupportedException();

            public int DataSectionSize => throw new NotSupportedException();

            public IntPtr RDataSectionBase => throw new NotSupportedException();

            public long RDataSectionOffset => throw new NotSupportedException();

            public int RDataSectionSize => throw new NotSupportedException();

            public ProcessModule Module => throw new NotSupportedException();

            public nint GetStaticAddressFromSig(string signature, int offset = 0) => throw new NotSupportedException();

            public bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0) => throw new NotSupportedException();

            public nint ScanData(string signature) => throw new NotSupportedException();

            public bool TryScanData(string signature, out nint result) => throw new NotSupportedException();

            public nint ScanModule(string signature) => throw new NotSupportedException();

            public bool TryScanModule(string signature, out nint result) => throw new NotSupportedException();

            public nint ResolveRelativeAddress(nint nextInstAddr, int relOffset) => throw new NotSupportedException();

            public nint ScanText(string signature) => found.TryGetValue(signature, out var address) ? address : throw new KeyNotFoundException(signature);

            public bool TryScanText(string signature, out nint result) => found.TryGetValue(signature, out result);

            public nint[] ScanAllText(string signature) => throw new NotSupportedException();

            public IEnumerable<nint> ScanAllText(string signature, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
