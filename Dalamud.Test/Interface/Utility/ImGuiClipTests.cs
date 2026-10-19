#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

using Xunit;

namespace Dalamud.Test.Interface.Utility
{
    // A begun clipper stays on the ImGui context's clipper stack (shared by every plugin) until End, and its native
    // allocation lives until Destroy. Running a clipper needs a native context, so this reads the IL instead: every
    // overload that creates one must Begin it inside a try whose finally Ends and Destroys it, and Destroy only there.
    public class ImGuiClipTests
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => (OpCode)x.GetValue(null)!)
            .ToDictionary(x => x.Value);

        [Fact]
        public void EveryClipperIsEndedAndDestroyedInAFinally()
        {
            var overloads = 0;
            foreach (var method in typeof(ImGuiClip).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var body = method.GetMethodBody()!;
                if (!body.LocalVariables.Any(x => x.LocalType == typeof(ImGuiListClipperPtr)))
                    continue;

                overloads++;
                var calls = ClipperCallsOf(method);
                var begin = Assert.Single(calls, x => x.Name == "Begin");
                var destroy = Assert.Single(calls, x => x.Name == "Destroy");
                var guard = Assert.Single(
                    body.ExceptionHandlingClauses,
                    x => x.Flags == ExceptionHandlingClauseOptions.Finally && Inside(destroy.Offset, x.HandlerOffset, x.HandlerLength));

                Assert.True(Inside(begin.Offset, guard.TryOffset, guard.TryLength), $"{method}: Begin is outside the guarded try");
                Assert.Contains(calls, x => x.Name == "End" && Inside(x.Offset, guard.HandlerOffset, guard.HandlerLength) && x.Offset < destroy.Offset);
                Assert.DoesNotContain(calls, x => x.Name == "End" && !Inside(x.Offset, guard.HandlerOffset, guard.HandlerLength));
            }

            Assert.Equal(3, overloads);
        }

        private static bool Inside(int offset, int start, int length) => offset >= start && offset < start + length;

        private static List<(string Name, int Offset)> ClipperCallsOf(MethodInfo method)
        {
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var calls = new List<(string, int)>();
            for (var i = 0; i < il.Length;)
            {
                var at = i;
                var value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
                var op = OpCodesByValue[value];
                i += op.Size;
                if (op.OperandType == OperandType.InlineMethod)
                {
                    var callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), method.DeclaringType!.GetGenericArguments(), method.GetGenericArguments())!;
                    var onClipper = callee.DeclaringType == typeof(ImGuiListClipperPtr) ||
                                    callee.GetParameters().FirstOrDefault()?.ParameterType == typeof(ImGuiListClipperPtr);
                    if (onClipper)
                        calls.Add((callee.Name, at));
                }

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
