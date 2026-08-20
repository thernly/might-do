# The might-do workspace format, version 1

This describes the files might-do keeps in the user's chosen folder. It exists
because those files, not the application, are the durable artefact: the folder
outlives any one implementation, is expected to be read and written by more than
one build of the app across synced machines, and — per
[ADR-0001](../adr/0001-file-per-task-json-storage.md) — is meant to stay
readable in a text editor if might-do stops existing.

The machine-readable half of this document is [`fixtures/`](../../fixtures/).
Where prose and fixtures disagree, the fixtures win. They were generated from
the running Flutter implementation, which has since been removed; they are held
in place by `tests/MightDo.Core.Tests/` and can no longer be regenerated.

## What compatibility means here

**An implementation is compatible if it reads every value another
implementation wrote, and writes values that survive the trip back.** Two
implementations may format their JSON differently and still both be correct.

Byte-identical output is *not* required and is not worth contorting a port to
achieve. It is worth approximating, for the reasons in ADR-0001 — the files are
meant to be greppable, and a sync client resolving a conflict is easier to read
when a one-field edit produced a one-line diff — so the house style below is a
recommendation, not a conformance rule.

| | Requirement |
|---|---|
| **Hard** | Key names and value encodings; ULID filenames; atomic writes; the read-tolerance rules; refusing files with a newer `schemaVersion`; ordinal comparison of `boardRank` |
| **House style** | Two-space indent, trailing newline, the key order shown below, unescaped non-ASCII, lowercase ULIDs, nullable fields written as explicit `null` |

## Folder layout

```
<workspace root>/
  config.json                  statuses, categories, tags
  tasks/<ulid>.json            one file per task
  attachments/<stored-name>    attached files, copied in
  .trash/tasks/<ulid>.json     deleted tasks, never purged automatically
  .trash/attachments/…         their attachments
```

Missing directories are created on load. A folder is recognised as an existing
workspace by the presence of `config.json`; without it, one is seeded with six
starter statuses and no categories or tags.

Several workspaces are simply several such folders. Nothing in one refers to
another, none of them records that the others exist, and a workspace cannot
tell whether it is the only one — so this format is the same whether the user
keeps one or ten.

The workspace folder does **not** record which machine it is on, its own name in
the switcher, the window layout, or the last-used view. Those are machine-local preferences and live in
the platform's own settings store — the folder is at a different path on every
machine, so a path stored inside it would be wrong everywhere but where it was
written. A port needs its own equivalent; nothing about it is part of this
format.

## `config.json`

The one shared file, and therefore the one real conflict hotspot. Accepted
deliberately: status and category edits are rare.

