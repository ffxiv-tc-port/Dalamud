using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.Utility.Internal;

/// <summary>
/// What one framework update spent its time on, for the FrameworkUpdate hitch line: each plugin's Update, the queued
/// framework-thread tasks, and the rest (Dalamud's own work and subscribers).
/// </summary>
/// <remarks>
/// Framework thread only. A frame records into fixed fields and allocates nothing; the slowest task is only
/// referenced, and reflection runs only in <see cref="Describe"/>, for a line being written.
/// </remarks>
internal sealed class FrameworkUpdateAttribution
{
    /// <summary>Label of the queued framework-thread tasks (RunOnTick, RunOnFrameworkThread, Run and their continuations).</summary>
    public const string TasksLabel = "framework thread tasks";

    /// <summary>Label of the time no plugin Update and no task accounts for.</summary>
    public const string RestLabel = "Dalamud and the rest";

    private static bool taskActionUnavailable;

    private SlowestThree plugins;
    private long pluginTicks;
    private long taskTicks;
    private Task? slowestTask;
    private Delegate? slowestTaskAction;
    private long slowestTaskTicks;
    private bool recording;

    /// <summary>Starts a frame: forgets the previous one and records until <see cref="End"/>.</summary>
    public void Begin()
    {
        this.plugins = default;
        this.pluginTicks = 0;
        this.taskTicks = 0;
        this.slowestTask = null;
        this.slowestTaskAction = null;
        this.slowestTaskTicks = 0;
        this.recording = true;
    }

    /// <summary>Stops recording and drops the task reference.</summary>
    public void End()
    {
        this.recording = false;
        this.slowestTask = null;
        this.slowestTaskAction = null;
    }

    /// <summary>Records one plugin's Update forward.</summary>
    /// <param name="pluginName">The plugin's internal name.</param>
    /// <param name="ticks">How long it ran, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    public void AddPlugin(string pluginName, long ticks)
    {
        if (!this.recording)
            return;

        this.pluginTicks += ticks;
        this.plugins.Offer(pluginName, ticks);
    }

    /// <summary>Records the queued tasks run this frame.</summary>
    /// <param name="ticks">How long running them took.</param>
    /// <param name="slowest">The task that ran longest, or null.</param>
    /// <param name="slowestAction">Its delegate, taken before it ran (a finished task lets go of it).</param>
    /// <param name="slowestTicks">How long that one ran.</param>
    public void SetTasks(long ticks, Task? slowest, Delegate? slowestAction, long slowestTicks)
    {
        if (!this.recording)
            return;

        this.taskTicks = ticks;
        this.slowestTask = slowest;
        this.slowestTaskAction = slowestAction;
        this.slowestTaskTicks = slowestTicks;
    }

    /// <summary>Describes the three slowest parts of the frame. Never throws.</summary>
    /// <param name="totalTicks">The whole span the hitch detector measured.</param>
    /// <returns>Like <c>AutoRetainer 61.2 ms, Dalamud and the rest 3.0 ms, Lifestream 0.4 ms</c>.</returns>
    public string Describe(long totalTicks)
    {
        try
        {
            var ranked = this.plugins;
            ranked.Offer(TasksLabel, this.taskTicks);
            ranked.Offer(RestLabel, Math.Max(0, totalTicks - this.pluginTicks - this.taskTicks));

            var text = new StringBuilder();
            for (var i = 0; i < ranked.Count; i++)
            {
                var (name, ticks) = ranked[i];
                if (i > 0)
                    text.Append(", ");
                text.Append(name).Append(' ').Append(Ms(ticks)).Append(" ms");
                if (ReferenceEquals(name, TasksLabel) && this.slowestTask is { } task)
                    text.Append(" (longest ").Append(Ms(this.slowestTaskTicks)).Append(" ms: ").Append(DescribeTask(task, this.slowestTaskAction)).Append(')');
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            return "? (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>Names the code a task runs: the innermost delegate or state machine outside the runtime and Dalamud.</summary>
    /// <param name="task">The task.</param>
    /// <param name="action">The task's delegate from <see cref="ActionOf"/>, taken before it ran.</param>
    /// <returns>Like <c>SomePlugin.Tasks+&lt;&gt;c.&lt;Tick&gt;b__3_0 (SomePlugin)</c>; never throws.</returns>
    internal static string DescribeTask(Task task, Delegate? action)
    {
        try
        {
            var current = action;
            if ((current == null || IsInfrastructure(current.Method.DeclaringType)) && task.AsyncState is Delegate state)
                current = state;

            // Wrappers (ContinueWhenAll's lambda, RunOnTick's closure) hold the caller's delegate in a field.
            for (var depth = 0; current != null && depth < 4; depth++)
            {
                var owner = current.Method.DeclaringType;
                if (!IsInfrastructure(owner))
                    return Name(owner!, current.Method.Name);

                var target = current.Target;
                if (target == null)
                    break;

                if (StateMachineOf(target.GetType()) is { } stateMachine)
                    return Name(stateMachine, null);

                current = FirstDelegateField(target);
            }

            return current == null ? task.GetType().Name : Name(current.Method.DeclaringType, current.Method.Name);
        }
        catch (Exception ex)
        {
            return "? (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>Gets the delegate a task will run; null once it has run, or when this runtime lays Task out differently.</summary>
    /// <param name="task">The task.</param>
    /// <returns>The delegate, or null.</returns>
    internal static Delegate? ActionOf(Task task)
    {
        if (taskActionUnavailable)
            return null;

        try
        {
            return TaskAction(task);
        }
        catch (Exception)
        {
            taskActionUnavailable = true;
            return null;
        }
    }

    // Bound by the JIT, no reflection per call. Throws MissingFieldException if the field is gone.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_action")]
    private static extern ref Delegate? TaskAction(Task task);

    private static string Ms(long ticks) => HitchCounters.Milliseconds(ticks).ToString("0.0", CultureInfo.InvariantCulture);

    private static bool IsInfrastructure(Type? type) =>
        type == null || type.Assembly == typeof(object).Assembly || type.Assembly == typeof(FrameworkUpdateAttribution).Assembly;

    // AsyncStateMachineBox<TResult, TStateMachine>: the state machine is the generic argument outside the runtime.
    private static Type? StateMachineOf(Type type)
    {
        if (!type.IsGenericType)
            return null;

        foreach (var argument in type.GetGenericArguments())
        {
            if (!IsInfrastructure(argument))
                return argument;
        }

        return null;
    }

    private static Delegate? FirstDelegateField(object target)
    {
        foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(Delegate).IsAssignableFrom(field.FieldType) && field.GetValue(target) is Delegate found)
                return found;
        }

        return null;
    }

    private static string Name(Type? type, string? method)
    {
        var text = type?.FullName ?? type?.Name ?? "?";
        if (method != null)
            text += "." + method;
        return text + " (" + (type?.Assembly.GetName().Name ?? "?") + ")";
    }
}
