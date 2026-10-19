using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;

using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Ipc.Internal.Converters;
using Dalamud.Utility.Internal;

using Newtonsoft.Json;
using Serilog;

using Direction = Dalamud.Utility.Internal.IpcJsonConversionReport.Direction;

namespace Dalamud.Plugin.Ipc.Internal;

/// <summary>
/// This class implements the channels and serialization needed for the typed gates to interact with each other.
/// </summary>
internal class CallGateChannel
{
    /// <summary>
    /// Settings for <see cref="ConvertObject"/>; shared because nothing mutates them after construction.
    /// </summary>
    private static readonly JsonSerializerSettings ConvertSettings = new()
    {
        Converters = { new GameObjectConverter() },
    };

    /// <summary>
    /// The actual storage.
    /// </summary>
    private readonly HashSet<Delegate> subscriptions = new();

    /// <summary>
    /// A copy of the actual storage, that will be cleared and populated depending on changes made to
    /// <see cref="subscriptions"/>.
    /// </summary>
    private ImmutableList<Delegate>? subscriptionsCopy;

    /// <summary>
    /// What <see cref="SendMessage"/> iterates; cleared together with <see cref="subscriptionsCopy"/>.
    /// </summary>
    private InvokeTarget[]? subscriptionTargets;

    private InvokeTarget? actionTarget;

    private InvokeTarget? funcTarget;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallGateChannel"/> class.
    /// </summary>
    /// <param name="name">The name of this IPC registration.</param>
    public CallGateChannel(string name)
    {
        this.Name = name;
    }

    /// <summary>
    /// Gets the name of the IPC registration.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Gets a list of delegate subscriptions for when SendMessage is called.
    /// </summary>
    public IReadOnlyList<Delegate> Subscriptions
    {
        get
        {
            var copy = this.subscriptionsCopy;
            if (copy is not null)
                return copy;
            lock (this.subscriptions)
                return this.subscriptionsCopy ??= this.subscriptions.ToImmutableList();
        }
    }

    /// <summary>
    /// Gets or sets an action for when InvokeAction is called.
    /// </summary>
    public Delegate? Action
    {
        get => this.actionTarget?.Handler;
        set => this.actionTarget = value is null ? null : new InvokeTarget(value);
    }

    /// <summary>
    /// Gets or sets a func for when InvokeFunc is called.
    /// </summary>
    public Delegate? Func
    {
        get => this.funcTarget?.Handler;
        set => this.funcTarget = value is null ? null : new InvokeTarget(value);
    }

    /// <summary>
    /// Gets a value indicating whether this <see cref="CallGateChannel"/> is not being used.
    /// </summary>
    public bool IsEmpty => this.Action is null && this.Func is null && this.Subscriptions.Count == 0;

    /// <inheritdoc cref="CallGatePubSubBase.Subscribe"/>
    internal void Subscribe(Delegate action)
    {
        lock (this.subscriptions)
        {
            this.subscriptionsCopy = null;
            this.subscriptionTargets = null;
            this.subscriptions.Add(action);
        }
    }

    /// <inheritdoc cref="CallGatePubSubBase.Unsubscribe"/>
    internal void Unsubscribe(Delegate action)
    {
        lock (this.subscriptions)
        {
            this.subscriptionsCopy = null;
            this.subscriptionTargets = null;
            this.subscriptions.Remove(action);
        }
    }

