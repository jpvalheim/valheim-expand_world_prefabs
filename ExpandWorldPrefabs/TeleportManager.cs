using System.Collections.Generic;
using Service;
using UnityEngine;

namespace ExpandWorld.Prefab;

/// <summary>
/// Keeps EWP's resetcloth rules from writing a stale source-position snapshot
/// back to a client-owned Player ZDO during an EWP-dispatched teleport.
/// Valheim's ResetCloth RPC is never delayed or canceled.
/// </summary>
internal static class TeleportManager
{
  internal const double WatchSeconds = 16d;
  internal const double ResetClothDeferralSeconds = 12d;
  private const float DestinationHorizontalTolerance = 8f;
  private static readonly int TeleportHash = "RPC_TeleportTo".GetStableHashCode();
  private static readonly int ClientTeleportHash = "RPC_TeleportPlayer".GetStableHashCode();
  private static readonly Dictionary<ZDOID, TeleportWatch> Watches = [];

  internal static bool IsTeleport(int hash) => hash == TeleportHash || hash == ClientTeleportHash;

  internal static void Track(ZDOID actor, int hash, object[] parameters)
  {
    if (!IsTeleport(hash) || actor == ZDOID.None || ZNet.instance == null || !TryGetDestination(parameters, out var destination))
      return;
    Watches[actor] = new(destination, ZNet.instance.m_netTime + WatchSeconds);
  }

  /// <summary>
  /// Returns true when EWP's resetcloth rule callback was queued. The caller
  /// must still allow Valheim's original RPC to run.
  /// </summary>
  internal static bool TryDeferResetCloth(ZDO zdo)
  {
    if (ZNet.instance == null || !TryGetWatch(zdo.m_uid, out var watch))
      return false;
    if (IsAtDestination(zdo.m_position, watch.Destination))
      return false;

    if (!watch.ResetClothPending)
    {
      watch.ResetClothPending = true;
      watch.ResetClothStarted = ZNet.instance.m_netTime;
      watch.ResetClothDeadline = CalculateResetClothDeadline(watch.ResetClothStarted, watch.Expires);
    }
    return true;
  }

  internal static void Execute()
  {
    var zdoManager = ZDOMan.instance;
    if (Watches.Count == 0 || ZNet.instance == null || zdoManager == null) return;

    // A released rule can dispatch another teleport, so iterate a snapshot.
    var watches = new List<KeyValuePair<ZDOID, TeleportWatch>>(Watches);
    foreach (var pair in watches)
    {
      var watch = pair.Value;
      if (!Watches.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, watch))
        continue;

      if (!watch.ResetClothPending)
      {
        if (watch.Expires < ZNet.instance.m_netTime)
          Watches.Remove(pair.Key);
        continue;
      }

      var zdo = zdoManager.GetZDO(pair.Key);
      if (zdo == null || zdoManager.m_deadZDOs.ContainsKey(pair.Key))
      {
        Watches.Remove(pair.Key);
        Log.Warning($"Skipped deferred resetcloth rules for {pair.Key}: Player ZDO is missing or dead.");
        continue;
      }

      if (IsAtDestination(zdo.m_position, watch.Destination))
      {
        Watches.Remove(pair.Key);
        Manager.Handle(ActionType.State, ["resetcloth"], zdo);
        continue;
      }

      if (ZNet.instance.m_netTime >= watch.ResetClothDeadline)
      {
        Watches.Remove(pair.Key);
        Log.Warning($"Skipped deferred resetcloth rules for {pair.Key}: Player ZDO did not reach the teleport destination within {(watch.ResetClothDeadline - watch.ResetClothStarted):0.###} seconds.");
      }
    }
  }

  internal static void Clear() => Watches.Clear();

  private static bool TryGetDestination(object[] parameters, out Vector3 destination)
  {
    foreach (var parameter in parameters)
    {
      if (parameter is Vector3 vector)
      {
        destination = vector;
        return true;
      }
    }
    destination = Vector3.zero;
    return false;
  }

  private static bool TryGetWatch(ZDOID id, out TeleportWatch watch)
  {
    if (!Watches.TryGetValue(id, out watch!)) return false;
    if (ZNet.instance != null && watch.Expires >= ZNet.instance.m_netTime) return true;
    Watches.Remove(id);
    watch = null!;
    return false;
  }

  internal static float HorizontalDistance(Vector3 value, Vector3 destination)
  {
    var x = value.x - destination.x;
    var z = value.z - destination.z;
    return Mathf.Sqrt(x * x + z * z);
  }

  internal static bool IsAtDestination(Vector3 value, Vector3 destination) =>
    HorizontalDistance(value, destination) <= DestinationHorizontalTolerance;

  internal static double CalculateResetClothDeadline(double now, double teleportExpires) =>
    System.Math.Min(now + ResetClothDeferralSeconds, teleportExpires);

  private sealed class TeleportWatch(Vector3 destination, double expires)
  {
    internal Vector3 Destination { get; } = destination;
    internal double Expires { get; } = expires;
    internal bool ResetClothPending { get; set; }
    internal double ResetClothStarted { get; set; }
    internal double ResetClothDeadline { get; set; }
  }
}
