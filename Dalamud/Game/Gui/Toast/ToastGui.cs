using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.UI;

using Serilog;

namespace Dalamud.Game.Gui.Toast;

/// <summary>
/// This class facilitates interacting with and creating native toast windows.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal sealed partial class ToastGui : IInternalDisposableService, IToastGui
{
    private const uint QuestToastCheckmarkMagic = 60081;

    private static readonly DetourFault NormalToastFault = new("ToastGui.ShowNormal", "顯示畫面提示（插件攔截或修改）");

    private static readonly DetourFault QuestToastFault = new("ToastGui.ShowQuest", "顯示任務提示（插件攔截或修改）");

    private static readonly DetourFault ErrorToastFault = new("ToastGui.ShowError", "顯示錯誤提示（插件攔截或修改）");

    private readonly ConcurrentQueue<(byte[] Message, ToastOptions Options)> normalQueue = new();
    private readonly ConcurrentQueue<(byte[] Message, QuestToastOptions Options)> questQueue = new();
    private readonly ConcurrentQueue<byte[]> errorQueue = new();

    // Null when the slot is missing or misplaced (see FindHookTargets): that kind of toast is neither seen nor shown.
    private readonly Hook<ShowNormalToastDelegate>? showNormalToastHook;
    private readonly Hook<ShowQuestToastDelegate>? showQuestToastHook;
    private readonly Hook<ShowErrorToastDelegate>? showErrorToastHook;

    // Framework thread only: the kinds whose dropped toasts were already logged.
    private readonly HashSet<string> dropLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ToastGui"/> class.
    /// </summary>
    /// <param name="sigScanner">The game's scanner, for where the game's sections are.</param>
    [ServiceManager.ServiceConstructor]
    private unsafe ToastGui(TargetSigScanner sigScanner)
    {
        var targets = HookTargets.For(nameof(ToastGui), sigScanner);
        var (normal, quest, error) = FindHookTargets(targets);
        this.showNormalToastHook = normal != 0 ? Hook<ShowNormalToastDelegate>.FromAddress(normal, this.HandleNormalToastDetour) : null;
        this.showQuestToastHook = quest != 0 ? Hook<ShowQuestToastDelegate>.FromAddress(quest, this.HandleQuestToastDetour) : null;
        this.showErrorToastHook = error != 0 ? Hook<ShowErrorToastDelegate>.FromAddress(error, this.HandleErrorToastDetour) : null;
        targets.ReportDisabled();

        this.showNormalToastHook?.Enable();
        this.showQuestToastHook?.Enable();
        this.showErrorToastHook?.Enable();
    }

    #region Marshal delegates

    private unsafe delegate void ShowNormalToastDelegate(UIModule* thisPtr, byte* text, int layer, byte isTop, byte isFast, uint logMessageId);

    private unsafe delegate void ShowQuestToastDelegate(UIModule* thisPtr, int position, byte* text, uint iconOrCheck1, byte playSound, uint iconOrCheck2, byte alsoPlaySound);

    private unsafe delegate void ShowErrorToastDelegate(UIModule* thisPtr, byte* text, byte respectsHidingMaybe);

    #endregion

    #region Events

    /// <inheritdoc/>
    public event IToastGui.OnNormalToastDelegate? Toast;

    /// <inheritdoc/>
    public event IToastGui.OnQuestToastDelegate? QuestToast;

    /// <inheritdoc/>
    public event IToastGui.OnErrorToastDelegate? ErrorToast;

    #endregion

    /// <summary>
    /// Disposes of managed and unmanaged resources.
    /// </summary>
    void IInternalDisposableService.DisposeService()
    {
        this.showNormalToastHook?.Dispose();
        this.showQuestToastHook?.Dispose();
        this.showErrorToastHook?.Dispose();
    }

    /// <summary>
    /// Checks the UIModule vtable slots this service hooks.
    /// </summary>
    /// <param name="targets">The checks.</param>
    /// <returns>ShowWideText, ShowText and ShowErrorText, 0 where left out.</returns>
    internal static unsafe (nint Normal, nint Quest, nint Error) FindHookTargets(HookTargets targets)
    {
        var uiModule = targets.VirtualTable(UIModule.StaticVirtualTablePointer);
        return (
            targets.Slot("UIModule.ShowWideText", "畫面中央提示（插件攔截或顯示）", uiModule, uiModule == null ? 0 : (nint)uiModule->ShowWideText),
            targets.Slot("UIModule.ShowText", "任務提示（插件攔截或顯示）", uiModule, uiModule == null ? 0 : (nint)uiModule->ShowText),
            targets.Slot("UIModule.ShowErrorText", "錯誤提示（插件攔截或顯示）", uiModule, uiModule == null ? 0 : (nint)uiModule->ShowErrorText));
    }

    /// <summary>
    /// Process the toast queue.
    /// </summary>
    internal void UpdateQueue()
    {
        while (this.normalQueue.TryDequeue(out var normal))
        {
            this.ShowNormal(normal.Message, normal.Options);
        }

        while (this.questQueue.TryDequeue(out var quest))
        {
            this.ShowQuest(quest.Message, quest.Options);
        }

        while (this.errorQueue.TryDequeue(out var error))
        {
            this.ShowError(error);
        }
    }

    // Toasts are shown through the hook's Original; without it they are dropped.
    private bool CanShow(object? hook, string kind)
    {
        if (hook != null)
            return true;

        if (this.dropLogged.Add(kind))
            Log.Warning("{Kind} toasts queued by Dalamud and plugins are dropped this session: the hook is not installed", kind);
        return false;
    }
}

