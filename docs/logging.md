# Logging

The `log` field adds text to `BepInEx/config/expand_world/ewp_log.txt` when a rule runs.

```yaml
- prefab: Player
  type: state, join
  log: "<realtime> • <pname> • joined"
```

The short scalar form writes one record. To write multiple independent records,
use a YAML sequence under the same `log` field. Entries are formatted and queued
in declaration order.

```yaml
- prefab: Player
  type: state, join
  log:
  - "<realtime> • <pname> • joined"
  - "<realtime> • audit • <cid>"
```

- A scalar containing `;;` writes those characters literally; no separator is reserved.
- Each YAML sequence item is one record. Runtime substitution cannot add records.
- Multiple records from one rule share its per-rule rate bucket. Each record uses
  its own global-rate, queue, memory and size allowance.
- A formatting failure disables only the affected template until YAML reload;
  sibling records remain eligible.

- Functions and object substitutions are supported.
- Logging runs after rule selection and chance checks, before other actions.
  - A record means the rule started its actions. It does not confirm that later actions succeeded.
- Only the server writes records. This includes single player and the host of a local server.
- Existing active contents are appended after restart unless the file already
  meets the segment limit. In that case, rolling mode archives it on startup
  and applies the configured retention bounds.
- Each log value adds a record and a newline. Newlines within a value are kept.
- Timestamps are not added automatically. Use `<time>` for game time or `<realtime>` for real time.
- Worlds using the same server installation share the file.

## Configuration

Settings are in `BepInEx/config/expand_world_prefabs.cfg`.

- Rule logging (default: `true`): Enables the `log` action.
  - Can be changed while running. Pending records are written before the file closes.
- Records per second (default: `1000`): Refill rate shared by all rules.
  - Allows a burst of up to `100` records, or the configured rate if lower.
- Records per rule per second (default: `250`): Refill rate shared by all objects using one rule.
  - Allows a burst of up to `25` records, or the configured rate if lower.
- Flush interval milliseconds (default: `1000`): How often pending output is flushed.
  - Output is also flushed after `64 KiB`.
- Retention mode (default: `Rolling`): Archives a full active segment and keeps
  logging. `StopAtLimit` preserves the former hard-stop behavior.
- Segment MiB (default: `32`): Active-file rollover threshold in rolling mode.
- Retained segments (default: `8`): Maximum completed segments.
- Maximum file MiB (default: `256`): Total completed-segment budget in rolling
  mode, or the active-file ceiling in `StopAtLimit` mode.
- Rate, flush and retention settings require restarting the game.

## Performance

Text is resolved when the rule runs. A background thread writes and flushes the file, so the rule does not wait for disk access.

- Rate and queue limits are checked before resolving functions.
- The queue holds at most `4096` records and `4 MiB` of charged text storage, including reservations and the record being written.
  - Storage is charged as two bytes per character plus 64 bytes per record. Queue and stream buffers use additional memory.
- A template or resolved record can contain at most `8192` UTF-16 characters. Longer records are skipped.
- Limits skip records instead of delaying other actions or growing the queue indefinitely.
- Functions still run on the calling thread. Expensive functions can affect gameplay even when file writes are buffered.

## Missing records

Logging is intended for development and administration. It does not guarantee that every record reaches disk.

- Skipped records are counted in an `[EWP LOG GAP]` summary, normally at most once every 30 seconds and once when stopping.
  - Summaries are sent to the BepInEx log and the file. They do not mark the exact position of missing records.
  - A busy rule has its own rate limit, but many busy rules can still fill the shared queue.
- If a function throws, that log template is disabled until YAML reload. Other templates and actions can still run.
- A file error disables logging until restart. Toggling the setting or reloading YAML does not retry it.
- Shutdown waits up to one second for the writer. A crash, forced shutdown or stalled disk can lose pending records.
- Rolling is performed only by the single background writer: it flushes and
  closes `ewp_log.txt`, renames it, opens a new active file, and then deletes
  the oldest completed segments when either retention bound is exceeded.
- An already-oversized active file is rolled intact the next time logging opens.
  `[EWP LOG ROTATE]` records identify startup and full-segment rollover.
- For analysis, collect the active file and every retained `ewp_log.*.txt`
  segment. Their timestamped names preserve segment order.
