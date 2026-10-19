#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using Dalamud.Interface.Utility.Raii;

using Xunit;

namespace Dalamud.Test.Interface.Utility
{
    // A conditional end object without success has nothing to end, so every such call shares one object.
    // A successful one ends something exactly once and must never be shared. None of this touches ImGui.
    public class ImRaiiEndObjectTests
    {
        [Fact]
        public void AFailedResultNeverRunsItsEndAction()
        {
            var ended = 0;
            var failed = ImRaii.Conditional(() => ended++, false);

            Assert.False(failed.Success);
            Assert.True(!failed);
            for (var i = 0; i < 3; i++)
                failed.Dispose();

            Assert.Equal(0, ended);
            Assert.False(failed.Success);
        }

        [Fact]
        public void FailedResultsShareOneObjectThatIsNotTheEmptyOne()
        {
            var a = ImRaii.Conditional(() => { }, false);
            var b = ImRaii.Conditional(() => throw new InvalidOperationException(), false);

            Assert.Same(a, b);
            Assert.Same(a, ImRaii.Disabled(false));
            Assert.NotSame(ImRaii.IEndObject.Empty, a);
            Assert.IsType<ImRaii.EndConditionally>(a);
        }

        [Fact]
        public void ASuccessfulResultIsItsOwnObjectAndEndsExactlyOnce()
        {
            var endedA = 0;
            var endedB = 0;
            var a = ImRaii.Conditional(() => endedA++, true);
            var b = ImRaii.Conditional(() => endedB++, true);
            var failed = ImRaii.Conditional(() => throw new InvalidOperationException(), false);

            Assert.NotSame(a, b);
            Assert.NotSame(failed, a);
            Assert.True(a.Success);
            Assert.False(((ImRaii.EndConditionally)a).Disposed);

            failed.Dispose();
            Assert.Equal((0, 0), (endedA, endedB));

            a.Dispose();
            a.Dispose();
            Assert.Equal((1, 0), (endedA, endedB));
            Assert.True(((ImRaii.EndConditionally)a).Disposed);
            Assert.False(((ImRaii.EndConditionally)b).Disposed);

            failed.Dispose();
            b.Dispose();
            b.Dispose();
            Assert.Equal((1, 1), (endedA, endedB));
        }

        [Fact]
        public void AFailedResultAllocatesNothing()
        {
            Action end = () => { };
            for (var i = 0; i < 2000; i++)
            {
                ImRaii.Conditional(end, false).Dispose();
                ImRaii.Disabled(false).Dispose();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                using var conditional = ImRaii.Conditional(end, false);
                using var disabled = ImRaii.Disabled(false);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // With nothing disabled Enabled() has nothing to restore: it returns the shared failed result without touching ImGui.
        [Fact]
        public void EnabledWithNothingDisabledAllocatesNothing()
        {
            Assert.Same(ImRaii.Disabled(false), ImRaii.Enabled());
            for (var i = 0; i < 2000; i++)
                ImRaii.Enabled().Dispose();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                using var enabled = ImRaii.Enabled();
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // ImPlot cannot run here without a native context, so this reads the IL of every ImRaii method that begins
        // something in ImPlot: each must go through Conditional and none may build its own EndConditionally.
        [Fact]
        public void EveryPlotEndObjectGoesThroughConditional()
        {
            var conditional = typeof(ImRaii).GetMethod(nameof(ImRaii.Conditional), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            var plotMethods = 0;
            foreach (var method in typeof(ImRaii).GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (method.ReturnType != typeof(ImRaii.IEndObject))
                    continue;

                var calls = CallsOf(method);
                if (!calls.Exists(x => x.Callee.DeclaringType?.FullName == "Dalamud.Bindings.ImPlot.ImPlot"))
                    continue;

                plotMethods++;
                Assert.Contains(calls, x => x.Op == OpCodes.Call && x.Callee == conditional);
                Assert.DoesNotContain(calls, x => x.Op == OpCodes.Newobj && x.Callee.DeclaringType == typeof(ImRaii.EndConditionally));
            }

            Assert.Equal(17, plotMethods);
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!)
            .ToDictionary(x => x.Value);

        private static List<(OpCode Op, MethodBase Callee)> CallsOf(MethodInfo method)
        {
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var calls = new List<(OpCode, MethodBase)>();
            for (var i = 0; i < il.Length;)
            {
                var value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
                var op = OpCodesByValue[value];
                i += op.Size;
                if (op.OperandType == OperandType.InlineMethod)
                    calls.Add((op, method.Module.ResolveMethod(BitConverter.ToInt32(il, i))!));

                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                    _ => 4,
                };
            }

            return calls;
        }
    }
}
