using NUnit.Framework;
namespace ExpandWorldPrefabs.Tests;
public class TerrainAndTeleportTests
{
  [Test] public void TeleportDestinationSynchronization() => TerrainAndTeleportChecks.RunTeleportDestinationChecks();
  [Test] public void DedicatedTerrainLocationReference() => TerrainAndTeleportChecks.RunTerrainReferenceChecks();
  [Test] public void SerializedDedicatedTerrain() => TerrainAndTeleportChecks.RunSerializedTerrainChecks();
}
