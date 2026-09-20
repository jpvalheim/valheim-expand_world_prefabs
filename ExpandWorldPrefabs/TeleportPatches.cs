using HarmonyLib;

namespace ExpandWorld.Prefab;

/// <summary>
/// Starts the Player-write quarantine for native teleports routed by the
/// server, including teleports sent by another server mod.
/// </summary>
[HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
internal static class ServerTeleportRouteObserver
{
  private static void Prefix(ZRoutedRpc.RoutedRPCData rpcData)
  {
    TeleportManager.TrackRouted(rpcData);
  }
}

/// <summary>
/// Observes fresh owner ZDO updates. The native packet is never changed,
/// suppressed, or replayed.
/// </summary>
[HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
internal static class PlayerZdoSyncObserver
{
  private static void Prefix(ZRpc rpc, out TeleportManager.TeleportNetworkState? __state)
  {
    __state = TeleportManager.CaptureNetwork(rpc);
  }

  private static void Postfix(TeleportManager.TeleportNetworkState? __state)
  {
    TeleportManager.CompleteNetwork(__state);
  }
}