/// <summary>
/// Handles normal toasts.
/// </summary>
internal sealed partial class ToastGui
{
    /// <inheritdoc/>
    public void ShowNormal(string message, ToastOptions? options = null)
    {
        options ??= new ToastOptions();
        this.normalQueue.Enqueue((Encoding.UTF8.GetBytes(message), options));
    }

    /// <inheritdoc/>
    public void ShowNormal(SeString message, ToastOptions? options = null)
    {
        options ??= new ToastOptions();
        this.normalQueue.Enqueue((message.Encode(), options));
    }

    private unsafe void ShowNormal(byte[] bytes, ToastOptions? options = null)
    {
        if (!this.CanShow(this.showNormalToastHook, "Normal"))
            return;

        options ??= new ToastOptions();

        fixed (byte* ptr = bytes.NullTerminate())
        {
            this.HandleNormalToastDetour(
                UIModule.Instance(),
                ptr,
                5,
                (byte)options.Position,
                (byte)options.Speed,
                0);
        }
    }

    // Original is called exactly once, outside the try block, unless there is no text or a handler took the toast.
    private unsafe void HandleNormalToastDetour(UIModule* thisPtr, byte* text, int layer, byte isTop, byte isFast, uint logMessageId)
    {
        if (text == null)
            return;

        byte[]? encoded = null;
        byte top = 0, fast = 0;
        try
        {
            // call events
            var isHandled = false;
            var str = MemoryHelper.ReadSeStringNullTerminated((nint)text);
            var options = new ToastOptions
            {
                Position = (ToastPosition)isTop,
                Speed = (ToastSpeed)isFast,
            };

            foreach (var d in Delegate.EnumerateInvocationList(this.Toast))
            {
                try
                {
                    d.Invoke(ref str, ref options, ref isHandled);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception during raise of {handler}", d.Method);
                }
            }

            // do nothing if handled
            if (isHandled)
                return;

            encoded = str.EncodeWithNullTerminator();
            top = (byte)(options.Position == ToastPosition.Top ? 1 : 0);
            fast = (byte)(options.Speed == ToastSpeed.Fast ? 1 : 0);
        }
        catch (Exception ex)
        {
            NormalToastFault.Report(ex);
            encoded = null;
        }

        if (encoded == null)
        {
            this.showNormalToastHook!.Original(thisPtr, text, layer, isTop, isFast, logMessageId);
            return;
        }

        fixed (byte* ptr = encoded)
        {
            this.showNormalToastHook!.Original(thisPtr, ptr, layer, top, fast, logMessageId);
        }
    }
}

