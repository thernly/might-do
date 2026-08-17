# The might-do workspace format, version 1

This describes the files might-do keeps in the user's chosen folder. It exists
because those files, not the application, are the durable artefact: the folder
outlives any one implementation, is expected to be read and written by more than
one build of the app across synced machines, and — per
[ADR-0001](../adr/0001-file-per-task-json-storage.md) — is meant to stay
readable in a text editor if might-do stops existing.

The machine-readable half of this document is [`fixtures/`](../../fixtures/).
Where prose and fixtures disagree, the fixtures win; they are generated from the
running code by `dart run tool/generate_fixtures.dart` and held in place by
`test/format/fixture_conformance_test.dart`.

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
| **Hard** | Key names and value encodings; ULID filenames; atomic writes; the read-tolerance rules; ordinal comparison of `boardRank` |
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

The workspace folder does **not** record which machine it is on, the window
layout, or the last-used view. Those are machine-local preferences and live in
the platform's own settings store — the folder is at a different path on every
machine, so a path stored inside it would be wrong everywhere but where it was
written. A port needs its own equivalent; nothing about it is part of this
format.

## `config.json`

The one shared file, and therefore the one real conflict hotspot. Accepted
deliberately: status and category edits are rare.

| Key | Type | Notes |
|---|---|---|
| `schemaVersion` | int | `1` |
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
| `schemaVersion` | int | `1` |
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
| `updatedAt` | timestamp | |

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
- **A file that fails to parse is reported, not skipped.** A task that silently
  vanishes is worse than one that shows up broken.
- **An empty file reads as absent**, not as an error.
- Only files in `tasks/` whose names are ULIDs are loaded (see below).

## Writing

Write to a temporary sibling and rename over the target. The rename is atomic on
every platform we ship to; without it, a sync client eventually uploads a
half-written task and you get a corrupt file instead of a resolvable conflict.
Never hold a file handle open.

Some Windows configurations refuse a rename onto an existing file. The fallback
is to delete the target first, which leaves a very small window where it is
absent — still far better than a partial write.

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

These are surfaced in the app for the user to resolve, never loaded and never
ignored — silently skipping them is how you discover months later that an edit
was lost. The task id is recovered by finding an embedded 26-character ULID in
the name, and is null when there isn't one. Our own in-flight `.tmp` files are
the one exception and are skipped.

## Deletion

Deleting moves the task file, and its attachments, into `.trash/` — deliberately
not a `deleted` flag. Keeping trashed tasks out of every query by construction
means no filter can ever forget to exclude them. Files in the trash keep exactly
the shape above. If a name is already taken there, the incoming file gets a
millisecond timestamp appended rather than clobbering what is there. Nothing
purges the trash automatically: destroying data on a timer is worse than a
folder that grows.

## The fixtures

| Path | What it is |
|---|---|
| `fixtures/workspace-v1/` | A real workspace. Five tasks, one trashed task, an attachment, and four conflict artefacts. |
| `fixtures/tolerance/` | Non-canonical input under `input/`, and the canonical form each becomes under `expected/`. |
| `fixtures/vectors/` | Inputs and expected outputs for the pure functions: ranks, calendar-date parsing, filename classification, conflict detection. |

The five tasks are chosen to be awkward: one with every field populated and
every collection non-empty; one with nothing optional set; one carrying emoji,
CJK, quotes, backslashes, a control character and HTML-sensitive characters; one
in a `final` status with a completion date; one with a deep fractional rank.

A port passes when it can load `workspace-v1/`, write it back without changing
any value, normalise the `tolerance/` inputs to their expected forms, and
reproduce the `vectors/` outputs.

## Notes for the .NET port

Findings from the Dart implementation, verified rather than assumed:

- `System.Text.Json` escapes non-ASCII **and** HTML-sensitive characters
  (`<`, `>`, `&`, `'`) by default, so `Café — 日本語` is written as a wall of
  `\uXXXX`. Valid JSON and read back correctly, but it defeats the greppability
  ADR-0001 is built on. `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` matches
  what is on disk today.
- `Category.color` overflows `int`. Use `uint`.
- Compare `boardRank` with `StringComparer.Ordinal`, never the default.
- Emit lowercase ULIDs to match existing files. Not required — readers are
  case-insensitive — but it keeps a mixed-implementation folder tidy.
- `FileSystemWatcher` is the weak point. Live-reloading files a sync client
  rewrote underneath us is not optional here; ADR-0001 notes the design loses
  data without it, and macOS behaviour is worth a spike before committing.
