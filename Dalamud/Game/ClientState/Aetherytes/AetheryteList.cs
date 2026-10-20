using System.Collections;
using System.Collections.Generic;
using System.Threading;

using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Serilog;

namespace Dalamud.Game.ClientState.Aetherytes;

/// <summary>
/// This collection represents the list of available Aetherytes in the Teleport window.
/// </summary>
[PluginInterface]
[ServiceManager.EarlyLoadedService]
#pragma warning disable SA1015
[ResolveVia<IAetheryteList>]
#pragma warning restore SA1015
internal sealed unsafe partial class AetheryteList : IServiceType, IAetheryteList
{
    private static int unresolvedLogged;

    [ServiceManager.ServiceDependency]
    private readonly ObjectTable objectTable = Service<ObjectTable>.Get();

    [ServiceManager.ServiceConstructor]
    private AetheryteList()
    {
        // Telepo is looked up on use only: Telepo.Instance() throws when unresolved, and a throwing early-loaded
        // service constructor takes every Dalamud service, and so every plugin, down with it.
        Log.Verbose($"Teleport address {Util.DescribeAddress(Telepo.Addresses.Instance.Value)}");
    }

    /// <inheritdoc/>
    public int Length
    {
        get
        {
            var telepo = GetTelepo();
            if (telepo == null)
                return 0;

            if (this.objectTable.LocalPlayer == null)
                return 0;

            this.Update(telepo);

            if (telepo->TeleportList.First == telepo->TeleportList.Last)
                return 0;

            return telepo->TeleportList.Count;
        }
    }

    /// <inheritdoc/>
    public IAetheryteEntry? this[int index]
    {
        get
        {
            if (index < 0 || index >= this.Length)
            {
                return null;
            }

            if (this.objectTable.LocalPlayer == null)
                return null;

            var telepo = GetTelepo();
            if (telepo == null)
                return null;

            return new AetheryteEntry(telepo->TeleportList[index]);
        }
    }

    /// <summary>
    /// Gets the game's Telepo, or null (logged once) when this client's Telepo.Instance or
    /// Telepo.UpdateAetheryteList could not be resolved; the list is then empty. Never throws.
    /// </summary>
    /// <returns>The Telepo instance, or null.</returns>
    private static Telepo* GetTelepo()
    {
        if (Telepo.Addresses.Instance.Value != 0 && Telepo.MemberFunctionPointers.UpdateAetheryteList != null)
            return Telepo.Instance();

        if (Interlocked.Exchange(ref unresolvedLogged, 1) == 0)
            Log.Warning("Telepo.Instance or Telepo.UpdateAetheryteList is not resolved on this client; the aetheryte list stays empty");

        return null;
    }

    private void Update(Telepo* telepo)
    {
        // this is very very important as otherwise it crashes
        if (this.objectTable.LocalPlayer == null)
            return;

        telepo->UpdateAetheryteList();
    }
}

/// <summary>
/// This collection represents the list of available Aetherytes in the Teleport window.
/// </summary>
internal sealed partial class AetheryteList
{
    /// <inheritdoc/>
    public int Count => this.Length;

    /// <inheritdoc/>
    public IEnumerator<IAetheryteEntry> GetEnumerator()
    {
        for (var i = 0; i < this.Length; i++)
        {
            yield return this[i];
        }
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return this.GetEnumerator();
    }
}
