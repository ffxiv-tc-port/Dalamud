#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Common;
using Dalamud.Data;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

using Lumina.Text.ReadOnly;

using Xunit;

namespace Dalamud.Test.Data
{
    // The test process never runs the ClientStructs resolver, so LayoutWorld.AddRsvString is unresolved here exactly as
    // on a client whose signature stopped matching.
    public class RsvResolverUnresolvedTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public RsvResolverUnresolvedTests()
        {
            // Reporting the notice looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void WithoutTheGameFunctionTheResolverSkipsItsHookAndReportsOnce()
        {
            Assert.Equal(nint.Zero, LayoutWorld.Addresses.AddRsvString.Value);

            var resolver = new RsvResolver();

            Assert.False(resolver.IsHooked);
            Assert.False(resolver.TryResolve(new ReadOnlySeString("_rsv_1"u8.ToArray()), out _));
            Assert.False(FeatureDisabledEcho.Instance.Enqueue(RsvResolver.EchoKey, "again"));
            resolver.Dispose();
        }

        [Fact]
        public void ADataManagerThatCannotReadGameDataFailsLikeAnyServiceWithoutEndingTheProcess()
        {
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "sqpack")));

            var dalamud = (Dalamud)RuntimeHelpers.GetUninitializedObject(typeof(Dalamud));
            typeof(Dalamud).GetProperty(nameof(Dalamud.StartInfo), AnyInstance)!.SetValue(dalamud, new DalamudStartInfo());
            var ctor = typeof(DataManager).GetConstructors(AnyInstance).Single();

            var thrown = Assert.Throws<TargetInvocationException>(() => ctor.Invoke([dalamud]));

            var failure = Assert.IsType<InvalidOperationException>(thrown.InnerException);
            Assert.Contains("sqpack", failure.Message);
            Assert.IsType<DirectoryNotFoundException>(failure.InnerException);
        }
    }
}
