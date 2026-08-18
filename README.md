# might-do

A personal task tracker for people who find Microsoft To Do too limited but full
project-management tools too heavy. Local-first, single-user, no server and no
account. Runs on macOS, Windows and Linux.

The application is C# on .NET 10 with an Avalonia front end, ported from an
earlier Flutter implementation that has now been removed — see
[The format is verified, not the port](#the-format-is-verified-not-the-port).

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

You choose a folder on first run. That folder is a **workspace**, and everything
in it is plain files:

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

You can keep several workspaces — work in one, home in another — and switch
between them from the button at the left of the toolbar. Each is an ordinary
folder of the shape above, with its own statuses, categories and tags; one is
open at a time. Forgetting a workspace removes it from the switcher and leaves
its folder untouched.

Choosing a folder is what *creates* a workspace; reopening one never creates
anything. If a remembered workspace's folder has gone — an unmounted drive, a
synced folder that has not arrived — might-do says so and leaves the folder
alone rather than seeding an empty workspace over the top of it. The workspace
stays in the switcher, because it may come back, and whatever else you have
is one click away.

The list of workspaces, what you call each one, and how you left each one — the
view, the sort, the filters — are remembered per machine, not in the folders:
they sit at different paths on each machine, and a name is not part of the
on-disk format. On macOS that is
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

## The format is verified, not the port

The Flutter implementation was the reference this one was checked against. It
has been removed now that the checking is finished, but what it pinned down
survives as committed fixtures, and three things stay verified automatically:

- **The format reads both ways.** `fixtures/workspace-v1/` is a corpus Flutter
  wrote, loaded and written back without losing a value; `fixtures/interop/`
  is what this implementation writes, which Flutter read the same way.
- **The behaviour matches.** A sixteen-step scenario, and the workspace Flutter
  left after running it, are committed in `fixtures/parity/` and replayed on
  every test run — down to the board ranks.
- **The views load.** Avalonia's headless platform builds the real visual tree
  with no display, so a XAML file naming a type that does not exist fails a test
  rather than a launch.

```sh
dotnet test                                        # 244 tests
dotnet run --project tools/MightDo.FixtureWriter   # rewrites fixtures/interop
```

These expectations can no longer be regenerated — the oracle that produced them
is gone. Treat a parity or conformance failure as a change in behaviour to
justify, not a fixture to refresh.

One divergence from Flutter is deliberate, recorded next to the code and named
by a test: selecting the `Final` **status type** reveals completed work here,
where Flutter showed an empty list. Flutter's behaviour was the bug.

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
