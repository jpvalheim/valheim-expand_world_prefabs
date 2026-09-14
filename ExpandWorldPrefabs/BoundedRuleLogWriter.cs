using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ExpandWorld.Prefab;

internal enum RuleLogRetentionMode
{
  Rolling,
  StopAtLimit
}

internal sealed class RuleLogFileLimitException : IOException { }

/// <summary>
/// Single-owner rule-log sink. Rolling mode closes and archives the active
/// file before opening its replacement, so no producer or maintenance thread
/// can race the writer. StopAtLimit preserves the previous hard-stop behavior.
/// </summary>
internal sealed class BoundedRuleLogWriter : TextWriter
{
  private readonly string Path;
  private readonly Encoding FileEncoding;
  private readonly long SegmentBytes;
  private readonly long RetentionBytes;
  private readonly int RetainedSegments;
  private readonly RuleLogRetentionMode Mode;
  private readonly Action<string> Report;
  private TextWriter? Inner;
  private long WrittenBytes;
  private int ArchiveSequence;

  internal BoundedRuleLogWriter(string path, Encoding encoding, long segmentBytes,
    long retentionBytes, int retainedSegments, RuleLogRetentionMode mode, Action<string> report)
  {
    if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(nameof(path));
    if (segmentBytes < 1 || retentionBytes < 1 || retainedSegments < 1) throw new ArgumentOutOfRangeException();
    Path = path;
    FileEncoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
    SegmentBytes = mode == RuleLogRetentionMode.StopAtLimit ? retentionBytes : Math.Min(segmentBytes, retentionBytes);
    RetentionBytes = retentionBytes;
    RetainedSegments = retainedSegments;
    Mode = mode;
    Report = report ?? (_ => { });

    var directory = System.IO.Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    WrittenBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
    if (Mode == RuleLogRetentionMode.Rolling && WrittenBytes >= SegmentBytes)
      Rotate("startup");
    else
      Inner = OpenAppend();
  }

  public override Encoding Encoding => FileEncoding;

  public override void WriteLine(string? value)
  {
    var bytes = (long)Encoding.GetByteCount(value ?? "") + Encoding.GetByteCount(NewLine);
    if (bytes > SegmentBytes) throw new RuleLogFileLimitException();
    if (WrittenBytes > SegmentBytes || bytes > SegmentBytes - WrittenBytes)
    {
      if (Mode == RuleLogRetentionMode.StopAtLimit) throw new RuleLogFileLimitException();
      Rotate("segment_full");
    }
    Inner!.WriteLine(value);
    WrittenBytes += bytes;
  }

  public override void Flush() => Inner?.Flush();

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      Inner?.Dispose();
      Inner = null;
    }
    base.Dispose(disposing);
  }

  private TextWriter OpenAppend()
  {
    var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536);
    try { return new StreamWriter(stream, FileEncoding, 16384) { AutoFlush = false }; }
    catch { stream.Dispose(); throw; }
  }

  private void Rotate(string reason)
  {
    Inner?.Flush();
    Inner?.Dispose();
    Inner = null;

    string? archived = null;
    if (File.Exists(Path) && new FileInfo(Path).Length > 0)
    {
      archived = NextArchivePath();
      File.Move(Path, archived);
    }

    var removed = TrimArchives();
    WrittenBytes = 0;
    Inner = OpenAppend();
    var notice = "[EWP LOG ROTATE] reason=" + reason +
      " archived=" + (archived == null ? "none" : System.IO.Path.GetFileName(archived)) +
      " removed=" + removed.ToString(CultureInfo.InvariantCulture) + ".";
    Inner.WriteLine(notice);
    WrittenBytes += Encoding.GetByteCount(notice) + Encoding.GetByteCount(NewLine);
    Inner.Flush();
    try { Report(notice); } catch { /* File retention must not depend on reporting. */ }
  }

  private string NextArchivePath()
  {
    var directory = System.IO.Path.GetDirectoryName(Path) ?? "";
    var stem = System.IO.Path.GetFileNameWithoutExtension(Path);
    var extension = System.IO.Path.GetExtension(Path);
    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
    string candidate;
    do
    {
      candidate = System.IO.Path.Combine(directory,
        stem + "." + timestamp + "." + (ArchiveSequence++).ToString("D3", CultureInfo.InvariantCulture) + extension);
    }
    while (File.Exists(candidate));
    return candidate;
  }

  private int TrimArchives()
  {
    var directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
    var stem = System.IO.Path.GetFileNameWithoutExtension(Path);
    var extension = System.IO.Path.GetExtension(Path);
    var archiveName = new Regex("^" + Regex.Escape(stem) + @"\.\d{8}T\d{9}Z\.\d{3}" +
      Regex.Escape(extension) + "$", RegexOptions.CultureInvariant);
    var files = Directory.GetFiles(directory, stem + ".*" + extension)
      .Where(file => !string.Equals(file, Path, StringComparison.OrdinalIgnoreCase))
      .Where(file => archiveName.IsMatch(System.IO.Path.GetFileName(file)))
      .Select(file => new FileInfo(file))
      .OrderBy(file => file.LastWriteTimeUtc)
      .ThenBy(file => file.Name, StringComparer.Ordinal)
      .ToList();
    long total = files.Sum(file => file.Length);
    var removed = 0;
    while (files.Count > 0 && (files.Count > RetainedSegments || total > RetentionBytes))
    {
      var file = files[0];
      files.RemoveAt(0);
      total -= file.Length;
      file.Delete();
      removed++;
    }
    return removed;
  }
}
