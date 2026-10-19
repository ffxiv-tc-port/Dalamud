using System;
using System.Collections.Generic;

namespace Dalamud.Game.Gui.ContextMenu;

/// <summary>
/// Derives, from game code, the index of the first item in the "ContextMenu" addon's AtkValues (7 before the 7.4 generation, 8 from it).
/// </summary>
/// <remarks>
/// Pure: never touches game memory, the caller supplies the bytes (in game: the scanner's module copy; offline: the executable).
/// Three independent sources; at least two must decode, and every one that decodes must agree, otherwise <see cref="NotDerived"/>.
/// Same rules and evidence text as ECommons' ContextMenuLayoutDerivation, so the two log lines can be compared directly.
/// </remarks>
internal static class ContextMenuLayoutDerivation
{
    /// <summary>Returned when the layout could not be derived.</summary>
    internal const int NotDerived = -1;

    /// <summary>The AtkValue size the OnRefresh decoder assumes.</summary>
    internal const int AtkValueSize = 16;

    /// <summary>Smallest index accepted.</summary>
    internal const int MinIndex = 1;

    /// <summary>Largest index accepted.</summary>
    internal const int MaxIndex = 16;

    /// <summary>Gets the sources, each with its accepted instruction shapes.</summary>
    internal static readonly Source[] Sources =
    [
        // AgentContext menu reset: mov ecx,2 ; mov edx,<first> ; xor edi,edi ; mov [rax+d],dx ; mov byte [rax+d+2],71h
        new("AgentContext.選單重設", [new("B9 02 00 00 00 BA ?? 00 00 00 33 FF 66 89 90 ?? ?? ?? ?? C6 80 ?? ?? ?? ?? 71", DecodeAgentReset)]),

        // AddonContextMenu.OnRefresh: lea r9,[r15+16*first] ; ... ; lea r8d,[r14-first] (disp32 once 16*first >= 0x80)
        new(
            "AddonContextMenu.OnRefresh",
            [
                new("4D 8D 4F ?? 8B 8F ?? ?? ?? ?? 45 8D 46 ?? 88 5C 24", b => DecodeRefresh((sbyte)b[3], (sbyte)b[13])),
                new("4D 8D 8F ?? ?? ?? ?? 8B 8F ?? ?? ?? ?? 45 8D 46 ?? 88 5C 24", b => DecodeRefresh(BitConverter.ToInt32(b, 3), (sbyte)b[16])),
            ]),

        // AgentInventoryContext open: writes first+1 with a title and first without one, both into the same field
        new("AgentInventoryContext.開選單", [new("C7 46 ?? ?? 00 00 00 48 8D 4E ?? E8 ?? ?? ?? ?? 4C 89 6E ?? EB 07 C7 46 ?? ?? 00 00 00", DecodeInventoryOpen)]),
    ];

    /// <summary>
    /// Derives the first item index.
    /// </summary>
    /// <param name="readMatch">Given a signature and a length, returns the bytes at its first match, or null if it does not match.</param>
    /// <param name="evidence">Per-source result, for the log.</param>
    /// <returns>The index, or <see cref="NotDerived"/>.</returns>
    internal static int Derive(Func<string, int, byte[]?> readMatch, out string evidence)
    {
        var found = new List<int>();
        var notes = new List<string>();
        foreach (var source in Sources)
        {
            var value = NotDerived;
            var note = "沒命中";
            foreach (var variant in source.Variants)
            {
                var length = SignatureLength(variant.Signature);
                var bytes = readMatch(variant.Signature, length);
                if (bytes == null || bytes.Length != length)
                    continue;

                if (!MatchesFixedBytes(variant.Signature, bytes))
                {
                    note = "命中處位元組與特徵碼不符";
                    continue;
                }

                var decoded = variant.Decode(bytes);
                if (decoded < MinIndex || decoded > MaxIndex)
                {
                    note = "命中但內部不一致";
                    continue;
                }

                value = decoded;
                note = decoded.ToString();
                break;
            }

            notes.Add($"{source.Name}={note}");
            if (value != NotDerived)
                found.Add(value);
        }

        evidence = string.Join("；", notes);
        if (found.Count < 2)
            return NotDerived;

        foreach (var value in found)
        {
            if (value != found[0])
                return NotDerived;
        }

        return found[0];
    }

    /// <summary>Counts the bytes a signature covers.</summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The length in bytes.</returns>
    internal static int SignatureLength(string signature)
        => signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Checks every non-wildcard byte of a signature again.</summary>
    /// <param name="signature">The signature.</param>
    /// <param name="bytes">The bytes read at the match.</param>
    /// <returns>Whether they agree.</returns>
    internal static bool MatchesFixedBytes(string signature, byte[] bytes)
    {
        var tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != bytes.Length)
            return false;

        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??")
                continue;
            if (Convert.ToByte(tokens[i], 16) != bytes[i])
                return false;
        }

        return true;
    }

    // CurrentEventIndex and CurrentEventId are adjacent (2 apart) in one menu struct: both writes must hit the same struct.
    private static int DecodeAgentReset(byte[] b)
        => BitConverter.ToInt32(b, 21) == BitConverter.ToInt32(b, 15) + 2 ? b[6] : NotDerived;

    private static int DecodeRefresh(int valuesOffset, int countDelta)
        => valuesOffset > 0 && valuesOffset % AtkValueSize == 0 && valuesOffset / AtkValueSize == -countDelta ? -countDelta : NotDerived;

    private static int DecodeInventoryOpen(byte[] b)
        => b[2] == b[24] && b[3] == b[25] + 1 ? b[25] : NotDerived;

    /// <summary>One accepted instruction shape of a source.</summary>
    /// <param name="Signature">The signature.</param>
    /// <param name="Decode">Turns the matched bytes into an index, or <see cref="NotDerived"/>.</param>
    internal sealed record Variant(string Signature, Func<byte[], int> Decode);

    /// <summary>One independent place in game code that encodes the first item index.</summary>
    /// <param name="Name">Name used in the evidence text.</param>
    /// <param name="Variants">Accepted shapes, tried in order.</param>
    internal sealed record Source(string Name, Variant[] Variants);
}
