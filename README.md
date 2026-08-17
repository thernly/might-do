# might-do

A personal task tracker for people who find Microsoft To Do too limited but full
project-management tools too heavy. Local-first, single-user, no server and no
account. Runs on macOS, Windows and Linux.

The application is C# on .NET 10 with an Avalonia front end. The original
Flutter implementation is still in the tree as a reference while the port is
verified against it — see [Two implementations](#two-implementations).

## What a task has

A summary, a description written up front, and a running log of timestamped
notes added as work proceeds. One status, at most one category, up to ten tags,
a priority, an estimate and an actual time, a due date, an ordered list of
steps, attachments, and reminders.

## Statuses

You define your own statuses, and they are the Kanban board's columns. Each is
typed `Initial`, `Active` or `Final` — a closed set the application reasons
about. Several statuses can share a type, so `Backlog` and `Ready` are both
`Initial`, and `Done` and `Abandoned` are both `Final`.

Entering any `Final` status stamps the completion date; leaving one clears it.
Deleting a status in use is blocked until you say where its tasks should go.

## Where your data lives

You choose a folder on first run. Everything is plain files beneath it:

```
<your folder>/
  config.json       statuses, categories, tags, settings
  tasks/            one JSON file per task, named by ULID
  attachments/      copies of attached files
  .trash/           deleted tasks, never purged automatically
```

Put that folder inside OneDrive, Dropbox or iCloud Drive and your tasks follow
you between machines. Conflicts are per-task rather than whole-database, and
copies left behind by the sync client are surfaced in the app instead of being
silently ignored.

There is no database, so you can back the folder up, grep it, or read it in any
text editor — and it stays readable if might-do stops existing. See
[docs/adr/0001](docs/adr/0001-file-per-task-json-storage.md) for why not SQLite.

Which folder you chose is remembered per machine, not in the folder — it sits at
a different path on each one. On macOS that is
`~/Library/Application Support/might-do/settings.json`.

## Running it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/MightDo.App
dotnet test
```

A development run reopens whatever workspace you last used, which means the
watcher and the reminder scheduler attach to your real tasks. To point a run
somewhere harmless:

```sh
MIGHTDO_SETTINGS=/tmp/might-do-dev.json dotnet run --project src/MightDo.App
```

## Repository layout

| Path | What it is |
|---|---|
| `src/MightDo.Core` | Domain, storage, queries, session, watcher, reminders. No UI, no dependencies beyond the base class library. |
| `src/MightDo.Platform` | Machine-local settings and the per-platform notifiers. |
| `src/MightDo.App` | The Avalonia application. |
| `tests/` | Three suites, mirroring the three projects. |
| `fixtures/` | The on-disk format's conformance corpus, shared by both implementations. |
| `tools/` | The fixture writer. |
| `spikes/` | Throwaway code backing the measurements in ADR-0003 and ADR-0004. |
| `lib/`, `test/` | The Flutter implementation and its tests. |

## Two implementations

Both are here on purpose. The Flutter app is the reference the port is checked
against, and it stays until that checking is finished. Three things are
verified automatically, and none of them need both toolchains — every
expectation is committed:

- **The format reads both ways.** Each implementation loads a corpus the other
  wrote and writes it back without losing a value.
- **The behaviour matches.** The same sixteen-step scenario run through each
  leaves workspaces that mean the same thing, down to the board ranks.
- **The views load.** Avalonia's headless platform builds the real visual tree
  with no display, so a XAML file naming a type that does not exist fails a test
  rather than a launch.

```sh
dotnet test                                   # 229 tests
flutter test                                  # 110 tests

# Regenerating the shared corpora, after changing either side's serialization
dart run tool/generate_fixtures.dart          # Flutter → fixtures/workspace-v1
dotnet run --project tools/MightDo.FixtureWriter   # .NET → fixtures/interop
REGENERATE_PARITY=1 flutter test test/format/parity_test.dart
```

Where the two deliberately differ, the divergence is recorded next to the code
and named by a test. There is one today: selecting the `Final` **status type**
reveals completed work in the .NET version, where the Flutter version shows an
empty list.

## Documentation

- [CONTEXT.md](CONTEXT.md) — the domain vocabulary. Read this first.
- [docs/adr/](docs/adr/) — decisions that would otherwise look surprising.
- [docs/format/workspace-v1.md](docs/format/workspace-v1.md) — the on-disk
  format, with a conformance corpus in [fixtures/](fixtures/). What any other
  implementation is written against.

## Not in this version

Deliberately deferred, each with a chosen approach already recorded:
recurring tasks (spawn-on-complete when a task reaches a `Final` status), a
system-tray presence so reminders fire while the app is closed, sync via a
server, importing from Microsoft To Do, and code signing.

Reminders currently notify only while might-do is running. Anything that fell
due while it was closed waits in the overdue banner when you next open it. The
in-app banner is the promise; an operating-system notification is attempted on
top and allowed to fail — see
[docs/adr/0004](docs/adr/0004-reminders-notify-in-app-first.md), which also
explains why no maintained cross-platform library does this. Today macOS
notifications appear credited to Script Editor rather than to might-do, which
goes away once the app is signed and bundled, and Windows shows none at all for
the same reason.
