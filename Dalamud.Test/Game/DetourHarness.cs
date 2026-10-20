#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

namespace Dalamud.Test.Game
{
    /// <summary>Records every call of a fake Original; pointer arguments are recorded as nint.</summary>
    public sealed class OriginalRecorder
    {
        public List<object?[]> Calls { get; } = [];

        public object? ReturnValue { get; set; }

        public object? Record(object?[] args)
        {
            this.Calls.Add(args);
            return this.ReturnValue;
        }
    }

    /// <summary>A hook whose Original is a recording stand-in; nothing is hooked.</summary>
    internal sealed class FakeHook<T> : Hook<T>
        where T : Delegate
    {
        private readonly T original;

        public FakeHook(T original)
            : base(IntPtr.Zero)
        {
            this.original = original;
        }

        public override T Original => this.original;

        public override bool IsEnabled => true;

        public override string BackendName => "Fake";

        public override void Enable()
        {
        }

        public override void Disable()
        {
        }
    }

    /// <summary>
    /// Runs Dalamud's private detours outside the game: their hook fields get a <see cref="FakeHook{T}"/>, and they are
    /// called through emitted stubs, so pointer parameters (and private delegate types) need no unsafe code here.
    /// </summary>
    internal static class DetourHarness
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<short, OpCode> OpCodesByValue =
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                           .Select(f => (OpCode)f.GetValue(null)!)
                           .ToDictionary(o => o.Value);

        public static OriginalRecorder InstallFakeHook(object service, string fieldName, object? returnValue = null)
        {
            var field = FindField(service.GetType(), fieldName);
            var delegateType = field.FieldType.GetGenericArguments()[0];
            var invoke = delegateType.GetMethod("Invoke")!;

            var recorder = new OriginalRecorder { ReturnValue = returnValue ?? DefaultOf(invoke.ReturnType) };
            var fake = Activator.CreateInstance(
                typeof(FakeHook<>).MakeGenericType(delegateType),
                AnyInstance,
                null,
                [RecordingDelegate(delegateType, invoke, recorder)],
                null);

            field.SetValue(service, fake);
            return recorder;
        }

        public static void SetField(object target, string name, object? value)
            => FindField(target.GetType(), name).SetValue(target, value);

        public static object? GetStaticField(Type type, string name)
            => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null);

        /// <summary>Calls a private instance method; pointer arguments and results are passed as nint.</summary>
        public static object? Invoke(object target, string methodName, params object?[] args)
        {
            var method = target.GetType().GetMethod(methodName, AnyInstance)!;
            var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();

            var stub = new DynamicMethod(
                "Invoke_" + methodName,
                typeof(object),
                [typeof(object), typeof(object[])],
                typeof(DetourHarness).Module,
                skipVisibility: true);
            var il = stub.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, method.DeclaringType!);
            for (var i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Unbox_Any, parameters[i].IsPointer ? typeof(nint) : parameters[i]);
            }

            il.Emit(OpCodes.Call, method);
            if (method.ReturnType == typeof(void))
                il.Emit(OpCodes.Ldnull);
            else
                il.Emit(OpCodes.Box, method.ReturnType.IsPointer ? typeof(nint) : method.ReturnType);
            il.Emit(OpCodes.Ret);

            var call = (Func<object, object?[], object?>)stub.CreateDelegate(typeof(Func<object, object?[], object?>));
            return call(target, args);
        }

        /// <summary>
        /// Describes where each Original call of a method sits: O outside any try with a catch, T inside such a try,
        /// C inside a catch block, F inside a finally block.
        /// </summary>
        public static string OriginalCallSites(Type type, string methodName)
        {
            var method = type.GetMethod(methodName, AnyInstance)!;
            var body = method.GetMethodBody()!;
            var sites = new List<string>();
            foreach (var offset in CallOffsets(method, IsOriginalGetter))
            {
                var where = string.Empty;
                foreach (var clause in body.ExceptionHandlingClauses)
                {
                    var hasCatch = clause.Flags is ExceptionHandlingClauseOptions.Clause or ExceptionHandlingClauseOptions.Filter;
                    if (hasCatch && offset >= clause.TryOffset && offset < clause.TryOffset + clause.TryLength)
                        where += "T";
                    if (offset >= clause.HandlerOffset && offset < clause.HandlerOffset + clause.HandlerLength)
                        where += clause.Flags == ExceptionHandlingClauseOptions.Finally ? "F" : "C";
                }

                sites.Add(where.Length == 0 ? "O" : new string(where.Distinct().OrderBy(c => c).ToArray()));
            }

            return string.Join(",", sites);
        }

        public static nint Bytes(params byte[] bytes)
        {
            var block = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, block, bytes.Length);
            return block;
        }

        public static nint Zeroed(int size) => Bytes(new byte[size]);

        private static bool IsOriginalGetter(MethodBase callee)
            => callee.Name is "get_Original" or "get_OriginalDisposeSafe"
               && callee.DeclaringType is { IsGenericType: true } owner
               && owner.GetGenericTypeDefinition() == typeof(Hook<>);

        private static IEnumerable<int> CallOffsets(MethodBase method, Func<MethodBase, bool> predicate)
        {
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var position = 0;
            while (position < il.Length)
            {
                var start = position;
                var value = (short)il[position++];
                if (value == 0xFE)
                    value = unchecked((short)(0xFE00 | il[position++]));

                var opCode = OpCodesByValue[value];
                var operandSize = opCode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                    _ => 4,
                };

                if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                    && TryResolve(method, BitConverter.ToInt32(il, position)) is { } callee
                    && predicate(callee))
                {
                    yield return start;
                }

                position += operandSize;
            }
        }

        private static MethodBase? TryResolve(MethodBase method, int token)
        {
            try
            {
                return method.Module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static Delegate RecordingDelegate(Type delegateType, MethodInfo invoke, OriginalRecorder recorder)
        {
            var parameters = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
            var stub = new DynamicMethod(
                "FakeOriginal",
                invoke.ReturnType,
                new[] { typeof(OriginalRecorder) }.Concat(parameters).ToArray(),
                typeof(DetourHarness).Module,
                skipVisibility: true);
            var il = stub.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (var i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Box, parameters[i].IsPointer ? typeof(nint) : parameters[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }

            il.Emit(OpCodes.Callvirt, typeof(OriginalRecorder).GetMethod(nameof(OriginalRecorder.Record))!);
            if (invoke.ReturnType == typeof(void))
                il.Emit(OpCodes.Pop);
            else
                il.Emit(OpCodes.Unbox_Any, invoke.ReturnType.IsPointer ? typeof(nint) : invoke.ReturnType);
            il.Emit(OpCodes.Ret);

            return stub.CreateDelegate(delegateType, recorder);
        }

        private static object? DefaultOf(Type type)
            => type == typeof(void) ? null : type.IsPointer ? (object)nint.Zero : Activator.CreateInstance(type);

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            throw new MissingFieldException(type.FullName, name);
        }
    }
}
