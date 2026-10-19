using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Loader;
using Dalamud.Utility;

using Serilog;

namespace Dalamud.Interface.Internal.Asserts;

/// <summary>
/// Class responsible for registering and handling ImGui asserts.
/// </summary>
internal class AssertHandler : IDisposable
{
    private const int HideThreshold = 20;
    private const int HidePrintEvery = 500;

    private const int MaxCallerFrames = 6;

    [ThreadStatic]
    private static string? drawingPlugin;

    private readonly HashSet<string> ignoredAsserts = [];
    private readonly Dictionary<string, uint> assertCounts = new();
    private readonly AssertSiteCounter siteCounter;

    // Store callback to avoid it from being GC'd
    private readonly AssertCallbackDelegate callback;

    private bool everShownAssertThisSession = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssertHandler"/> class.
    /// </summary>
    public AssertHandler()
        : this(AssertSiteCounter.Shared)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AssertHandler"/> class.
    /// </summary>
    /// <param name="siteCounter">Where every failure is counted, shown or not.</param>
    internal unsafe AssertHandler(AssertSiteCounter siteCounter)
    {
        this.siteCounter = siteCounter;
        this.callback = this.OnImGuiAssert;
    }

    private unsafe delegate void AssertCallbackDelegate(
        void* expr,
        void* file,
        int line);