| Key | Type | Notes |
|---|---|---|
| `schemaVersion` | int | `1`. A higher value is refused, not loaded — see [Reading](#reading). |
| `defaultStatusId` | string | Status new tasks start in. Always a status of type `initial`. Means nothing else. |
| `statuses` | array | Written sorted by `order`. |
| `categories` | array | |
| `tags` | array | |

**Status** — `id`, `name`, `type`, `order` (int, also left-to-right column
order), `hiddenFromBoard` (bool, default `false`).

`type` is one of `initial`, `active`, `final` — a closed set, not user-editable,
and the wire values are stable regardless of what the enum is called in the host
language. See
[ADR-0002](../adr/0002-statuses-are-user-data-typed-by-a-closed-set.md). Several
statuses share a type routinely; there is no "the done status".

**Category** — `id`, `name`, `color`.

`color` is a **32-bit ARGB integer**, and an opaque colour exceeds
`0x7FFFFFFF`. Type it as unsigned or 64-bit; a signed 32-bit field overflows on
every fully opaque colour the app has ever written.

**Tag** — `id`, `name`.

## A task file

One task per file, named `<id>.json`. Key order below is the on-disk order.

| Key | Type | Notes |
|---|---|---|
| `schemaVersion` | int | `1`. A higher value is refused, not loaded — see [Reading](#reading). |
| `id` | string | ULID; matches the filename |
| `summary` | string | |
| `description` | string | `""` when unset, never null |
| `statusId` | string | |
| `categoryId` | string? | At most one |
| `tagIds` | string[] | Max 10 |
| `priority` | string | `low` \| `medium` \| `high` \| `critical` |
| `dueDate` | string? | **Calendar day**, `yyyy-MM-dd` — see below |
| `completedAt` | timestamp? | Set by the app on entering a `final` status, cleared on leaving one |
| `estimateMinutes` | int? | Whole minutes |
| `totalTimeMinutes` | int? | Whole minutes, entered by hand |
| `boardRank` | string | Fractional index — see below |
| `steps` | array | `id`, `text`, `done` |
| `notes` | array | `id`, `createdAt`, `body` |
| `attachments` | array | `id`, `originalName`, `storedName`, `sizeBytes`, `addedAt` |
| `reminders` | array | `id`, `remindAt`, `firedAt?`, `dismissedAt?` |
| `createdAt` | timestamp | |
| `updatedAt` | timestamp | Stamped by changes the user made to the task — see below |

`updatedAt` means *the user changed this task*: editing a field, adding a note,
ticking a step, retagging, moving it to another status or position, attaching or
removing a file. It is deliberately **not** stamped by reminder
bookkeeping — firing happens on a timer to a task nobody touched, and dismissing
one acknowledges a notification rather than changing the task it points at — or
by a task being rewritten because a status, category or tag it used was deleted
in settings — a workspace-wide tidy-up that marked everything as freshly updated
would empty the field of meaning. Sorting by "recently updated" is what the
field is for, so it has to answer one question.

Steps are ordered by their position in the array and carry no status or dates.
Notes are append-only in practice — they are a running commentary, not editable
history. A reminder is *pending* while `firedAt` and `dismissedAt` are both
null, and *outstanding* while `dismissedAt` is null.

`storedName` is the attachment's name inside `attachments/`, prefixed with the
attachment id so two files called `contract.pdf` cannot collide.
`originalName` is what the user sees.

### Dates and times are two different things

This distinction is the format's sharpest edge, and collapsing it is the single
most common way to corrupt this data.

- **`dueDate` is a calendar day**: `2026-08-21`, no time and no zone. A task is
  due *on the 21st*, not at midnight on the 21st. Represent it with a date-only
  type. Storing it as an instant and rendering it in another zone moves it to
  the 20th.
- **Every other date field is a real instant**, written UTC in ISO-8601.

Timestamps are written with a `Z` suffix and three or six fractional digits.
Readers must accept any valid ISO-8601 instant — a zone offset instead of `Z`,
no fractional part, or more digits than we write — and convert to UTC. .NET's
round-trip (`"O"`) format is read correctly; its seventh fractional digit is
truncated, which loses less than a microsecond and is not worth avoiding.

### `boardRank`

A fractional index: a base-36 string (`0-9a-z`) that sorts lexicographically.
Dropping a card between two others generates a rank between their two ranks,
rewriting **one** task file rather than renumbering the column — which matters
because a reorder that touched every file would be the worst possible shape for
per-file sync. One field covers every column, since columns hold disjoint tasks.

No rank ever ends in `0`; that invariant is what guarantees room to insert
before any existing rank.

Sort ranks by **ordinal** string comparison. In C# this needs saying out loud:
`string.CompareTo` and the default `Comparer<string>` are culture-sensitive, and
a culture-sensitive sort will silently produce the wrong board order. Use
`StringComparer.Ordinal`.

`fixtures/vectors/ranks.json` gives inputs, expected outputs and the rejected
cases.

## Reading

Be liberal, and never lose a task quietly.

- **Absent optional keys are legal.** Defaults: `description` → `""`,
  `tagIds` and the four arrays → empty, `priority` → `medium`, `boardRank` →
  `"i"`, `createdAt`/`updatedAt` → now. This is why the writer emits explicit
  nulls but the reader must not depend on them.
- **Unknown keys are dropped**, so round-tripping a file written by a future
  version discards what that version added. Recorded here as a known property of
  the format rather than a bug — a port should behave the same way rather than
  invent its own preservation scheme.
- **`schemaVersion` is read, not assumed.** Dropping unknown keys is only safe
  while nothing writes the file back, so a file whose `schemaVersion` is higher
  than the implementation supports is refused rather than loaded: a task file is
  reported as unreadable (the same path a broken file takes) and never reaches
  memory, so no save can downgrade it, and a newer `config.json` refuses to open
  the workspace at all — it defines the statuses every task refers to. An
  implementation must never write a file carrying a version it does not
  understand. Absent `schemaVersion` means `1`.
- **A file that fails to parse is reported, not skipped.** A task that silently
  vanishes is worse than one that shows up broken. A `config.json` that fails to
  parse refuses the whole workspace, by name and with the reason: it defines the
  statuses every task refers to, so seeding a fresh one over it would orphan
  every task in the folder. Only a *missing* config is seeded — never an
  unreadable one, and the unreadable file is left exactly as it was found.
- **Parsing is not the same as being usable.** A required key being present says
  nothing about its value: `"summary": null`, `"steps": [null]` and
  `"reminders": null` all parse. The whole object is checked at the boundary —
  required strings, collections and their elements, enum values, non-negative
  numbers, ids that are not repeated — so a hand-edited or sync-merged file
  becomes one broken task beside the working ones rather than something that
  fails later, in the middle of drawing the workspace it belongs to.
- **A file too large to be one of ours is refused before it is read.** No task
  or config written by this format approaches 16 MB; a file that does is a
  truncated download or a sync client's mistake, and reading it into memory to
  find that out is the thing worth avoiding.
- **An empty file reads as absent**, not as an error.
- Only files in `tasks/` whose names are ULIDs are loaded (see below).
- **Names that become paths are checked, not trusted.** A file that parses is
  still hand-editable input: `id` must be a ULID and must equal the filename,
  and every `storedName` must be `<attachment-ulid>-<file name>` with no
  separator (`/` or `\`, on every platform), drive or stream qualifier, or
  `..`. A file breaking either rule is reported as a broken file rather than
  loaded — otherwise the next save, delete, or trash of that task would write,
  remove, or move a file outside the workspace.
- **The folders the app owns must be real folders.** (Single files are not
  checked, and do not need to be: every write is a rename over the target, which
  replaces a link rather than following it.) A plain name is only half
  the boundary: if `tasks/`, `attachments/` or `.trash/` (or either folder
  inside it) is a symlink, junction or other reparse point, the escape happens
  while the path is resolved, after every name has passed its check. Any of
  them being a link refuses the workspace, and the check is repeated before
  each write rather than done once at open, because a link can be swapped in
  while the app is running. The root itself is exempt: whatever it resolves to
  is the folder the user chose, and that *is* the boundary.

## Writing

Write to a temporary sibling and rename over the target. The rename is atomic on
every platform we ship to; without it, a sync client eventually uploads a
half-written task and you get a corrupt file instead of a resolvable conflict.
Never hold a file handle open.

**The temporary name must be unique per write** — `<target>.<random>.tmp`, not
`<target>.tmp`. A shared name is fine until two writers overlap, and then each
is writing the file the other is about to rename: one save fails, or the right
name ends up carrying the wrong writer's bytes.

Some Windows configurations refuse a rename onto an existing file. The fallback
moves the target aside to a temporary name, renames the new file into place, and
only then deletes what it moved aside — putting it back if the second rename
fails too. Deleting the target first instead means a transient share, quota or
network error takes the last good copy with it.

**The folder has to be there already.** Nothing on the write path creates the
workspace or its subfolders: a workspace that has gone is an unmounted drive, a
folder moved on another machine, or one the user deleted, and a save that
recreates it leaves a fragment of a workspace at the old path for the real
folder to collide with when it comes back. Writes into a missing workspace are
refused, and so are reloads. Only the explicit "make a workspace here" path
creates anything.

### Checking before overwriting

A rename is atomic, but atomicity says nothing about *lost updates*: the reader
and the writer of a synced folder are different machines, and a watcher is a
hint that may arrive after the save it should have preceded. So before replacing
a file, compare what is on disk against the contents it was read from. If they
differ, somebody else wrote it since — rename theirs aside as

```
<name> (conflicted copy <yyyy-MM-dd HHmmss>)<.ext>
```

in the same folder, then write. The user's save still lands, their own copy is
kept in the shape the sync clients already use, and the next rescan reports it
alongside every other conflict artefact. A file that has gone missing needs no
copy: the save simply puts it back.

### Two writers at once

Comparing before overwriting keeps a save from destroying an edit it never saw,
but the comparison and the replacement are separate filesystem operations: two
writers that both read version V can both find V still there, and the loser's
edit goes without even a conflict copy.

On one machine that is closed properly: the whole compare-preserve-replace
sequence is held under a lock, taken by opening a per-workspace file
exclusively. The lock file lives beside the machine's temporary files rather
than in the workspace, so nothing synced, backed up or looked at by the user
ever contains it, and it is held by an open handle rather than by the file
existing — a process that crashes releases it. A writer that cannot take the
lock is refused, and nothing is written: a change that did not happen can be
repeated, and an edit overwritten by another process cannot be recovered. The
one exception is a machine that cannot create a lock file at all — a read-only
or sandboxed temporary folder — where waiting would refuse every change the user
ever makes for a lock that is never coming.

The lock covers every mutation, not only saves: a task file, `config.json`,
trashing and restoring a task, and detaching an attachment. Moving several files
is no more atomic than compare-and-replace is, and an interleaved save would
otherwise put back the task file a trash had just taken away, leaving an active
task whose attachments are all in `.trash/`.

Across machines nothing is closed, and nothing can be: a lock file inside a
synced folder arrives seconds or minutes after it was taken. Two machines editing
the same task at the same moment is left to the sync client, whose conflict copy
the app then reports like any other. This is a real limitation of the format, not
an oversight.

## Filenames, and the files we did not write

Task filenames are exactly a 26-character Crockford base32 ULID (the alphabet
omits `I`, `L`, `O` and `U`) plus `.json`. Matching is **case-insensitive**: this
app writes lowercase, but a sync client or a case-insensitive filesystem may
hand the name back in another case, and a task must never be mistaken for a
foreign file over casing.

ULIDs are used because they are collision-free across machines editing offline
and sort chronologically, so a directory listing is in creation order.

Because the naming rule is strict, **anything else in `tasks/` came from
somewhere else** — in practice a sync client's conflict copy:

| Client | Shape |
|---|---|
| OneDrive | `01m….json` → `01m…-LAPTOP.json` |
| Dropbox | `01m… (conflicted copy 2026-08-16).json` |
| iCloud Drive | `01m… 2.json` |
| this app | `01m… (conflicted copy 2026-08-16 141002).json` |

These are surfaced in the app for the user to resolve, never loaded and never
ignored — silently skipping them is how you discover months later that an edit
was lost. The task id is recovered by finding an embedded 26-character ULID in
the name, and is null when there isn't one. Our own in-flight `.tmp` files are
the one exception and are skipped.

The workspace root is scanned the same way: any `config*.json` that is not
`config.json` is somebody else's copy of the one shared file, and a lost status
or category is as much a lost edit as a task is.

## Deletion

Deleting moves the task file, and its attachments, into `.trash/` — deliberately
not a `deleted` flag. Keeping trashed tasks out of every query by construction
means no filter can ever forget to exclude them. Files in the trash keep exactly
the shape above. If a name is already taken there, the incoming file gets a
millisecond timestamp appended rather than clobbering what is there. Nothing
purges the trash automatically: destroying data on a timer is worse than a
folder that grows.

Removing a single attachment from a task moves its bytes into
`.trash/attachments/` too; nothing in a workspace is deleted outright.

Restoring a task whose canonical file has come back — a sync client putting it
there while the task sat in the trash — keeps that file as a conflict copy and
restores under the task's own name. Restoring beside it under a different name
would report success while the task's file said something else, and the next
rescan would silently undo the restore. If the rest of the restore then fails
and is put back, the conflict copy stays where it is: it is the only copy of
that version, and it is reported like any other.

Trashing and restoring move several files with no way to do them as one
operation, so a run that fails partway puts back whatever it already moved. A
failed trash leaves the task active with its attachments beside it, and a failed
restore leaves the whole task in `.trash/` — never an active task pointing at
files that are already in the trash.

## The fixtures

| Path | What it is |
|---|---|
| `fixtures/workspace-v1/` | A real workspace, written by the Flutter implementation. Five tasks, one trashed task, an attachment, and four conflict artefacts. |
| `fixtures/tolerance/` | Non-canonical input under `input/`, and the canonical form each becomes under `expected/`. |
| `fixtures/vectors/` | Inputs and expected outputs for the pure functions: ranks, calendar-date parsing, filename classification, conflict detection. |
| `fixtures/interop/dotnet-written/` | The same workspace, written by the .NET implementation, plus one task it created from scratch. |

The five tasks are chosen to be awkward: one with every field populated and
every collection non-empty; one with nothing optional set; one carrying emoji,
CJK, quotes, backslashes, a control character and HTML-sensitive characters; one
in a `final` status with a completion date; one with a deep fractional rank.

A port passes when it can load `workspace-v1/`, write it back without changing
any value, normalise the `tolerance/` inputs to their expected forms, and
reproduce the `vectors/` outputs.

That only proves one direction, so the corpora were checked both ways while
both implementations existed. Each read what the other wrote:

| Direction | Written by | Read and checked by |
|---|---|---|
| Flutter → .NET | `tool/generate_fixtures.dart` → `workspace-v1/` | `tests/MightDo.Core.Tests/` |
| .NET → Flutter | `tools/MightDo.FixtureWriter` → `interop/dotnet-written/` | `test/format/interop_test.dart` |

Both corpora are committed, so they still run with the Flutter side gone — but
only the first row can still be checked. `tools/MightDo.FixtureWriter` rewrites
`interop/dotnet-written/`; nothing reads it now, so a change there is a change
to what a future implementation is written against, and needs justifying rather
than regenerating.

In practice the two agree closely: of the seven files in the canonical
workspace, six are byte-identical between implementations and the seventh
differs only in the astral-plane escaping described above.

## Other formats

[`csv-v1`](csv-v1.md) is what might-do writes when you export tasks and reads
when you import them. It is a **view** of a workspace shaped for a spreadsheet,
not a second copy of it: it carries names where this format carries ids, it
leaves out attachments and board positions, and it is not a backup. This format
is the backup — copy the folder. See
[ADR-0005](../adr/0005-csv-is-interchange-not-backup.md).

## Notes for the .NET port

Findings from the Dart implementation, verified rather than assumed:

- `System.Text.Json` escapes non-ASCII **and** HTML-sensitive characters
  (`<`, `>`, `&`, `'`) by default, so `Café — 日本語` is written as a wall of
  `\uXXXX`. Valid JSON and read back correctly, but it defeats the greppability
  ADR-0001 is built on. `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` is the
  closest match to what is on disk today.
- **Astral-plane characters are the one divergence we accept.** Every
  `System.Text.Json` encoder — `UnsafeRelaxedJsonEscaping`,
  `Create(UnicodeRanges.All)`, a hand-built `TextEncoderSettings` — writes
  characters outside the BMP as surrogate pairs, so an emoji in a summary
  becomes `🎉` where Flutter writes `🎉`. BMP text (`é`, `—`, `日本語`)
  stays literal in both. The two files parse to identical values, which is what
  compatibility means here; eliminating it would need a custom `Utf8JsonWriter`
  and is not worth it. Worth knowing before someone diffs two files and thinks
  something is broken.
- `Category.color` overflows `int`. Use `uint`.
- Compare `boardRank` with `StringComparer.Ordinal`, never the default.
- Emit lowercase ULIDs to match existing files. Not required — readers are
  case-insensitive — but it keeps a mixed-implementation folder tidy.
- `FileSystemWatcher` is the weak point. Live-reloading files a sync client
  rewrote underneath us is not optional here; ADR-0001 notes the design loses
  data without it, and macOS behaviour is worth a spike before committing.