/// <summary>
/// Handles quest toasts.
/// </summary>
internal sealed partial class ToastGui
{
    /// <inheritdoc/>
    public void ShowQuest(string message, QuestToastOptions? options = null)
    {
        options ??= new QuestToastOptions();
        this.questQueue.Enqueue((Encoding.UTF8.GetBytes(message), options));
    }

    /// <inheritdoc/>
    public void ShowQuest(SeString message, QuestToastOptions? options = null)
    {
        options ??= new QuestToastOptions();
        this.questQueue.Enqueue((message.Encode(), options));
    }

    private unsafe void ShowQuest(byte[] bytes, QuestToastOptions? options = null)
    {
        if (!this.CanShow(this.showQuestToastHook, "Quest"))
            return;

        options ??= new QuestToastOptions();

        var (ioc1, ioc2) = this.DetermineParameterOrder(options);

        fixed (byte* ptr = bytes.NullTerminate())
        {
            this.HandleQuestToastDetour(
                UIModule.Instance(),
                (int)options.Position,
                ptr,
                ioc1,
                (byte)(options.PlaySound ? 1 : 0),
                ioc2,
                0);
        }
    }

    // Original is called exactly once, outside the try block, unless there is no text or a handler took the toast.
    private unsafe void HandleQuestToastDetour(UIModule* thisPtr, int position, byte* text, uint iconOrCheck1, byte playSound, uint iconOrCheck2, byte alsoPlaySound)
    {
        if (text == null)
            return;

        byte[]? encoded = null;
        UIModule* target = null;
        int newPosition = 0;
        uint ioc1 = 0, ioc2 = 0;
        byte sound = 0;
        try
        {
            // call events
            var isHandled = false;
            var str = SeString.Parse(text);
            var options = new QuestToastOptions
            {
                Position = (QuestToastPosition)position,
                DisplayCheckmark = iconOrCheck1 == QuestToastCheckmarkMagic,
                IconId = iconOrCheck1 == QuestToastCheckmarkMagic ? iconOrCheck2 : iconOrCheck1,
                PlaySound = playSound == 1,
            };

            foreach (var d in Delegate.EnumerateInvocationList(this.QuestToast))
            {
                try
                {
                    d.Invoke(ref str, ref options, ref isHandled);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception during raise of {handler}", d.Method);
                }
            }

            // do nothing if handled
            if (isHandled)
                return;

            encoded = str.EncodeWithNullTerminator();
            (ioc1, ioc2) = this.DetermineParameterOrder(options);
            newPosition = (int)options.Position;
            sound = (byte)(options.PlaySound ? 1 : 0);

            // As before, the module is looked up here; if that throws, the game's own call goes through below.
            target = UIModule.Instance();
        }
        catch (Exception ex)
        {
            QuestToastFault.Report(ex);
            encoded = null;
        }

        if (encoded == null)
        {
            this.showQuestToastHook!.Original(thisPtr, position, text, iconOrCheck1, playSound, iconOrCheck2, alsoPlaySound);
            return;
        }

        fixed (byte* ptr = encoded)
        {
            this.showQuestToastHook!.Original(target, newPosition, ptr, ioc1, sound, ioc2, 0);
        }
    }

    private (uint IconOrCheck1, uint IconOrCheck2) DetermineParameterOrder(QuestToastOptions options)
    {
        return options.DisplayCheckmark
                   ? (QuestToastCheckmarkMagic, options.IconId)
                   : (options.IconId, 0);
    }
}

/// <summary>
/// Handles error toasts.
/// </summary>
internal sealed partial class ToastGui
{
    /// <inheritdoc/>
    public void ShowError(string message)
    {
        this.errorQueue.Enqueue(Encoding.UTF8.GetBytes(message));
    }

    /// <inheritdoc/>
    public void ShowError(SeString message)
    {
        this.errorQueue.Enqueue(message.Encode());
    }