    /// <summary>Removes every subscription, action and function part that <paramref name="remove"/> matches.</summary>
    /// <remarks>
    /// The rest of a multicast delegate stays registered. The action and function are swapped only if no one replaced
    /// them meanwhile, the way their setters write them: without a lock. A call already under way may still run a removed part.
    /// </remarks>
    /// <param name="remove">Whether one part of a registered delegate is to be removed.</param>
    /// <param name="removed">Receives each removed part with what it was registered as.</param>
    internal void RemoveDelegates(Func<Delegate, bool> remove, List<(string Kind, Delegate Part)> removed)
    {
        lock (this.subscriptions)
        {
            List<Delegate>? matched = null;
            foreach (var subscription in this.subscriptions)
            {
                if (Array.Exists(subscription.GetInvocationList(), p => remove(p)))
                    (matched ??= []).Add(subscription);
            }

            if (matched != null)
            {
                this.subscriptionsCopy = null;
                this.subscriptionTargets = null;
                foreach (var subscription in matched)
                {
                    this.subscriptions.Remove(subscription);
                    if (Split(subscription, remove, "subscription", removed) is { } rest)
                        this.subscriptions.Add(rest);
                }
            }
        }

        RemoveTarget(ref this.actionTarget, remove, "action", removed);
        RemoveTarget(ref this.funcTarget, remove, "function", removed);
    }

