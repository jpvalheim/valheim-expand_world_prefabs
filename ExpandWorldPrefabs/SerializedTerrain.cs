using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ExpandWorld.Prefab;

/// <summary>
/// Dedicated-server fallback for generated zones whose terrain compiler ZDO
/// exists but whose Heightmap/TerrainComp scene objects are intentionally not
/// loaded. It edits Valheim's native TCData payload; clients still consume the
/// result through their ordinary TerrainComp implementation.
/// </summary>
internal static class SerializedTerrain
{
  private const int Width = 64;
  private const float Scale = 1f;
  private const float LevelLimit = 8f;
  private const float SmoothLimit = 1f;

  internal static bool Apply(ZDO compiler, Vector2s zone, Vector3 pos,
    TerrainOp.Settings settings, out string reason)
  {
    reason = "unknown";
    if (HeightmapBuilder.instance == null || WorldGenerator.instance == null)
    {
      reason = "terrain-builder-unavailable";
      return false;
    }

    var center = ZoneSystem.GetZonePos(zone);
    var build = HeightmapBuilder.instance.RequestTerrainSync(center, Width, Scale, false, WorldGenerator.instance);
    var pitch = Width + 1;
    var count = pitch * pitch;
    if (build == null || build.m_baseHeights == null || build.m_baseHeights.Count != count ||
        build.m_baseMask == null || build.m_baseMask.Length != count)
    {
      var heightCount = build?.m_baseHeights?.Count ?? -1;
      var maskCount = build?.m_baseMask?.Length ?? -1;
      reason = $"terrain-build-data-not-ready:heights={heightCount}:mask={maskCount}:expected={count}";
      return false;
    }

    if (!TerrainReference.TryCreateSurface(center, Width, Scale,
        build.m_baseHeights, build.m_baseMask, out var reference,
        out var referenceReason))
    {
      reason = referenceReason;
      return false;
    }

    var storedBefore = compiler.GetByteArray(ZDOVars.s_TCData);
    var state = TerrainState.Load(storedBefore, build.m_baseHeights,
      build.m_baseMask, reference.Heights, reference.Mask, center, Width, Scale);
    var mutationWrites = state.Apply(pos, settings);
    var paintOnly = settings.m_paintCleared && !settings.m_level && !settings.m_raise && !settings.m_smooth;
    if (paintOnly && mutationWrites == 0)
    {
      // The authoritative payload may already be correct while an owning peer
      // still renders an older revision. Republish it instead of reporting a
      // dispatch-only success.
      if (storedBefore != null)
      {
        TerrainSync.PublishNativeSave(compiler);
      }
      reason = storedBefore == null ? "paint-noop-no-payload" : "paint-noop-republished";
      return true;
    }

    state.Operations++;
    state.LastOperationPoint = pos;
    state.LastOperationRadius = settings.GetRadius();
    var bytes = state.Save();
    TerrainSync.Commit(compiler, Utils.Compress(bytes));
    var storedAfter = compiler.GetByteArray(ZDOVars.s_TCData);
    if (!HasSavedOperation(storedAfter, state.Operations))
      throw new InvalidOperationException("Serialized terrain write returned without a matching stored operation receipt.");
    reason = "serialized-saved:" + referenceReason;
    return true;
  }

  private static bool HasSavedOperation(byte[]? bytes, int operations)
  {
    if (bytes == null) return false;
    var package = new ZPackage(Utils.Decompress(bytes));
    return package.ReadInt() == 1 && package.ReadInt() == operations;
  }

  internal sealed class TerrainState
  {
    private readonly IReadOnlyList<float> BaseHeights;
    private readonly IReadOnlyList<float> ReferenceHeights;
    private readonly Vector3 Center;
    private readonly int Width;
    private readonly int Pitch;
    private readonly float Scale;
    private readonly bool[] ModifiedHeight;
    private readonly float[] LevelDelta;
    private readonly float[] SmoothDelta;
    private readonly bool[] ModifiedPaint;
    private readonly Color[] PaintMask;

    internal int Operations;
    internal Vector3 LastOperationPoint;
    internal float LastOperationRadius;

