using System.Collections.Generic;
using System.Linq;

using Dalamud.Logging.Internal;

using InteropGenerator.Runtime;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Reports which FFXIVClientStructs addresses the resolver could not find in this client.
/// </summary>
/// <remarks>
/// Names are written as <c>Type.Member</c>, the same form CS uses in its "Address for X is null" exceptions.
/// </remarks>
internal static class ClientStructsAddressReport
{
    /// <summary>The <see cref="FeatureDisabledEcho"/> key of the chat notice.</summary>
    internal const string EchoKey = "ClientStructs.Unresolved";

    private static readonly ModuleLog Log = new("CSADDR");

    /// <summary>
    /// Gets the short names of the addresses that were left unresolved, sorted ordinally.
    /// </summary>
    /// <param name="addresses">The registered addresses.</param>
    /// <returns>The names.</returns>
    internal static List<string> CollectUnresolved(IEnumerable<Address> addresses)
    {
        var names = addresses.Where(a => a.Value == 0).Select(a => ShortName(a.Name)).ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Turns <c>Namespace.Outer+Type.Member</c> into <c>Type.Member</c>.
    /// </summary>
    /// <param name="name">The full address name.</param>
    /// <returns>The short name.</returns>
    internal static string ShortName(string name)
    {
        var memberDot = name.LastIndexOf('.');
        if (memberDot <= 0)
            return name;

        var typeStart = name.LastIndexOfAny(['.', '+'], memberDot - 1) + 1;
        return name[typeStart..];
    }

    /// <summary>
    /// Builds the log line.
    /// </summary>
    /// <param name="unresolved">Short names of the unresolved addresses.</param>
    /// <param name="total">How many addresses are registered.</param>
    /// <returns>The line.</returns>
    internal static string FormatLogLine(IReadOnlyCollection<string> unresolved, int total)
        => unresolved.Count == 0
               ? $"0 of {total} ClientStructs addresses are unresolved"
               : $"{unresolved.Count} of {total} ClientStructs addresses are unresolved: {string.Join(", ", unresolved)}";

    /// <summary>
    /// Builds the chat line.
    /// </summary>
    /// <param name="count">How many addresses are unresolved.</param>
    /// <returns>The line.</returns>
    internal static string FormatChatLine(int count)
        => $"Dalamud：遊戲改版後有 {count} 個遊戲位址找不到，用到它們的 Dalamud 與插件功能會停用或出錯，請等更新。名單請輸入 /xllog 查看。";

    /// <summary>
    /// Writes one Information line (also when nothing is missing) and, if anything is missing, queues one chat line
    /// for after login. Never throws.
    /// </summary>
    /// <param name="addresses">The registered addresses, after resolving.</param>
    internal static void Report(IReadOnlyCollection<Address> addresses)
    {
        try
        {
            var unresolved = CollectUnresolved(addresses);
            Log.Information("{Line:l}", FormatLogLine(unresolved, addresses.Count));

            if (unresolved.Count != 0)
                FeatureDisabledEcho.Report(EchoKey, FormatChatLine(unresolved.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not report unresolved ClientStructs addresses");
        }
    }
}
