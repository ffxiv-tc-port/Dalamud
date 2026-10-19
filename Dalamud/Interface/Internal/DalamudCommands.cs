using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CheapLoc;
using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Internal.Windows.PluginInstaller;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Utility;
using Serilog;

namespace Dalamud.Interface.Internal;

/// <summary>
/// Class handling Dalamud core commands.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal class DalamudCommands : IServiceType
{
    // One deadline per /xlupdate, shared by the wait for boot plugin loading and the wait for the repositories.
    private static readonly TimeSpan UpdateCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UpdateCommandPollInterval = TimeSpan.FromMilliseconds(250);

    // 1 from /xlupdate until the update has been handed to the installer.
    private int updateCommandRunning;

    // True while that /xlupdate is still waiting for the boot plugin load. Set on the framework thread, cleared by the wait.
    private volatile bool updateCommandWaitingForPlugins;

    [ServiceManager.ServiceConstructor]
    private DalamudCommands(CommandManager commandManager)
    {
        commandManager.AddHandler("/xldclose", new CommandInfo(this.OnUnloadCommand)
        {
            HelpMessage = Loc.Localize("DalamudUnloadHelp", "Unloads XIVLauncher in-game addon. For debug use only!"),
            ShowInHelp = false,
        });

        commandManager.AddHandler("/xlkill", new CommandInfo(this.OnKillCommand)
        {
            HelpMessage = "Kill the game. For debug use only!",
            ShowInHelp = false,
        });

        commandManager.AddHandler("/xlrestart", new CommandInfo(this.OnRestartCommand)
        {
            HelpMessage = "Restart the game. For debug use only!",
            ShowInHelp = false,
        });

        commandManager.AddHandler("/xlhelp", new CommandInfo(this.OnHelpCommand)
        {
            HelpMessage = Loc.Localize("DalamudCmdInfoHelp", "Shows list of commands available. If an argument is provided, shows help for that command."),
        });

        commandManager.AddHandler("/xlmute", new CommandInfo(this.OnBadWordsAddCommand)
        {
            HelpMessage = Loc.Localize("DalamudMuteHelp", "Mute a word or sentence from appearing in chat. Usage: /xlmute <word or sentence>"),
            ShowInHelp = false,
        });

        commandManager.AddHandler("/xlmutelist", new CommandInfo(this.OnBadWordsListCommand)
        {
            HelpMessage = Loc.Localize("DalamudMuteListHelp", "List muted words or sentences."),
        });

        commandManager.AddHandler("/xlunmute", new CommandInfo(this.OnBadWordsRemoveCommand)
        {
            HelpMessage = Loc.Localize("DalamudUnmuteHelp", "Unmute a word or sentence. Usage: /xlunmute <word or sentence>"),
        });

        commandManager.AddHandler("/ll", new CommandInfo(this.OnLastLinkCommand)
        {
            HelpMessage = Loc.Localize("DalamudLastLinkHelp", "Open the last posted link in your default browser."),
        });

        commandManager.AddHandler("/xlbgmset", new CommandInfo(this.OnBgmSetCommand)
        {
            HelpMessage = Loc.Localize("DalamudBgmSetHelp", "Set the Game background music. Usage: /xlbgmset <BGM ID>"),
        });

        commandManager.AddHandler("/xldev", new CommandInfo(this.OnDebugDrawDevMenu)
        {
            HelpMessage = Loc.Localize("DalamudDevMenuHelp", "Draw dev menu DEBUG"),
            ShowInHelp = false,
        });

        commandManager.AddHandler("/xlstats", new CommandInfo(this.OnTogglePluginStats)
        {
            HelpMessage = Loc.Localize("DalamudPluginStats", "Draw plugin statistics window"),
        });

        commandManager.AddHandler("/xlbranch", new CommandInfo(this.OnToggleBranchSwitcher)
        {
            HelpMessage = Loc.Localize("DalamudBranchSwitcher", "Open the branch switcher"),
        });

        commandManager.AddHandler("/xldata", new CommandInfo(this.OnDebugDrawDataMenu)
        {
            HelpMessage = Loc.Localize("DalamudDevDataMenuHelp", "Draw dev data menu DEBUG. Usage: /xldata [Data Dropdown Type]"),
            ShowInHelp = false,
        });

        commandManager.AddHandler("/xllog", new CommandInfo(this.OnOpenLog)
        {
            HelpMessage = Loc.Localize("DalamudDevLogHelp", "Open the plugin log window/console"),
        });

        commandManager.AddHandler("/xlplugins", new CommandInfo(this.OnOpenInstallerCommand)
        {
            HelpMessage = Loc.Localize("DalamudInstallerHelp", "Open the plugin installer"),
        });

        commandManager.AddHandler("/xlupdate", new CommandInfo(this.OnUpdatePluginsCommand)
        {
            HelpMessage = Loc.Localize(
                "DalamudUpdatePluginsHelp",
                "更新所有有新版本的插件（與插件管理員的「更新插件」按鈕相同，不必開啟插件管理員）。"),
        });

        commandManager.AddHandler("/xllanguage", new CommandInfo(this.OnSetLanguageCommand)
        {
            HelpMessage =
                Loc.Localize(
                    "DalamudLanguageHelp",
                    "Set the language for Dalamud and plugins that support it. Available languages: ") +
                Localization.ApplicableLangCodes.Aggregate("en", (current, code) => current + ", " + code),
        });

        commandManager.AddHandler("/xlsettings", new CommandInfo(this.OnOpenSettingsCommand)
        {
            HelpMessage = Loc.Localize(
                "DalamudSettingsHelp",
                "Change various In-Game-Addon settings like chat channels and the discord bot setup."),
        });

        commandManager.AddHandler("/xlversion", new CommandInfo(this.OnVersionInfoCommand)
        {
            HelpMessage = "Dalamud version info",
        });

        commandManager.AddHandler("/xlui", new CommandInfo(this.OnUiCommand)
        {
            HelpMessage = Loc.Localize(
                "DalamudUiModeHelp",
                "Toggle Dalamud UI display modes. Native UI modifications may also be affected by this, but that depends on the plugin."),
        });

        commandManager.AddHandler("/xlprofiler", new CommandInfo(this.OnOpenProfilerCommand)
        {
            HelpMessage = Loc.Localize(
                "DalamudProfilerHelp",
                "Open Dalamud's startup timing profiler."),
        });

        commandManager.AddHandler("/xlcopylog", new CommandInfo(this.OnCopyLogCommand)
        {
            HelpMessage = Loc.Localize(
                "DalamudCopyLogHelp",
                "Copy the dalamud.log file to your clipboard."),
        });
        // Add the new command handler for toggling multi-monitor option
        commandManager.AddHandler("/xltogglemultimonitor", new CommandInfo(this.OnToggleMultiMonitorCommand)
        {
            HelpMessage = Loc.Localize("DalamudToggleMultiMonitorHelp", "Toggle multi-monitor windows."),
        });
    }

    /// <summary>
    /// Polls <paramref name="ready"/> until it is true or <paramref name="deadlineUtc"/> has passed, without blocking a thread.
    /// </summary>
    /// <param name="ready">The condition; read once more after every poll interval.</param>
    /// <param name="deadlineUtc">When to give up.</param>
    /// <param name="pollInterval">How long to wait between reads.</param>
    /// <returns>True when the condition became true, false when the deadline passed first.</returns>
    internal static async Task<bool> WaitUntilAsync(Func<bool> ready, DateTime deadlineUtc, TimeSpan pollInterval)
    {
        while (!ready())
        {
            if (DateTime.UtcNow > deadlineUtc)
                return false;

            await Task.Delay(pollInterval);
        }

        return true;
    }

    private static void ReportUpdateFromCommand(ChatGui chatGui, Task<IEnumerable<PluginUpdateStatus>> task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            chatGui.PrintError(Loc.Localize("DalamudUpdatePluginsFatal", "插件更新失敗：更新程式發生錯誤，詳情見 dalamud.log。"));
            return;
        }

        // The updated plugins are already listed one per line by the installer's own chat output.
        var results = task.Result.ToList();
        var updated = results.Count(x => x.Status == PluginUpdateStatus.StatusKind.Success);
        var failed = results.Where(PluginInstallerWindow.IsUpdateFailure).ToList();

        if (failed.Count > 0)
        {
            var names = string.Join("、", failed.Select(x => $"{x.Name}（{PluginUpdateStatus.LocalizeUpdateStatusKind(x.Status)}）"));
            chatGui.PrintError(string.Format(
                                   Loc.Localize("DalamudUpdatePluginsDoneWithFailures", "插件更新完成：已更新 {0} 個，失敗 {1} 個：{2}"),
                                   updated,
                                   failed.Count,
                                   names));
        }
        else if (updated > 0)
        {
            chatGui.Print(string.Format(Loc.Localize("DalamudUpdatePluginsDone", "插件更新完成：已更新 {0} 個。"), updated));
        }
        else
        {
            chatGui.Print(Loc.Localize("DalamudUpdatePluginsDoneNothing", "插件更新完成：沒有插件需要更新（都已是最新版本）。"));
        }
    }

    private void OnUnloadCommand(string command, string arguments)
    {
        Service<ChatGui>.Get().Print("Unloading...");
        Service<Dalamud>.Get().Unload();
    }

    private void OnKillCommand(string command, string arguments)
    {
        Process.GetCurrentProcess().Kill();
    }

    private void OnRestartCommand(string command, string arguments)
    {
        Dalamud.RestartGame();
    }

    private void OnHelpCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();
        var commandManager = Service<CommandManager>.Get();

        if (arguments.IsNullOrWhitespace())
        {
            chatGui.Print(Loc.Localize("DalamudCmdHelpAvailable", "Available commands:"));
            foreach (var cmd in commandManager.Commands.OrderBy(cInfo => cInfo.Key))
            {
                if (!cmd.Value.ShowInHelp)
                    continue;

                chatGui.Print($"{cmd.Key}: {cmd.Value.HelpMessage}");
            }
        }
        else
        {
            var trimmedArguments = arguments.Trim();
            var targetCommandText = trimmedArguments[0] == '/' ? trimmedArguments : $"/{trimmedArguments}";
            chatGui.Print(commandManager.Commands.TryGetValue(targetCommandText, out var targetCommand)
                              ? $"{targetCommandText}: {targetCommand.HelpMessage}"
                              : Loc.Localize("DalamudCmdHelpNotFound", "Command not found."));
        }
    }

    private void OnBadWordsAddCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();

        chatGui.PrintError(Loc.Localize("DalamudMuteListDeprecation", "The built-in Dalamud bad word list is deprecated and no new words can be added. " +
                                                                      "Please use a plugin for this functionality."));
    }

    private void OnBadWordsListCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();
        var configuration = Service<DalamudConfiguration>.Get();

        configuration.BadWords ??= new List<string>();

        if (configuration.BadWords.Count == 0)
        {
            chatGui.Print(Loc.Localize("DalamudNoneMuted", "No muted words or sentences."));
            return;
        }

        configuration.QueueSave();

        foreach (var word in configuration.BadWords)
            chatGui.Print($"\"{word}\"");
    }

    private void OnBadWordsRemoveCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();
        var configuration = Service<DalamudConfiguration>.Get();

        configuration.BadWords ??= new List<string>();

        configuration.BadWords.RemoveAll(x => x == arguments);

        configuration.QueueSave();

        chatGui.Print(string.Format(Loc.Localize("DalamudUnmuted", "Unmuted \"{0}\"."), arguments));
    }

    private void OnLastLinkCommand(string command, string arguments)
    {
        var chatHandlers = Service<ChatHandlers>.Get();
        var chatGui = Service<ChatGui>.Get();

        if (string.IsNullOrEmpty(chatHandlers.LastLink))
        {
            chatGui.Print(Loc.Localize("DalamudNoLastLink", "No last link..."));
            return;
        }

        chatGui.Print(string.Format(Loc.Localize("DalamudOpeningLink", "Opening {0}"), chatHandlers.LastLink));
        Process.Start(new ProcessStartInfo(chatHandlers.LastLink)
        {
            UseShellExecute = true,
        });
    }

    private void OnBgmSetCommand(string command, string arguments)
    {
        var gameGui = Service<GameGui>.Get();

        if (ushort.TryParse(arguments, out var value))
        {
            gameGui.SetBgm(value);
        }
        else
        {
            // Revert to the original BGM by specifying an invalid one
            gameGui.ResetBgm();
        }
    }

    private void OnDebugDrawDevMenu(string command, string arguments)
    {
        Service<DalamudInterface>.Get().ToggleDevMenu();
    }

    private void OnTogglePluginStats(string command, string arguments)
    {
        Service<DalamudInterface>.Get().TogglePluginStatsWindow();
    }

    private void OnToggleBranchSwitcher(string command, string arguments)
    {
        Service<DalamudInterface>.Get().ToggleBranchSwitcher();
    }

    private void OnDebugDrawDataMenu(string command, string arguments)
    {
        var dalamudInterface = Service<DalamudInterface>.Get();

        if (string.IsNullOrEmpty(arguments))
            dalamudInterface.ToggleDataWindow();
        else
            dalamudInterface.ToggleDataWindow(arguments);
    }

    private void OnOpenLog(string command, string arguments)
    {
        Service<DalamudInterface>.Get().ToggleLogWindow();
    }

    private void OnVersionInfoCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();

        chatGui.Print(new SeStringBuilder()
                      .AddItalics("Dalamud:")
                      .AddText($" {Util.GetScmVersion()}")
                      .Build());

        chatGui.Print(new SeStringBuilder()
                      .AddItalics("FFXIVCS:")
                      .AddText($" {Util.GetGitHashClientStructs()}")
                      .Build());
    }

    private void OnOpenInstallerCommand(string command, string arguments)
    {
        var configuration = Service<DalamudConfiguration>.Get();
        Service<DalamudInterface>.Get().TogglePluginInstallerWindowTo(configuration.PluginInstallerOpen);
    }

    private void OnUpdatePluginsCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();
        var pluginManager = Service<PluginManager>.Get();

        if (pluginManager.SafeMode)
        {
            chatGui.PrintError(Loc.Localize("DalamudUpdatePluginsSafeMode", "安全模式下無法更新插件。"));
            return;
        }

        // Still loading from boot: take the command now and hand it on once loading is done, like the repository wait.
        var waitForPlugins = !pluginManager.PluginsReady;

        if ((!waitForPlugins && pluginManager.IsAnyPluginOperationInFlight) ||
            Interlocked.CompareExchange(ref this.updateCommandRunning, 1, 0) != 0)
        {
            if (this.updateCommandWaitingForPlugins)
            {
                Log.Information("/xlupdate: pressed again while waiting for plugins to load, not queued");
                chatGui.Print(Loc.Localize("DalamudUpdatePluginsAlreadyWaiting", "已經在等插件載入，載入完會自動開始更新，不必再按。"));
                return;
            }

            chatGui.Print(Loc.Localize("DalamudUpdatePluginsBusy", "已有插件正在安裝或更新，等它完成後再試。"));
            return;
        }

        if (waitForPlugins)
        {
            this.updateCommandWaitingForPlugins = true;
            Log.Information("/xlupdate: plugins still loading, waiting up to {Timeout} before updating", UpdateCommandTimeout);
            chatGui.Print(Loc.Localize("DalamudUpdatePluginsWaitingForLoad", "插件還在載入，載入完會自動開始更新。"));
        }
        else
        {
            chatGui.Print(Loc.Localize("DalamudUpdatePluginsChecking", "正在檢查插件更新…"));
        }

        _ = this.UpdatePluginsFromCommandAsync(chatGui, pluginManager, waitForPlugins);
    }

    // Runs its first part on the framework thread; never throws.
    private async Task UpdatePluginsFromCommandAsync(ChatGui chatGui, PluginManager pluginManager, bool waitForPlugins)
    {
        try
        {
            var deadline = DateTime.UtcNow + UpdateCommandTimeout;

            if (waitForPlugins)
            {
                var waitStart = DateTime.UtcNow;
                var loaded = await WaitUntilAsync(() => pluginManager.PluginsReady, deadline, UpdateCommandPollInterval);
                this.updateCommandWaitingForPlugins = false;

                if (!loaded)
                {
                    Log.Information("/xlupdate: plugins still loading after {Timeout}, nothing updated", UpdateCommandTimeout);
                    chatGui.PrintError(string.Format(
                                           Loc.Localize("DalamudUpdatePluginsLoadTimeout", "等了 {0} 分鐘插件仍未載入完成，沒有更新任何插件。"),
                                           UpdateCommandTimeout.TotalMinutes));
                    return;
                }

                Log.Information("/xlupdate: plugins finished loading after {Seconds:0.0} s, checking for updates", (DateTime.UtcNow - waitStart).TotalSeconds);
                chatGui.Print(Loc.Localize("DalamudUpdatePluginsChecking", "正在檢查插件更新…"));
            }

            // Opening the installer reloads the repositories before "update all" is pressed, so do the same; a reload
            // that is already running is waited for instead of starting a second one next to it.
            if (pluginManager.ReposReady)
            {
                await pluginManager.ReloadPluginMastersAsync();
            }
            else if (!await WaitUntilAsync(() => pluginManager.ReposReady, deadline, UpdateCommandPollInterval))
            {
                Log.Information("/xlupdate: repositories still loading after {Timeout}, nothing updated", UpdateCommandTimeout);
                chatGui.PrintError(Loc.Localize("DalamudUpdatePluginsRepoTimeout", "插件倉庫讀取逾時，沒有更新任何插件。"));
                return;
            }

            var framework = Service<Framework>.Get();
            await framework.RunOnFrameworkThread(() => StartUpdateFromCommand(chatGui, pluginManager, framework));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "/xlupdate could not start the plugin update");
            chatGui.PrintError(Loc.Localize("DalamudUpdatePluginsFatal", "插件更新失敗：更新程式發生錯誤，詳情見 dalamud.log。"));
        }
        finally
        {
            // From here on the installer's own state blocks a second round.
            this.updateCommandWaitingForPlugins = false;
            Volatile.Write(ref this.updateCommandRunning, 0);
        }

        static void StartUpdateFromCommand(ChatGui chatGui, PluginManager pluginManager, Framework framework)
        {
            if (framework.IsFrameworkUnloading)
                return;

            var repoWarning = pluginManager.Repos.Any(x => x.State == PluginRepositoryState.Fail)
                                  ? Loc.Localize("DalamudUpdatePluginsRepoFailed", "（有插件倉庫讀取失敗，清單可能不完整，詳情見 dalamud.log）")
                                  : string.Empty;

            var result = Service<DalamudInterface>.Get().TryStartPluginUpdateAll(
                task => ReportUpdateFromCommand(chatGui, task),
                out var count);
            Log.Information("/xlupdate: {Result}, {Count} plugins, repo failure: {RepoFailed}", result, count, repoWarning.Length > 0);

            switch (result)
            {
                case PluginInstallerWindow.UpdateAllRequestResult.Started:
                    chatGui.Print(string.Format(Loc.Localize("DalamudUpdatePluginsStarted", "開始更新 {0} 個插件…"), count) + repoWarning);
                    break;
                case PluginInstallerWindow.UpdateAllRequestResult.NothingToUpdate:
                    chatGui.Print(Loc.Localize("DalamudUpdatePluginsNothing", "沒有可更新的插件。") + repoWarning);
                    break;
                case PluginInstallerWindow.UpdateAllRequestResult.Busy:
                    chatGui.Print(Loc.Localize("DalamudUpdatePluginsBusy", "已有插件正在安裝或更新，等它完成後再試。"));
                    break;
                case PluginInstallerWindow.UpdateAllRequestResult.NotReady:
                    chatGui.Print(Loc.Localize("DalamudUpdatePluginsNotReady", "插件還在載入中，請稍後再試。"));
                    break;
                case PluginInstallerWindow.UpdateAllRequestResult.SafeMode:
                    chatGui.PrintError(Loc.Localize("DalamudUpdatePluginsSafeMode", "安全模式下無法更新插件。"));
                    break;
            }
        }
    }

    private void OnSetLanguageCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();
        var configuration = Service<DalamudConfiguration>.Get();
        var localization = Service<Localization>.Get();

        if (Localization.ApplicableLangCodes.Contains(arguments.ToLowerInvariant()) || arguments.ToLowerInvariant() == "en")
        {
            localization.SetupWithLangCode(arguments.ToLowerInvariant());
            configuration.LanguageOverride = arguments.ToLowerInvariant();

            chatGui.Print(string.Format(Loc.Localize("DalamudLanguageSetTo", "Language set to {0}"), arguments));
        }
        else
        {
            localization.SetupWithUiCulture();
            configuration.LanguageOverride = null;

            chatGui.Print(string.Format(Loc.Localize("DalamudLanguageSetTo", "Language set to {0}"), "default"));
        }

        configuration.QueueSave();
    }

    private void OnOpenSettingsCommand(string command, string arguments)
    {
        Service<DalamudInterface>.Get().ToggleSettingsWindow();
    }

    private void OnUiCommand(string command, string arguments)
    {
        var im = Service<InterfaceManager>.Get();

        im.IsDispatchingEvents = arguments switch
        {
            "show" => true,
            "hide" => false,
            _ => !im.IsDispatchingEvents,
        };

        var pm = Service<PluginManager>.Get();

        foreach (var plugin in pm.InstalledPlugins)
        {
            if (im.IsDispatchingEvents)
            {
                plugin.DalamudInterface?.LocalUiBuilder.NotifyShowUi();
            }
            else
            {
                plugin.DalamudInterface?.LocalUiBuilder.NotifyHideUi();
            }
        }
    }

    private void OnOpenProfilerCommand(string command, string arguments)
    {
        Service<DalamudInterface>.Get().ToggleProfilerWindow();
    }

    private void OnCopyLogCommand(string command, string arguments)
    {
        var chatGui = Service<ChatGui>.Get();
        var logDir = Service<Dalamud>.Get().StartInfo.LogPath ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherTC");

        // The live log rolls forward into dalamud_001.log, dalamud_002.log, ... once it exceeds its
        // size limit, so the newest data is not necessarily in the file named "dalamud.log".
        var logPath = Path.Join(logDir, "dalamud.log");
        try
        {
            var newest = new DirectoryInfo(logDir)
                         .GetFiles("dalamud*.log")
                         .Where(x => Regex.IsMatch(x.Name, @"^dalamud(_[0-9]{3,})?\.log$", RegexOptions.IgnoreCase))
                         .MaxBy(x => x.LastWriteTimeUtc);
            if (newest is not null)
                logPath = newest.FullName;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not resolve the newest log file, falling back to {Path}", logPath);
        }

        var message = Util.CopyFilesToClipboard([logPath])
                          ? Loc.Localize("DalamudLogCopySuccess", "Log file copied to clipboard.")
                          : Loc.Localize("DalamudLogCopyFailure", "Could not copy log file to clipboard.");
        chatGui.Print(message);
    }

    private void OnToggleMultiMonitorCommand(string command, string arguments)
    {
        var configuration = Service<DalamudConfiguration>.Get();
        var chatGui = Service<ChatGui>.Get();

        configuration.IsDisableViewport = !configuration.IsDisableViewport;
        configuration.QueueSave();

        var message = configuration.IsDisableViewport
            ? Loc.Localize("DalamudMultiMonitorDisabled", "Multi-monitor windows disabled.")
            : Loc.Localize("DalamudMultiMonitorEnabled", "Multi-monitor windows enabled.");

        chatGui.Print(message);
    }
}