    private TerrainState(IReadOnlyList<float> baseHeights,
      IReadOnlyList<float> referenceHeights, Color[] referenceMask,
      Vector3 center, int width, float scale)
    {
      BaseHeights = baseHeights;
      ReferenceHeights = referenceHeights;
      Center = center;
      Width = width;
      Pitch = width + 1;
      Scale = scale;
      var count = Pitch * Pitch;
      ModifiedHeight = new bool[count];
      LevelDelta = new float[count];
      SmoothDelta = new float[count];
      ModifiedPaint = new bool[count];
      PaintMask = new Color[count];
      Array.Copy(referenceMask, PaintMask, count);
    }

    internal static TerrainState Load(byte[]? compressed, IReadOnlyList<float> baseHeights,
      Color[] baseMask, Vector3 center, int width, float scale)
      => Load(compressed, baseHeights, baseMask, baseHeights, baseMask,
        center, width, scale);

    internal static TerrainState Load(byte[]? compressed,
      IReadOnlyList<float> baseHeights, Color[] baseMask,
      IReadOnlyList<float> referenceHeights, Color[] referenceMask,
      Vector3 center, int width, float scale)
    {
      var count = (width + 1) * (width + 1);
      if (baseHeights.Count != count || baseMask.Length != count ||
          referenceHeights.Count != count || referenceMask.Length != count)
        throw new InvalidDataException("Canonical terrain grid size does not match the zone pitch.");
      var state = new TerrainState(baseHeights, referenceHeights,
        referenceMask, center, width, scale);
      if (compressed == null) return state;

      var package = new ZPackage(Utils.Decompress(compressed));
      var version = package.ReadInt();
      if (version != 1) throw new InvalidDataException("Unsupported terrain compiler payload version " + version + ".");
      state.Operations = package.ReadInt();
      state.LastOperationPoint = package.ReadVector3();
      state.LastOperationRadius = package.ReadSingle();
      var heightCount = package.ReadInt();
      if (heightCount != count) throw new InvalidDataException("Terrain height array size does not match the canonical zone pitch.");
      for (var index = 0; index < heightCount; index++)
      {
        state.ModifiedHeight[index] = package.ReadBool();
        if (!state.ModifiedHeight[index]) continue;
        state.LevelDelta[index] = package.ReadSingle();
        state.SmoothDelta[index] = package.ReadSingle();
      }

      var paintCount = package.ReadInt();
      if (paintCount != count && paintCount != width * width)
        throw new InvalidDataException("Terrain paint array size does not match a supported native layout.");
      var modified = new bool[paintCount];
      var colors = new Color[paintCount];
      for (var index = 0; index < paintCount; index++)
      {
        modified[index] = package.ReadBool();
        if (!modified[index]) continue;
        colors[index] = new(package.ReadSingle(), package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
      }
      if (paintCount == count)
      {
        Array.Copy(modified, state.ModifiedPaint, count);
        for (var index = 0; index < count; index++)
          if (modified[index]) state.PaintMask[index] = colors[index];
      }
      else
      {
        for (var y = 0; y < state.Pitch; y++)
        {
          for (var x = 0; x < state.Pitch; x++)
          {
            var source = Math.Min(y, width - 1) * width + Math.Min(x, width - 1);
            var target = y * state.Pitch + x;
            state.ModifiedPaint[target] = modified[source];
            if (modified[source]) state.PaintMask[target] = colors[source];
          }
        }
      }
      return state;
    }

    internal byte[] Save()
    {
      var package = new ZPackage();
      package.Write(1);
      package.Write(Operations);
      package.Write(LastOperationPoint);
      package.Write(LastOperationRadius);
      package.Write(ModifiedHeight.Length);
      for (var index = 0; index < ModifiedHeight.Length; index++)
      {
        package.Write(ModifiedHeight[index]);
        if (!ModifiedHeight[index]) continue;
        package.Write(LevelDelta[index]);
        package.Write(SmoothDelta[index]);
      }
      package.Write(ModifiedPaint.Length);
      for (var index = 0; index < ModifiedPaint.Length; index++)
      {
        package.Write(ModifiedPaint[index]);
        if (!ModifiedPaint[index]) continue;
        package.Write(PaintMask[index].r);
        package.Write(PaintMask[index].g);
        package.Write(PaintMask[index].b);
        package.Write(PaintMask[index].a);
      }
      return package.GetArray();
    }

    internal int Apply(Vector3 pos, TerrainOp.Settings settings)
    {
      // TerrainComp mutates its delta arrays but does not rebuild Heightmap
      // until every action in this operation has finished. Preserve that
      // ordering by sampling the rendered heights once for this transaction.
      var sampledHeights = new float[ReferenceHeights.Count];
      for (var index = 0; index < sampledHeights.Length; index++)
        sampledHeights[index] = CurrentHeight(index);
      var changed = 0;
      if (settings.m_level)
        changed += Level(pos + Vector3.up * settings.m_levelOffset, settings.m_levelRadius, settings.m_square, sampledHeights);
      if (settings.m_raise)
        changed += Raise(pos, settings.m_raiseRadius, settings.m_raiseDelta, settings.m_square, settings.m_raisePower, sampledHeights);
      if (settings.m_smooth)
        changed += Smooth(pos + Vector3.up * settings.m_levelOffset, settings.m_smoothRadius, settings.m_square, settings.m_smoothPower, sampledHeights);
      if (settings.m_paintCleared)
        changed += Paint(pos, settings, sampledHeights);
      return changed;
    }

    internal float SampleHeight(Vector3 pos)
    {
      WorldToVertex(pos, false, out var x, out var y);
      x = Mathf.Clamp(x, 0, Pitch - 1);
      y = Mathf.Clamp(y, 0, Pitch - 1);
      return CurrentHeight(y * Pitch + x);
    }

    internal float SampleBaseHeight(Vector3 pos)
    {
      WorldToVertex(pos, false, out var x, out var y);
      x = Mathf.Clamp(x, 0, Pitch - 1);
      y = Mathf.Clamp(y, 0, Pitch - 1);
      return BaseHeights[y * Pitch + x];
    }

    internal float SampleReferenceHeight(Vector3 pos)
    {
      WorldToVertex(pos, false, out var x, out var y);
      x = Mathf.Clamp(x, 0, Pitch - 1);
      y = Mathf.Clamp(y, 0, Pitch - 1);
      return ReferenceHeights[y * Pitch + x];
    }

    private int Level(Vector3 pos, float radius, bool square, float[] sampledHeights)
    {
      WorldToVertex(pos, false, out var centerX, out var centerY);
      var localY = pos.y - Center.y;
      var scaledRadius = radius / Scale;
      var extent = Mathf.CeilToInt(scaledRadius);
      var changed = 0;
      for (var y = centerY - extent; y <= centerY + extent; y++)
      {
        for (var x = centerX - extent; x <= centerX + extent; x++)
        {
          if (!square && Vector2.Distance(new(centerX, centerY), new(x, y)) > scaledRadius) continue;
          if (!InBounds(x, y)) continue;
          var index = y * Pitch + x;
          var delta = localY - sampledHeights[index] + SmoothDelta[index];
          var level = Mathf.Clamp(LevelDelta[index] + delta, -LevelLimit, LevelLimit);
          if (!ModifiedHeight[index] || level != LevelDelta[index] || SmoothDelta[index] != 0f)
            changed++;
          LevelDelta[index] = level;
          SmoothDelta[index] = 0f;
          ModifiedHeight[index] = true;
        }
      }
      return changed;
    }

    private int Raise(Vector3 pos, float radius, float delta, bool square, float power, float[] sampledHeights)
    {
      WorldToVertex(pos, false, out var centerX, out var centerY);
      var localY = pos.y - Center.y;
      var scaledRadius = radius / Scale;
      var extent = Mathf.CeilToInt(scaledRadius);
      var changed = 0;
      for (var y = centerY - extent; y <= centerY + extent; y++)
      {
        for (var x = centerX - extent; x <= centerX + extent; x++)
        {
          if (!InBounds(x, y)) continue;
          var falloff = 1f;
          if (!square)
          {
            var distance = Vector2.Distance(new(centerX, centerY), new(x, y));
            if (distance > scaledRadius) continue;
            if (power > 0f)
            {
              falloff = 1f - distance / scaledRadius;
              if (power != 1f) falloff = Mathf.Pow(falloff, power);
            }
          }
          var current = sampledHeights[y * Pitch + x];
          var appliedDelta = delta * falloff;
          var target = localY + appliedDelta;
          if (delta < 0f && target > current) continue;
          if (delta > 0f)
          {
            if (target < current) continue;
            if (target > current + appliedDelta) target = current + appliedDelta;
          }
          var index = y * Pitch + x;
          var adjustment = target - current + SmoothDelta[index];
          var level = Mathf.Clamp(LevelDelta[index] + adjustment, -LevelLimit, LevelLimit);
          if (!ModifiedHeight[index] || level != LevelDelta[index] || SmoothDelta[index] != 0f)
            changed++;
          LevelDelta[index] = level;
          SmoothDelta[index] = 0f;
          ModifiedHeight[index] = true;
        }
      }
      return changed;
    }

    private int Smooth(Vector3 pos, float radius, bool square, float power, float[] sampledHeights)
    {
      WorldToVertex(pos, false, out var centerX, out var centerY);
      var targetHeight = pos.y - Center.y;
      var scaledRadius = radius / Scale;
      var extent = Mathf.CeilToInt(scaledRadius);
      var changed = 0;
      for (var y = centerY - extent; y <= centerY + extent; y++)
      {
        for (var x = centerX - extent; x <= centerX + extent; x++)
        {
          var distance = Vector2.Distance(new(centerX, centerY), new(x, y));
          if (distance > scaledRadius) continue;
          if (!InBounds(x, y)) continue;
          var ratio = distance / scaledRadius;
          ratio = power == 3f ? ratio * ratio * ratio : Mathf.Pow(ratio, power);
          var index = y * Pitch + x;
          var current = sampledHeights[index];
          var adjustment = Mathf.Lerp(current, targetHeight, 1f - ratio) - current;
          var smooth = Mathf.Clamp(SmoothDelta[index] + adjustment, -SmoothLimit, SmoothLimit);
          if (!ModifiedHeight[index] || smooth != SmoothDelta[index])
            changed++;
          SmoothDelta[index] = smooth;
          ModifiedHeight[index] = true;
        }
      }
      return changed;
    }

    private int Paint(Vector3 pos, TerrainOp.Settings settings, float[] sampledHeights)
    {
      var worldPos = pos;
      worldPos.x -= 0.5f;
      worldPos.z -= 0.5f;
      var height = worldPos.y - Center.y;
      WorldToVertex(worldPos, true, out var centerX, out var centerY);
      var radius = settings.m_paintRadius / Scale;
      var extent = Mathf.CeilToInt(radius);
      var target = PaintColor(settings.m_paintType);
      var changed = 0;
      for (var y = centerY - extent; y <= centerY + extent; y++)
      {
        for (var x = centerX - extent; x <= centerX + extent; x++)
        {
          if (!InBounds(x, y)) continue;
          var index = y * Pitch + x;
          if (settings.m_paintHeightCheck && sampledHeights[index] > height) continue;
          var distance = Vector2.Distance(new(centerX, centerY), new(x, y));
          var strength = Mathf.Pow(1f - Mathf.Clamp01(distance / radius), 0.1f);
          var color = Color.Lerp(PaintMask[index], target, strength);
          color.a = PaintMask[index].a;
          if (!ModifiedPaint[index] || !Same(PaintMask[index], color))
            changed++;
          ModifiedPaint[index] = true;
          PaintMask[index] = color;
        }
      }
      return changed;
    }

    private float CurrentHeight(int index) => Mathf.Clamp(
      ReferenceHeights[index] + LevelDelta[index] + SmoothDelta[index],
      ReferenceHeights[index] - LevelLimit,
      ReferenceHeights[index] + LevelLimit);

    private void WorldToVertex(Vector3 pos, bool mask, out int x, out int y)
    {
      var relative = pos - Center;
      var half = mask ? (Width + 1) / 2 : Width / 2;
      x = Mathf.FloorToInt(relative.x / Scale + 0.5f + half);
      y = Mathf.FloorToInt(relative.z / Scale + 0.5f + half);
    }

    private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Pitch && y < Pitch;

    private static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private static Color PaintColor(TerrainModifier.PaintType paintType) => paintType switch
    {
      TerrainModifier.PaintType.Cultivate => Heightmap.m_paintMaskCultivated,
      TerrainModifier.PaintType.Paved => Heightmap.m_paintMaskPaved,
      TerrainModifier.PaintType.Reset => Heightmap.m_paintMaskNothing,
      TerrainModifier.PaintType.ClearVegetation => Heightmap.m_paintMaskClearVegetation,
      TerrainModifier.PaintType.DeepSnow => Heightmap.m_paintMaskDeepSnow,
      _ => Heightmap.m_paintMaskDirt
    };
  }
}
