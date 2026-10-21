using System.Globalization;
using System.IO;
using System.Text;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Dalamud.Game.Text.SeStringHandling.Payloads;

/// <summary>
/// This class represents a custom Dalamud clickable chat link.
/// </summary>
public class DalamudLinkPayload : Payload
{
    // Not JsonConvert: it applies JsonConvert.DefaultSettings, which any plugin can set for the whole process. These
    // are Newtonsoft's defaults, so the bytes stay the same, but the plugin name is always a JSON array: its '[' is
    // what keeps a Dalamud link from being taken for the game's EventTutorial link (see DalamudLinkStripper).
    private static readonly JsonSerializerSettings ExtraDataJson = new()
    {
        ContractResolver = new DefaultContractResolver(),
        CheckAdditionalContent = true,
    };

    /// <inheritdoc/>
    public override PayloadType Type => PayloadType.DalamudLink;

    /// <summary>Gets the plugin command ID to be linked.</summary>
    public uint CommandId { get; internal set; }

    /// <summary>Gets an optional extra integer value 1.</summary>
    public int Extra1 { get; internal set; }

    /// <summary>Gets an optional extra integer value 2.</summary>
    public int Extra2 { get; internal set; }

    /// <summary>Gets the plugin name to be linked.</summary>
    public string Plugin { get; internal set; } = string.Empty;

    /// <summary>Gets an optional extra string.</summary>
    public string ExtraString { get; internal set; } = string.Empty;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{this.Type} - {this.Plugin} ({this.CommandId}/{this.Extra1}/{this.Extra2}/{this.ExtraString})";

    /// <inheritdoc/>
    protected override byte[] EncodeImpl()
    {
        var ssb = Lumina.Text.SeStringBuilder.SharedPool.Get();
        var res = ssb.BeginMacro(MacroCode.Link)
                     .AppendIntExpression((int)EmbeddedInfoType.DalamudLink - 1)
                     .AppendUIntExpression(this.CommandId)
                     .AppendIntExpression(this.Extra1)
                     .AppendIntExpression(this.Extra2)
                     .BeginStringExpression()
                     .Append(SerializeExtraData(this.Plugin, this.ExtraString))
                     .EndExpression()
                     .EndMacro()
                     .ToArray();
        Lumina.Text.SeStringBuilder.SharedPool.Return(ssb);
        return res;
    }

    /// <inheritdoc/>
    protected override void DecodeImpl(BinaryReader reader, long endOfStream)
    {
        // Note: Payload.DecodeChunk already took the first int expr (DalamudLink).

        var body = reader.ReadBytes((int)(endOfStream - reader.BaseStream.Position));
        var rosps = new ReadOnlySePayloadSpan(ReadOnlySePayloadType.Macro, MacroCode.Link, body.AsSpan());

        if (!rosps.TryGetExpression(
                out var commandIdExpression,
                out var extra1Expression,
                out var extra2Expression,
                out var compositeExpression))
        {
            if (!rosps.TryGetExpression(out var pluginExpression, out commandIdExpression))
                return;

            if (!pluginExpression.TryGetString(out var pluginString))
                return;

            if (!commandIdExpression.TryGetUInt(out var commandId))
                return;

            this.Plugin = pluginString.ExtractText();
            this.CommandId = commandId;
        }
        else
        {
            if (!commandIdExpression.TryGetUInt(out var commandId))
                return;

            if (!extra1Expression.TryGetInt(out var extra1))
                return;

            if (!extra2Expression.TryGetInt(out var extra2))
                return;

            if (!compositeExpression.TryGetString(out var compositeString))
                return;

            string[]? extraData;
            try
            {
                extraData = DeserializeExtraData(compositeString.ExtractText());
            }
            catch
            {
                return;
            }

            // JSON null or an array too short is not a Dalamud link either; reading it used to throw.
            if (extraData is not { Length: >= 2 })
                return;

            this.CommandId = commandId;
            this.Extra1 = extra1;
            this.Extra2 = extra2;
            this.Plugin = extraData[0];
            this.ExtraString = extraData[1];
        }
    }

    // Same writer and reader JsonConvert.SerializeObject / DeserializeObject use.
    private static string SerializeExtraData(string plugin, string extraString)
    {
        var text = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture);
        using (var writer = new JsonTextWriter(text))
            JsonSerializer.Create(ExtraDataJson).Serialize(writer, new[] { plugin, extraString });

        return text.ToString();
    }

    private static string[]? DeserializeExtraData(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json));
        return JsonSerializer.Create(ExtraDataJson).Deserialize<string[]>(reader);
    }
}