    private unsafe void ShowError(byte[] bytes)
    {
        if (!this.CanShow(this.showErrorToastHook, "Error"))
            return;

        fixed (byte* ptr = bytes.NullTerminate())
        {
            this.HandleErrorToastDetour(UIModule.Instance(), ptr, 0);
        }
    }

    // Original is called exactly once, outside the try block, unless there is no text or a handler took the toast.
    private unsafe void HandleErrorToastDetour(UIModule* thisPtr, byte* text, byte respectsHidingMaybe)
    {
        if (text == null)
            return;

        byte[]? encoded = null;
        try
        {
            // call events
            var isHandled = false;
            var str = SeString.Parse(text);

            foreach (var d in Delegate.EnumerateInvocationList(this.ErrorToast))
            {
                try
                {
                    d.Invoke(ref str, ref isHandled);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception during raise of {handler}", d.Method);
                }
            }

            // do nothing if handled
            if (isHandled)
                return;

            encoded = str.EncodeWithNullTerminator();
        }
        catch (Exception ex)
        {
            ErrorToastFault.Report(ex);
            encoded = null;
        }

        if (encoded == null)
        {
            this.showErrorToastHook!.Original(thisPtr, text, respectsHidingMaybe);
            return;
        }

        fixed (byte* ptr = encoded)
        {
            this.showErrorToastHook!.Original(thisPtr, ptr, respectsHidingMaybe);
        }
    }
}

/// <summary>
/// Plugin scoped version of ToastGui.
/// </summary>
[PluginInterface]
[ServiceManager.ScopedService]
#pragma warning disable SA1015
[ResolveVia<IToastGui>]
#pragma warning restore SA1015
internal class ToastGuiPluginScoped : IInternalDisposableService, IToastGui
{
    [ServiceManager.ServiceDependency]
    private readonly ToastGui toastGuiService = Service<ToastGui>.Get();

    /// <summary>
    /// Initializes a new instance of the <see cref="ToastGuiPluginScoped"/> class.
    /// </summary>
    internal ToastGuiPluginScoped()
    {
        this.toastGuiService.Toast += this.ToastForward;
        this.toastGuiService.QuestToast += this.QuestToastForward;
        this.toastGuiService.ErrorToast += this.ErrorToastForward;
    }

    /// <inheritdoc/>
    public event IToastGui.OnNormalToastDelegate? Toast;

    /// <inheritdoc/>
    public event IToastGui.OnQuestToastDelegate? QuestToast;

    /// <inheritdoc/>
    public event IToastGui.OnErrorToastDelegate? ErrorToast;

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.toastGuiService.Toast -= this.ToastForward;
        this.toastGuiService.QuestToast -= this.QuestToastForward;
        this.toastGuiService.ErrorToast -= this.ErrorToastForward;

        this.Toast = null;
        this.QuestToast = null;
        this.ErrorToast = null;
    }

    /// <inheritdoc/>
    public void ShowNormal(string message, ToastOptions? options = null) => this.toastGuiService.ShowNormal(message, options);

    /// <inheritdoc/>
    public void ShowNormal(SeString message, ToastOptions? options = null) => this.toastGuiService.ShowNormal(message, options);

    /// <inheritdoc/>
    public void ShowQuest(string message, QuestToastOptions? options = null) => this.toastGuiService.ShowQuest(message, options);

    /// <inheritdoc/>
    public void ShowQuest(SeString message, QuestToastOptions? options = null) => this.toastGuiService.ShowQuest(message, options);

    /// <inheritdoc/>
    public void ShowError(string message) => this.toastGuiService.ShowError(message);

    /// <inheritdoc/>
    public void ShowError(SeString message) => this.toastGuiService.ShowError(message);

    private void ToastForward(ref SeString message, ref ToastOptions options, ref bool isHandled)
        => this.Toast?.Invoke(ref message, ref options, ref isHandled);

    private void QuestToastForward(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
        => this.QuestToast?.Invoke(ref message, ref options, ref isHandled);

    private void ErrorToastForward(ref SeString message, ref bool isHandled)
        => this.ErrorToast?.Invoke(ref message, ref isHandled);
}
