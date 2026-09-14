using System;
using System.IO;
using Data;
using Service;

namespace ExpandWorld.Prefab;

internal static class RuleLog
{
  private static BufferedRuleLog? Writer;
  private static readonly Func<string, Functions, string> Format =
    (template, functions) => functions.Replace(template, false, false);

  public static void Init(string path)
  {
    // Never replace a worker that might still own the file.
    if (Writer != null) return;
    try
    {
      Writer = new BufferedRuleLog(() => Open(path), Config.GetRuleLogOptions(), Log.Warning);
      Writer.SetEnabled(Config.RuleLogging);
    }
    catch (Exception e)
    {
      Log.Error("Unable to start rule logging. Gameplay will continue without it. " + e.Message);
    }
  }

  private static TextWriter Open(string path)
  {
    return new BoundedRuleLogWriter(path, new System.Text.UTF8Encoding(false),
      Config.RuleLogSegmentBytes, Config.RuleLogMaximumFileBytes,
      Config.RuleLogRetainedSegments, Config.RuleLogRetention, Log.Warning);
  }

  public static void Write(RuleLogSource source, Functions functions) => Writer?.TryWrite(source, functions, Format);
  public static void SetEnabled(bool enabled) => Writer?.SetEnabled(enabled);
  public static void Close() => Writer?.Stop(1000);
}
