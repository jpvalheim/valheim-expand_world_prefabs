using System;
using System.Linq;
using ExpandWorld.Prefab;

namespace ExpandWorldPrefabs.Tests;

internal static class TerrainAndTeleportChecks
{
  internal static void RunTeleportDestinationChecks()
  {
    void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    var destination = new UnityEngine.Vector3(100f, 50f, 200f);
    Check(TeleportManager.IsAtDestination(new UnityEngine.Vector3(100f, -500f, 200f), destination), "Vertical floor adjustment should not block release");
    Check(TeleportManager.IsAtDestination(new UnityEngine.Vector3(107.99f, 50f, 200f), destination), "Position inside horizontal tolerance was rejected");
    Check(!TeleportManager.IsAtDestination(new UnityEngine.Vector3(108.01f, 50f, 200f), destination), "Position outside horizontal tolerance was accepted");
    Check(Math.Abs(TeleportManager.HorizontalDistance(new UnityEngine.Vector3(103f, 0f, 204f), destination) - 5f) < 0.001f, "Horizontal distance calculation changed");
    Check(Math.Abs(TeleportManager.CalculateResetClothDeadline(100d, 116d) - 112d) < 0.001d,
      "resetcloth callback did not receive its 12-second arrival window");
    Check(Math.Abs(TeleportManager.CalculateResetClothDeadline(110d, 116d) - 116d) < 0.001d,
      "resetcloth callback outlived the enclosing 16-second teleport watch");
    System.Console.WriteLine("PASS: teleport destination synchronization checks");
  }

  internal static void RunTerrainReferenceChecks()
  {
    void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    var offset = TerrainReference.CalculateVerticalOffset(48.711f, 53.711f);
    Check(Math.Abs(offset - 5f) < 0.001f, "Location ground offset was not recovered from the proxy");
    var adjusted = TerrainReference.ApplyVerticalOffset(new UnityEngine.Vector3(10f, 53.711f, 20f), offset);
    Check(Math.Abs(adjusted.y - 48.711f) < 0.001f, "Terrain reference did not remove the location ground offset");
    Check(Math.Abs(adjusted.x - 10f) < 0.001f && Math.Abs(adjusted.z - 20f) < 0.001f, "Terrain reference changed horizontal coordinates");
    var negative = TerrainReference.ApplyVerticalOffset(new UnityEngine.Vector3(0f, 8f, 0f), TerrainReference.CalculateVerticalOffset(10f, 8f));
    Check(Math.Abs(negative.y - 10f) < 0.001f, "Negative location offsets were not preserved");
    System.Console.WriteLine("PASS: dedicated terrain location-reference checks");
  }

  internal static void RunSerializedTerrainChecks()
  {
    void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    const int width = 64;
    var count = (width + 1) * (width + 1);
    var heights = new float[count];
    var mask = new UnityEngine.Color[count];
    for (var index = 0; index < count; index++)
    {
      heights[index] = 10f;
      mask[index] = new UnityEngine.Color(0f, 0f, 0f, 1f);
    }
    var state = SerializedTerrain.TerrainState.Load(null, heights, mask, UnityEngine.Vector3.zero, width, 1f);
    var settings = new TerrainOp.Settings
    {
      m_level = true,
      m_levelRadius = 2f,
      m_smooth = true,
      m_smoothRadius = 3f,
      m_smoothPower = 3f
    };
    Check(state.Apply(new UnityEngine.Vector3(0f, 12f, 0f), settings), "serialized terrain operation changed no cells");
    state.Operations++;
    state.LastOperationPoint = new UnityEngine.Vector3(0f, 12f, 0f);
    state.LastOperationRadius = settings.GetRadius();
    var first = state.Save();
    var restored = SerializedTerrain.TerrainState.Load(Utils.Compress(first), heights, mask,
      UnityEngine.Vector3.zero, width, 1f);
    var second = restored.Save();
    Check(first.SequenceEqual(second), "native terrain payload did not round-trip byte-for-byte");
    Check(restored.Operations == 1, "serialized terrain operation receipt was lost");
    Check(Math.Abs(restored.SampleHeight(UnityEngine.Vector3.zero) - 13f) < 0.001f,
      "serialized level/smooth ordering did not match TerrainComp's deferred Heightmap rebuild");
    System.Console.WriteLine("PASS: serialized dedicated-server terrain round trip");
  }
}