    /// <summary>
    /// Gets or sets a value indicating whether ImGui asserts should be shown to the user.
    /// </summary>
    public bool ShowAsserts { get; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether we want to hide asserts that occur frequently (= every update)
    /// and whether we want to log callstacks.
    /// </summary>
    public bool EnableVerboseLogging { get; set; }

    /// <summary>
    /// Gets or sets the internal name of the plugin whose UiBuilder draw is running on this thread, or null.
    /// Only for describing asserts, which Dalamud's code around a plugin's draw can also raise.
    /// </summary>
    internal static string? DrawingPlugin
    {
        get => drawingPlugin;
        set => drawingPlugin = value;
    }

    /// <summary>
    /// Register the cimgui assert handler with the native library.
    /// </summary>
    public unsafe void Setup()
    {
        CustomNativeFunctions.igCustom_SetAssertCallback(
            Marshal.GetFunctionPointerForDelegate(this.callback).ToPointer());
    }

    /// <summary>
    /// Unregister the cimgui assert handler with the native library.
    /// </summary>
    public unsafe void Shutdown()
    {
        CustomNativeFunctions.igCustom_SetAssertCallback(null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Shutdown();
    }

    private static string? ExtractImguiFunction(StackTrace stackTrace)
    {
        var frame = stackTrace.GetFrames()
                              .FirstOrDefault(f => f.GetMethod()?.DeclaringType?.Namespace == "Dalamud.Bindings.ImGui");
        if (frame == null)
            return null;

        var method = frame.GetMethod();
        if (method == null)
            return null;

        return $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.Name))})";
    }

    private static StackTrace GenerateStackTrace()
    {
        var trace = DiagnosticUtil.GetUsefulTrace(new StackTrace(true));
        var frames = trace.GetFrames().ToList();

        // Remove everything that happens in the assert context.
        var lastAssertIdx = frames.FindLastIndex(f => f.GetMethod()?.DeclaringType == typeof(AssertHandler));
        if (lastAssertIdx >= 0)
        {
            frames.RemoveRange(0, lastAssertIdx + 1);
        }

        var firstInterfaceManagerIdx = frames.FindIndex(f => f.GetMethod()?.DeclaringType == typeof(InterfaceManager));
        if (firstInterfaceManagerIdx >= 0)
        {
            frames.RemoveRange(firstInterfaceManagerIdx, frames.Count - firstInterfaceManagerIdx);
        }

        return new StackTrace(frames);
    }

    // Same test as Marshal.PtrToStringAnsi: null and Win32 atoms give no string. Nothing is read here.
    private static unsafe bool GivesString(void* pointer) => ((nuint)pointer & ~(nuint)0xFFFF) != 0;

    // Where the failing ImGui call came from: the thread, the nearest plugin on the stack (by load context, so a
    // library inside a plugin counts as that plugin), the binding that was called and the frames below it.
    private static string DescribeCaller()
    {
        var text = new StringBuilder(
            ThreadSafety.IsMainThread
                ? "framework thread"
                : $"thread {Environment.CurrentManagedThreadId} \"{Thread.CurrentThread.Name}\"");
        var callers = new StringBuilder();
        string? binding = null;
        string? plugin = null;
        string? lastAssembly = null;
        var listed = 0;
        foreach (var frame in new StackTrace(1, false).GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method == null || type == null || type == typeof(AssertHandler))
                continue;

            if (listed == 0 && type.Namespace?.StartsWith("Dalamud.Bindings", StringComparison.Ordinal) == true)
            {
                binding = $"{type.Name}.{method.Name}";
                continue;
            }

            plugin ??= AssemblyLoadContext.GetLoadContext(type.Assembly) is ManagedLoadContext context ? context.Name : null;
            if (listed < MaxCallerFrames)
            {
                var assembly = type.Assembly.GetName().Name;
                callers.Append(listed == 0 ? string.Empty : " <- ").Append(type.Name).Append('.').Append(method.Name);
                if (assembly != lastAssembly)
                    callers.Append(" (").Append(assembly).Append(')');
                lastAssembly = assembly;
                listed++;
            }
            else if (plugin != null)
            {
                break;
            }
        }

        text.Append(plugin == null ? "; no plugin on the stack" : $"; plugin {plugin} on the stack");
        text.Append(drawingPlugin == null ? "; in no plugin's UiBuilder draw" : $"; in the UiBuilder draw of {drawingPlugin}");
        text.Append("; ImGui call: ").Append(binding ?? "none on the stack");
        if (listed > 0)
            text.Append("; callers: ").Append(callers);
        return text.ToString();
    }

    private unsafe void OnImGuiAssert(void* pExpr, void* pFile, int line)
    {
        this.CountSite((nint)pExpr, (nint)pFile, line);

        // Neither shown nor logged: return before any string or closure is built. A null or atom pointer still takes
        // the full path, which logs that it could not be parsed.
        if (!this.ShowAsserts && !this.everShownAssertThisSession && GivesString(pExpr) && GivesString(pFile))
            return;

        this.HandleAssert(pExpr, pFile, line);
    }

    // Runs inside ImGui's native assert, where nothing may unwind; counting is best effort.
    private void CountSite(nint expr, nint file, int line)
    {
        try
        {
            if (this.siteCounter.Count(expr, file, line))
                this.siteCounter.Describe(expr, file, line, DescribeCaller());
        }
        catch (Exception)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe void HandleAssert(void* pExpr, void* pFile, int line)
    {
        var expr = Marshal.PtrToStringAnsi(new IntPtr(pExpr));
        var file = Marshal.PtrToStringAnsi(new IntPtr(pFile));
        if (expr == null || file == null)
        {
            Log.Warning(
                "ImGui assertion failed: {Expr} at {File}:{Line} (failed to parse)",
                expr,
                file,
                line);
            return;
        }

        var key = $"{file}:{line}";
        if (this.ignoredAsserts.Contains(key))
            return;

        // Don't log unless we've ever shown an assert this session
        if (!this.ShowAsserts && !this.everShownAssertThisSession)
            return;

        Lazy<StackTrace> stackTrace = new(GenerateStackTrace);

        if (!this.EnableVerboseLogging)
        {
            if (this.assertCounts.TryGetValue(key, out var count))
            {
                this.assertCounts[key] = count + 1;

                if (count <= HideThreshold || count % HidePrintEvery == 0)
                {
                    Log.Warning(
                        "ImGui assertion failed: {Expr} at {File}:{Line} (repeated {Count} times)",
                        expr,
                        file,
                        line,
                        count);
                }
            }
            else
            {
                this.assertCounts[key] = 1;
            }
        }
        else
        {
            Log.Warning(
                "ImGui assertion failed: {Expr} at {File}:{Line}\n{StackTrace:l}",
                expr,
                file,
                line,
                stackTrace.Value.ToString());
        }

        if (!this.ShowAsserts)
            return;

        this.everShownAssertThisSession = true;

        string? GetRepoUrl()
        {
            // TODO: implot, imguizmo?
            const string userName = "goatcorp";
            const string repoName = "gc-imgui";
            const string branch = "1.88-enhanced-abifix";

            if (!file.Contains("imgui", StringComparison.OrdinalIgnoreCase))
                return null;

            var lastSlash = file.LastIndexOf('\\');
            var fileName = file[(lastSlash + 1)..];
            return $"https://github.com/{userName}/{repoName}/blob/{branch}/{fileName}#L{line}";
        }

        // grab the stack trace now that we've decided to show UI.
        var responsiblePlugin = Service<PluginManager>.GetNullable()?.FindCallingPlugin(stackTrace.Value);
        var responsibleMethodCall = ExtractImguiFunction(stackTrace.Value);

        var gitHubUrl = GetRepoUrl();
        var showOnGitHubButton = new TaskDialogButton
        {
            Text = "Open GitHub",
            AllowCloseDialog = false,
            Enabled = !gitHubUrl.IsNullOrEmpty(),
        };
        showOnGitHubButton.Click += (_, _) =>
        {
            if (!gitHubUrl.IsNullOrEmpty())
                Util.OpenLink(gitHubUrl);
        };

        var breakButton = new TaskDialogButton
        {
            Text = "Break",
            AllowCloseDialog = true,
        };

        var disableButton = new TaskDialogButton
        {
            Text = "Disable for this session",
            AllowCloseDialog = true,
        };

        var ignoreButton = TaskDialogButton.Ignore;

        TaskDialogButton? result = null;

        void DialogThreadStart()
        {
            // TODO(goat): This is probably not gonna work if we showed the loading dialog
            // this session since it already loaded visual styles...
            Application.EnableVisualStyles();

            string text;
            if (responsiblePlugin != null)
            {
                text = $"The plugin \"{responsiblePlugin.Name}\" appears to have caused an ImGui assertion failure. " +
                       $"Please report this problem to the plugin's developer.\n\n";
            }
            else
            {
                text = "Some code in a plugin or Dalamud itself has caused an ImGui assertion failure. " +
                       "Please report this problem in the Dalamud discord.\n\n";
            }

            text += $"You may attempt to continue running the game, but Dalamud UI elements may not work " +
                    $"correctly, or the game may crash after resuming.\n\n";

            if (responsibleMethodCall != null)
            {
                text += $"Assertion failed: {expr} when performing {responsibleMethodCall}\n{file}:{line}";
            }
            else
            {
                text += $"Assertion failed: {expr}\nAt: {file}:{line}";
            }

            var page = new TaskDialogPage
            {
                Heading = "ImGui assertion failed",
                Caption = "Dalamud",
                Expander = new TaskDialogExpander
                {
                    CollapsedButtonText = "Show stack trace",
                    ExpandedButtonText = "Hide stack trace",
                    Text = stackTrace.Value.ToString(),
                },
                Text = text,
                Icon = TaskDialogIcon.Warning,
                Buttons =
                [
                    showOnGitHubButton,
                    breakButton,
                    disableButton,
                    ignoreButton,
                ],
                DefaultButton = showOnGitHubButton,
            };

            result = TaskDialog.ShowDialog(page);
        }

        // Run in a separate thread because of STA and to not mess up other stuff
        var thread = new Thread(DialogThreadStart)
        {
            Name = "Dalamud ImGui Assert Dialog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (result == breakButton)
        {
            Debugger.Break();
        }
        else if (result == disableButton)
        {
        }
        else if (result == ignoreButton)
        {
            this.ignoredAsserts.Add(key);
        }
    }

    private static unsafe class CustomNativeFunctions
    {
        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
#pragma warning disable SA1300
        public static extern void igCustom_SetAssertCallback(void* cb);
#pragma warning restore SA1300
    }
}
