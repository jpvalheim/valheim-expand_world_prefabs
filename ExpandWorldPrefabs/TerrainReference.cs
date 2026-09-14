using UnityEngine;

namespace ExpandWorld.Prefab;

/// <summary>
/// Resolves the terrain reference used when a dedicated server must recover a
/// terrain compiler through either a live binding or serialized-data path.
/// </summary>
internal static class TerrainReference
{
  private const float MinimumOffset = 0.01f;
  private const float ProxyMatchDistance = 1f;
  private static readonly int LocationProxyHash = "LocationProxy".GetStableHashCode();

  /// <summary>
  /// Expand World Data keeps the unshifted terrain surface in the vanilla
  /// LocationInstance and applies groundOffset to the spawned LocationProxy.
  /// A server-only terrain path does not contain the client-side location
  /// object, so normalize only recovery paths by that observable offset.
  /// </summary>
  internal static Vector3 Resolve(Vector3 sourcePosition, bool instanceBoundForOperation)
  {
    if (!instanceBoundForOperation || ZoneSystem.instance == null)
      return sourcePosition;

    var location = FindLocation(sourcePosition);
    if (location == null) return sourcePosition;

    var locationValue = location.Value;
    var proxy = FindLocationProxy(locationValue.m_position);
    if (proxy == null) return sourcePosition;

    var offset = CalculateVerticalOffset(locationValue.m_position.y, proxy.m_position.y);
    if (!IsFinite(offset) || Mathf.Abs(offset) < MinimumOffset)
      return sourcePosition;
    return ApplyVerticalOffset(sourcePosition, offset);
  }

  internal static float CalculateVerticalOffset(float locationY, float proxyY) => proxyY - locationY;

  internal static Vector3 ApplyVerticalOffset(Vector3 position, float offset)
  {
    position.y -= offset;
    return position;
  }

  private static ZoneSystem.LocationInstance? FindLocation(Vector3 position)
  {
    ZoneSystem.LocationInstance? closest = null;
    var closestDistance = float.MaxValue;
    var zone = ZoneSystem.GetZone(position);
    for (var x = zone.x - 1; x <= zone.x + 1; ++x)
    {
      for (var y = zone.y - 1; y <= zone.y + 1; ++y)
      {
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(new Vector2s(x, y), out var candidate) || candidate.m_location == null)
          continue;
        var distance = Utils.DistanceXZ(candidate.m_position, position);
        var radius = Mathf.Max(1f, candidate.m_location.m_exteriorRadius);
        if (distance > radius || distance >= closestDistance)
          continue;
        closest = candidate;
        closestDistance = distance;
      }
    }
    return closest;
  }

  private static ZDO? FindLocationProxy(Vector3 locationPosition)
  {
    ZDO? closest = null;
    var closestDistance = float.MaxValue;
    var zone = ZoneSystem.GetZone(locationPosition);
    for (var x = zone.x - 1; x <= zone.x + 1; ++x)
    {
      for (var y = zone.y - 1; y <= zone.y + 1; ++y)
      {
        var zdos = Helper.GetZDOsInSector(new Vector2s(x, y));
        if (zdos == null) continue;
        foreach (var candidate in zdos)
        {
          if (candidate.m_prefab != LocationProxyHash) continue;
          var distance = Utils.DistanceXZ(candidate.m_position, locationPosition);
          if (distance > ProxyMatchDistance || distance >= closestDistance) continue;
          closest = candidate;
          closestDistance = distance;
        }
      }
    }
    return closest;
  }

  private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
