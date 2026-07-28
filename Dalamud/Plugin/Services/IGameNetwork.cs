using Dalamud.Game.Network;

namespace Dalamud.Plugin.Services;

/// <summary>
/// This class handles interacting with game network events.
/// </summary>
// TC 差異：維持警告級而非錯誤級。ECommons 的 Svc.cs 仍然參照這個介面，
// 全艦隊 43 個外掛都吃 ECommons，改成錯誤級會讓它們全部編不過。
// （執行期本來就沒有實作可注入，這個屬性只影響編譯。）
[Obsolete("Will be removed in a future release. Use packet handler hooks instead.")]
public interface IGameNetwork : IDalamudService
{
    // TODO(v9): we shouldn't be passing pointers to the actual data here

    /// <summary>
    /// The delegate type of a network message event.
    /// </summary>
    /// <param name="dataPtr">The pointer to the raw data.</param>
    /// <param name="opCode">The operation ID code.</param>
    /// <param name="sourceActorId">The source actor ID.</param>
    /// <param name="targetActorId">The taret actor ID.</param>
    /// <param name="direction">The direction of the packed.</param>
    public delegate void OnNetworkMessageDelegate(nint dataPtr, ushort opCode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction);

    /// <summary>
    /// Event that is called when a network message is sent/received.
    /// </summary>
    public event OnNetworkMessageDelegate NetworkMessage;
}
