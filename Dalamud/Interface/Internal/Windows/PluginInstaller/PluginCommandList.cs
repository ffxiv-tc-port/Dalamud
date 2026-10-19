using System.Collections.Generic;

using Dalamud.Game.Command;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// The commands one plugin registered, in the order the installer lists them: by display order, then by command
/// (compared like <c>OrderBy</c> compares strings), ties in the order the command table lists them. Draw thread only.
/// </summary>
internal sealed class PluginCommandList
{
    private static readonly Comparison<Entry> InListOrder = static (a, b) =>
    {
        var c = a.Info.DisplayOrder.CompareTo(b.Info.DisplayOrder);
        if (c == 0)
            c = Comparer<string>.Default.Compare(a.Command, b.Command);
        return c != 0 ? c : a.TableOrder.CompareTo(b.TableOrder);
    };

    private readonly List<Entry> entries = [];
    private int changeCount = -1;
    private string? internalName;

    /// <summary>
    /// The plugin's commands, shown in help or not, sorted. Read again only after a command was added or removed;
    /// the sort runs every time because a command's display order can be changed at any time.
    /// </summary>
    /// <param name="manager">The command manager.</param>
    /// <param name="pluginInternalName">The plugin's internal name.</param>
    /// <returns>The commands; the same list every call.</returns>
    public List<Entry> Get(CommandManager manager, string pluginInternalName)
    {
        // Read before the table: a change while it is read makes the next call read it again.
        var changes = manager.ChangeCount;
        if (changes != this.changeCount || !ReferenceEquals(pluginInternalName, this.internalName))
        {
            this.entries.Clear();
            var order = 0;
            foreach (var (command, info) in manager.Commands)
            {
                if (manager.GetHandlerAssemblyName(command, info) == pluginInternalName)
                    this.entries.Add(new Entry(command, info, order));
                order++;
            }

            this.changeCount = changes;
            this.internalName = pluginInternalName;
        }

        this.entries.Sort(InListOrder);
        return this.entries;
    }

    /// <summary>One registered command.</summary>
    /// <param name="Command">The command.</param>
    /// <param name="Info">Its info, read live (help text, visibility and display order can change).</param>
    /// <param name="TableOrder">Its position in the command table when it was read.</param>
    internal readonly record struct Entry(string Command, IReadOnlyCommandInfo Info, int TableOrder);
}