    /// <summary>
    /// Invoke all actions that have subscribed to this IPC.
    /// </summary>
    /// <param name="args">Message arguments.</param>
    internal void SendMessage(object?[]? args)
    {
        var targets = this.GetSubscriptionTargets();
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            this.CheckAndConvertArgs(args, target.ParameterTypes, Direction.MessageArgument);

            target.Handler.DynamicInvoke(args);
        }
    }

    /// <summary>
    /// Invoke an action registered for inter-plugin communication.
    /// </summary>
    /// <param name="args">Action arguments.</param>
    /// <exception cref="IpcNotReadyError">This is thrown when the IPC publisher has not registered a func for calling yet.</exception>
    internal void InvokeAction(object?[]? args)
    {
        var target = this.actionTarget ?? throw new IpcNotReadyError(this.Name);

        this.CheckAndConvertArgs(args, target.ParameterTypes, Direction.ActionArgument);

        target.Handler.DynamicInvoke(args);
    }

    /// <summary>
    /// Invoke a function registered for inter-plugin communication.
    /// </summary>
    /// <param name="args">Func arguments.</param>
    /// <returns>The return value.</returns>
    /// <typeparam name="TRet">The return type.</typeparam>
    /// <exception cref="IpcNotReadyError">This is thrown when the IPC publisher has not registered a func for calling yet.</exception>
    internal TRet InvokeFunc<TRet>(object?[]? args)
    {
        var target = this.funcTarget ?? throw new IpcNotReadyError(this.Name);

        // No arguments and the very same return type: nothing to check or convert, so call the delegate itself
        // and a value type result is not boxed. A throwing provider is wrapped the way DynamicInvoke wraps it.
        // The return type test is not redundant: a Func<string> also passes "is Func<object>".
        if ((args is null || args.Length == 0) && target.ParameterTypes.Length == 0
            && typeof(TRet) == target.ReturnType && target.Handler is Func<TRet> direct)
        {
            try
            {
                return direct();
            }
            catch (Exception ex)
            {
                throw new TargetInvocationException(ex);
            }
        }

        this.CheckAndConvertArgs(args, target.ParameterTypes, Direction.FunctionArgument);

        var result = target.Handler.DynamicInvoke(args);

        if (typeof(TRet) != target.ReturnType)
            result = this.ConvertObject(result, typeof(TRet), Direction.FunctionResult);

        return (TRet)result;
    }

    private static bool IsInBaseTypeChain(Type? type, Type target)
    {
        for (; type != null && type != typeof(object); type = type.BaseType)
        {
            if (type == target)
                return true;
        }

        return false;
    }

    private static Delegate? Split(Delegate registered, Func<Delegate, bool> remove, string kind, List<(string Kind, Delegate Part)> removed)
    {
        Delegate? rest = null;
        foreach (var part in registered.GetInvocationList())
        {
            if (remove(part))
                removed.Add((kind, part));
            else
                rest = Delegate.Combine(rest, part);
        }

        return rest;
    }

    private static void RemoveTarget(
        ref InvokeTarget? field, Func<Delegate, bool> remove, string kind, List<(string Kind, Delegate Part)> removed)
    {
        var current = Volatile.Read(ref field);
        if (current == null || !Array.Exists(current.Handler.GetInvocationList(), p => remove(p)))
            return;

        var parts = new List<(string Kind, Delegate Part)>();
        var rest = Split(current.Handler, remove, kind, parts);
        if (Interlocked.CompareExchange(ref field, rest is null ? null : new InvokeTarget(rest), current) == current)
            removed.AddRange(parts);
    }

    private InvokeTarget[] GetSubscriptionTargets()
    {
        var targets = this.subscriptionTargets;
        if (targets is not null)
            return targets;
        lock (this.subscriptions)
            return this.subscriptionTargets ??= this.subscriptions.Select(static x => new InvokeTarget(x)).ToArray();
    }

    private void CheckAndConvertArgs(object?[]? args, Type[] paramTypes, Direction direction)
    {
        if (args is null)
        {
            if (paramTypes.Length == 0)
                return;
            throw new IpcLengthMismatchError(this.Name, 0, paramTypes.Length);
        }

        if (args.Length != paramTypes.Length)
            throw new IpcLengthMismatchError(this.Name, args.Length, paramTypes.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var paramType = paramTypes[i];

            if (arg == null)
            {
                if (paramType.IsValueType)
                {
                    if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(Nullable<>))
                        continue;

                    throw new IpcValueNullError(this.Name, paramType, i);
                }

                continue;
            }

            var argType = arg.GetType();
            if (argType != paramType)
            {
                // check the inheritance tree
                if (IsInBaseTypeChain(argType.BaseType, paramType))
                {
                    // The source type inherits from the destination type
                    continue;
                }

                args[i] = this.ConvertObject(arg, paramType, direction);
            }
        }
    }

    private object? ConvertObject(object? obj, Type type, Direction direction)
    {
        if (obj is null)
            return null;

        IpcJsonConversionReport.Counter.Shared.Count(this.Name, direction, obj.GetType(), type);

        var settings = ConvertSettings;

        var json = JsonConvert.SerializeObject(obj, settings);

        try
        {
            return JsonConvert.DeserializeObject(json, type);
        }
        catch (Exception)
        {
            Log.Verbose($"Could not convert {obj.GetType().Name} to {type.Name}, will look for compatible type instead");
        }

        // If type -> type fails, try to find an object that matches.
        var sourceType = obj.GetType();
        var fieldNames = sourceType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                                   .Select(f => f.Name);
        var propNames = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                  .Select(p => p.Name);

        var assignableTypes = type.Assembly.GetTypes()
                                  .Where(t => type.IsAssignableFrom(t) && type != t)
                                  .ToArray();

        foreach (var assignableType in assignableTypes)
        {
            var matchesFields = assignableType.GetFields().All(f => fieldNames.Contains(f.Name));
            var matchesProps = assignableType.GetProperties().All(p => propNames.Contains(p.Name));
            if (matchesFields && matchesProps)
            {
                type = assignableType;
                break;
            }
        }

        try
        {
            return JsonConvert.DeserializeObject(json, type, settings);
        }
        catch (Exception ex)
        {
            throw new IpcTypeMismatchError(this.Name, obj.GetType(), type, ex);
        }
    }

    /// <summary>
    /// A registered delegate with its signature read once instead of on every call.
    /// It holds the plugin's types, so it must be dropped the moment its delegate is removed.
    /// </summary>
    private sealed class InvokeTarget
    {
        public InvokeTarget(Delegate handler)
        {
            var methodInfo = handler.GetMethodInfo();
            this.Handler = handler;
            this.ParameterTypes = Array.ConvertAll(methodInfo.GetParameters(), static pi => pi.ParameterType);
            this.ReturnType = methodInfo.ReturnType;
        }

        public Delegate Handler { get; }

        public Type[] ParameterTypes { get; }

        public Type ReturnType { get; }
    }
}
