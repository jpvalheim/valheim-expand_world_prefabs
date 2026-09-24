namespace ExpandWorld.Prefab;

internal readonly struct TerrainZoneResult(bool success, string reason)
{
  internal readonly bool Success = success;
  internal readonly string Reason = reason;
}

internal static class TerrainSync
{
  internal const int RevisionAdvance = 100;

  internal static void Commit(ZDO compiler, byte[] payload)
  {
    compiler.Set(ZDOVars.s_TCData, payload);
    // A remote owner may have an in-flight compiler revision. Advance past it
    // and explicitly queue this authoritative serialized write for peers.
    compiler.DataRevision += RevisionAdvance;
    ZDOMan.instance.ForceSendZDO(compiler.m_uid);
  }

  internal static void PublishNativeSave(ZDO compiler)
  {
    compiler.DataRevision += RevisionAdvance;
    ZDOMan.instance.ForceSendZDO(compiler.m_uid);
  }
}
