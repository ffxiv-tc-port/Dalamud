using System.Collections.Generic;
using System.Reflection;

namespace Dalamud.Utility.Internal;

/// <summary>
/// The message shown when Dalamud's services fail to start.
/// </summary>
/// <remarks>
/// Names each root cause by its first line only; the full exception goes to the log.
/// </remarks>
internal static class ServiceFailureNotice
{
    /// <summary>The message box title.</summary>
    internal const string Caption = "台服 Dalamud：服務載入失敗";

    /// <summary>Most causes listed.</summary>
    internal const int MaxCauses = 6;

    /// <summary>
    /// Builds the message.
    /// </summary>
    /// <param name="exception">Why the services failed, if known.</param>
    /// <returns>The message box text.</returns>
    internal static string Format(Exception? exception)
    {
        List<string> causes = exception == null ? [] : Causes(exception);
        var listed = causes.Count == 0
                         ? string.Empty
                         : "\n失敗原因（完整內容在 dalamud.log）：\n" + string.Concat(causes.ConvertAll(c => $"・{c}\n"));

        return "台服 Dalamud 有服務載入失敗，這次不載入 Dalamud 與所有插件。\n" +
               "遊戲照常運作，只是這次沒有插件。通常是遊戲改版、Dalamud 還沒跟上所致；請等 Dalamud 更新後重新啟動遊戲。\n" +
               listed +
               "\nDalamud failed to load all necessary services.\n" +
               "The game will continue, but you may not be able to use plugins.";
    }

    /// <summary>
    /// Lists the root causes, wrappers peeled off, one line each, without duplicates.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>At most <see cref="MaxCauses"/> lines like <c>Type: first line of the message</c>.</returns>
    internal static List<string> Causes(Exception exception)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0 && lines.Count < MaxCauses)
        {
            var current = pending.Pop();
            switch (current)
            {
                case AggregateException aggregate:
                    for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                        pending.Push(aggregate.InnerExceptions[i]);
                    continue;
                case TargetInvocationException { InnerException: { } inner }:
                    pending.Push(inner);
                    continue;
                case TypeInitializationException { InnerException: { } inner }:
                    pending.Push(inner);
                    continue;
            }

            var message = current.Message ?? string.Empty;
            var lineEnd = message.IndexOfAny(['\r', '\n']);
            var line = $"{current.GetType().Name}: {(lineEnd >= 0 ? message[..lineEnd] : message).Trim()}";
            if (seen.Add(line))
                lines.Add(line);
        }

        return lines;
    }
}
