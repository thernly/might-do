# Live reload is a debounced rescan, not an interpretation of watch events

The app watches the workspace folder with the platform's ordinary file watcher
(`FileSystemWatcher` on .NET, `package:watcher` equivalent on Flutter) and treats
every event as nothing more than **"something under the workspace changed, go
and look"**. On the first event it starts a short debounce, and when that expires
it rescans the folder and diffs the result against what is in memory. The event's
kind, and the path it names, are deliberately ignored.

[ADR-0001](0001-file-per-task-json-storage.md) notes the design silently loses
data without live reload, which makes this load-bearing rather than a nicety. It
was measured on .NET 10 (10.0.400) on macOS 25.6 before being decided, with
[`spikes/platform-spike/`](../../spikes/platform-spike/) — re-runnable — and the
numbers below are what came back.

## What the measurements showed

Every scenario we care about was reported, with no dropped events and no
`Error` events: an external create; our own temp-file-then-rename write; an
in-place rewrite; a delete; a conflict artefact with spaces and parentheses in
its name; an uppercase filename on a case-insensitive volume; 400 files created
in a burst; a move into `.trash/`; and `tasks/` being replaced wholesale. First
event arrived in a median of 11ms. The default 8KB internal buffer never
overflowed, even during the burst.

Crucially, the same run against a **real OneDrive folder** under
`~/Library/CloudStorage` — a macOS File Provider volume, not an ordinary
filesystem, and the environment ADR-0001 expects a workspace to live in —
reported every change at 11–12ms as well.

Two results decided the shape:

**The event kind is wrong.** Rewriting an existing file's contents was reported
as `Created`. Moving a file *out* of `tasks/` was also reported as `Created`, at
the source path. Any logic branching on `Created` vs `Changed` vs `Deleted` would
be built on sand.

**One user action is many events.** A single atomic save produced 3 events
locally and 5 inside OneDrive, because the sync client touches the file itself
after we write it. There is no fixed ratio to rely on.

## Considered options

**Interpret events incrementally** — apply each event to the in-memory model as a
targeted add, update or remove. Rejected: it requires the event kind and path to
be trustworthy, and neither is. It also cannot find conflict artefacts, which is
inherently a directory listing rather than an event.

**Poll only, no watcher.** Correct and bulletproof, and .NET ships it as a
first-party option — `PhysicalFileProvider.UsePollingFileWatcher` with
`UseActivePolling`, which exists precisely because `FileSystemWatcher` is
ineffective on mounted and network volumes. Rejected as the *primary* strategy
only because its default 4-second interval is a poor experience next to the 11ms
we measured. Retained as a fallback.

**A third-party watcher library.** Investigated and rejected: the well-regarded
ones are Windows-only (`FileWatcherEx`, `MiniFSWatcher`, `SmartDirWatcher` — the
last two are built on Win32 APIs and a Windows minifilter driver), and the
cross-platform ones are abandoned (`acken/FSWatcher`, .NET/Mono era). There is no
maintained cross-platform alternative worth a dependency, and once events are
only a hint, their quality barely matters.

**Watcher as a hint, debounced rescan as the mechanism (chosen).** The watcher
supplies latency; the rescan supplies correctness. Every "did we sequence the
events right?" bug disappears because we never sequence them.

## Consequences

- Debouncing is mandatory, not an optimisation — 3 to 5 events per save would
  otherwise mean 3 to 5 rescans. A couple of hundred milliseconds is enough to
  coalesce what we measured.
- **Never write to the workspace in response to a watch event.** The sync client
  generates events for files we just wrote; responding to those with another
  write is a feedback loop.
- A rescan re-reads every task file, which ADR-0001 already accepts as the cost
  model — queries are our code over an in-memory collection. This holds at
  hundreds or low thousands of tasks and would not at fifty thousand. It is the
  same bound, not a new one.
- Because the whole folder is rescanned, conflict artefacts and externally
  deleted tasks are found for free, on the same path as ordinary edits.
- **Deleting the watched root produced no events at all**, though the watcher
  recovered by itself once the folder reappeared. Detecting "the workspace has
  gone" — an unmounted drive, a moved OneDrive folder — therefore needs a
  separate existence check, not a watch event. Without it the app will sit
  showing stale tasks for a folder that is no longer there.
- A manual refresh stays in the UI regardless. It costs nothing and is the
  honest answer when a platform lies to us.
- Polling remains available as a per-workspace escape hatch for volumes where
  the watcher turns out to be unreliable in the field.

## What is still unverified

The spike drove local writes into a cloud folder. It did **not** test a change
arriving *from another machine*, where the File Provider materialises a file that
was previously a dataless placeholder — the one case that needs two machines to
observe. If live reload is going to fail anywhere, it is there. The periodic
existence check and the manual refresh are what stand between that failure and
data loss, which is a further reason to keep both.
