using System.Collections.Generic;

using Dalamud.Data;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI.Info;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Dalamud.Game.Network.Structures.InfoProxy;

/// <summary>
/// Display group of a character. Used for friends.
/// </summary>
public enum DisplayGroup : sbyte
{
    /// <summary>
    /// All display groups.
    /// </summary>
    All = -1,

    /// <summary>
    /// No display group.
    /// </summary>
    None,

    /// <summary>
    /// Star display group.
    /// </summary>
    Star,

    /// <summary>
    /// Circle display group.
    /// </summary>
    Circle,

    /// <summary>
    /// Triangle display group.
    /// </summary>
    Triangle,

    /// <summary>
    /// Diamond display group.
    /// </summary>
    Diamond,

    /// <summary>
    /// Heart display group.
    /// </summary>
    Heart,

    /// <summary>
    /// Spade display group.
    /// </summary>
    Spade,

    /// <summary>
    /// Club display group.
    /// </summary>
    Club,
}

/// <summary>
/// Dalamud wrapper around a client structs <see cref="InfoProxyCommonList.CharacterData"/>.
/// </summary>
public unsafe class CharacterData
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CharacterData"/> class.
    /// </summary>
    /// <param name="data">Character data to wrap.</param>
    internal CharacterData(InfoProxyCommonList.CharacterData* data)
    {
        this.Address = (nint)data;
    }

    /// <summary>
    /// Gets the address of the <see cref="InfoProxyCommonList.CharacterData"/> in memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a snapshot of the address taken when this instance was constructed; it is never
    /// re-resolved. Use it only for as long as the call that handed you this instance is on the
    /// stack, and never store it across frames: the entry it points at lives inside an info
    /// proxy's array, which the game rewrites and reallocates as the underlying list changes.
    /// Dereferencing a stale address raises an AccessViolationException, a corrupted-state
    /// exception that no managed <c>try</c>/<c>catch</c> can contain.
    /// </para>
    /// <para>
    /// To hold on to a character, store its <see cref="ContentId"/> and look the entry up again
    /// from the info proxy on each frame you need it.
    /// </para>
    /// </remarks>
    public nint Address { get; }

    /// <summary>
    /// Gets the content id of the character.
    /// </summary>
    public ulong ContentId => this.Struct->ContentId;

    /// <summary>
    /// Gets the status mask of the character.
    /// </summary>
    public ulong StatusMask => (ulong)this.Struct->State;

    /// <summary>
    /// Gets the applicable statues of the character.
    /// </summary>
    public IReadOnlyList<RowRef<OnlineStatus>> Statuses
    {
        get
        {
            var statuses = new List<RowRef<OnlineStatus>>();
            for (var i = 0; i < 64; i++)
            {
                if ((this.StatusMask & (1UL << i)) != 0)
                    statuses.Add(LuminaUtils.CreateRef<OnlineStatus>((uint)i));
            }

            return statuses;
        }
    }

    /// <summary>
    /// Gets the display group of the character.
    /// </summary>
    public DisplayGroup DisplayGroup => (DisplayGroup)this.Struct->Group;

    /// <summary>
    /// Gets a value indicating whether the character's home world is different from the current world.
    /// </summary>
    public bool IsFromOtherServer => this.Struct->IsOtherServer;

    /// <summary>
    /// Gets the sort order of the character.
    /// </summary>
    public byte Sort => this.Struct->Sort;

    /// <summary>
    /// Gets the current world of the character.
    /// </summary>
    public RowRef<World> CurrentWorld => LuminaUtils.CreateRef<World>(this.Struct->CurrentWorld);

    /// <summary>
    /// Gets the home world of the character.
    /// </summary>
    public RowRef<World> HomeWorld => LuminaUtils.CreateRef<World>(this.Struct->HomeWorld);

    /// <summary>
    /// Gets the location of the character.
    /// </summary>
    public RowRef<TerritoryType> Location => LuminaUtils.CreateRef<TerritoryType>(this.Struct->Location);

    /// <summary>
    /// Gets the grand company of the character.
    /// </summary>
    public RowRef<GrandCompany> GrandCompany => LuminaUtils.CreateRef<GrandCompany>((uint)this.Struct->GrandCompany);

    /// <summary>
    /// Gets the primary client language of the character.
    /// </summary>
    public ClientLanguage ClientLanguage => (ClientLanguage)this.Struct->ClientLanguage;

    /// <summary>
    /// Gets the supported language mask of the character.
    /// </summary>
    public byte LanguageMask => (byte)this.Struct->Languages;

    /// <summary>
    /// Gets the supported languages the character supports.
    /// </summary>
    public IReadOnlyList<ClientLanguage> Languages
    {
        get
        {
            var languages = new List<ClientLanguage>();
            for (var i = 0; i < 4; i++)
            {
                if ((this.LanguageMask & (1 << i)) != 0)
                    languages.Add((ClientLanguage)i);
            }

            return languages;
        }
    }

    /// <summary>
    /// Gets the gender of the character.
    /// </summary>
    public byte Gender => this.Struct->Sex;

    /// <summary>
    /// Gets the job of the character.
    /// </summary>
    public RowRef<ClassJob> ClassJob => LuminaUtils.CreateRef<ClassJob>(this.Struct->Job);

    /// <summary>
    /// Gets the name of the character.
    /// </summary>
    public string Name => NativeStringUtil.ReadFixedBuffer(this.Struct->Name);

    /// <summary>
    /// Gets the free company tag of the character.
    /// </summary>
    public string FCTag => NativeStringUtil.ReadFixedBuffer(this.Struct->FCTag);

    /// <summary>
    /// Gets the underlying <see cref="InfoProxyCommonList.CharacterData"/> struct.
    /// </summary>
    internal InfoProxyCommonList.CharacterData* Struct => (InfoProxyCommonList.CharacterData*)this.Address;
}
