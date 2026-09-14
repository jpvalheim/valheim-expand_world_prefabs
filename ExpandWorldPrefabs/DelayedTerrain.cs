using System;
using System.Collections.Generic;
using System.Linq;
using Service;
using UnityEngine;

namespace ExpandWorld.Prefab;

public class DelayedTerrain
{
  internal const float RetryIntervalSeconds = 1f;
  internal const int MaximumAttempts = 12;
  public DelayedTerrain(double due, Vector3 pos, float size, TerrainOp.Settings settings, float resetRadius)
  {
    Due = due;
    Pos = pos;
    Size = size;
    Settings = settings;
    ResetRadius = resetRadius;
  }

  private static readonly List<DelayedTerrain> Terrains = [];
  private double Due;
  private readonly Vector3 Pos;
  private readonly float Size;
  private readonly TerrainOp.Settings Settings;
  private readonly float ResetRadius;
  private HashSet<Vector2s>? PendingZones;
  private int Attempt;
  private Action<bool>? Completion;
  private bool Finished;
  public static void Clear() => Terrains.Clear();

  public static void Add(float delay, Vector3 pos, float size, TerrainOp.Settings settings, float resetRadius)
  {
    Add(delay, pos, size, settings, resetRadius, null);
  }

  internal static void Add(float delay, Vector3 pos, float size, TerrainOp.Settings settings, float resetRadius, Action<bool>? completion)
  {
    var created = TerrainManager.GenerateCompilers(pos, size);
    // Allow a newly created compiler, or corrected ownership, to initialize.
    if (created) delay = Mathf.Max(delay, 1f);
    if (delay <= 0f)
    {
      var terrain = new DelayedTerrain(ZNet.instance.m_netTime, pos, size, settings, resetRadius) { Completion = completion };
      if (!terrain.ExecuteAction())
        Terrains.Add(terrain);
      return;
    }
    Terrains.Add(new(ZNet.instance.m_netTime + delay, pos, size, settings, resetRadius) { Completion = completion });
  }
  public static void Execute()
  {
    for (var i = 0; i < Terrains.Count; i++)
    {
      var terrain = Terrains[i];
      if (terrain.Due > ZNet.instance.m_netTime) continue;
      if (terrain.ExecuteAction())
      {
        Terrains.RemoveAt(i);
        i--;
      }
    }
  }
  private bool ExecuteAction()
  {
    if (Finished) return true;
    try { return TryExecute(); }
    catch (Exception exception)
    {
      // A partly applied non-idempotent operation must never replay each frame.
      Log.Warning("Terrain operation failed and won't be retried: " + exception.Message);
      return Finish(false);
    }
  }
  private bool TryExecute()
  {
    Attempt++;
    PendingZones = TerrainManager.Modify(Pos, Size, Settings, ResetRadius, PendingZones);
    if (PendingZones.Count == 0)
      return Finish(true);
    if (Attempt >= MaximumAttempts)
    {
      Log.Warning("Terrain operation failed after " + Attempt + " attempts. Pending zones: " + FormatZones(PendingZones) + ".");
      return Finish(false);
    }
    Due = ZNet.instance.m_netTime + RetryIntervalSeconds;
    return false;
  }

  private bool Finish(bool success)
  {
    if (Finished) return true;
    Finished = true;
    Completion?.Invoke(success);
    return true;
  }

  private static string FormatZones(IEnumerable<Vector2s> zones) => string.Join(";", zones.OrderBy(zone => zone.x).ThenBy(zone => zone.y).Select(zone => $"{zone.x},{zone.y}"));
}
