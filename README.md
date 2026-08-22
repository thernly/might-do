# might-do

A personal task tracker for people who find Microsoft To Do too limited but full
project-management tools too heavy. Local-first, single-user, no server and no
account. Runs on macOS, Windows and Linux.

The application is C# on .NET 10 with an Avalonia front end.

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

### Getting tasks in and out

Settings has an **Import and export** section. Export writes the tasks the list
is currently showing to a CSV file you choose — filtered, if you have filtered
it, and the button says so. Import reads one back, shows you exactly what it
would create, update and leave alone, and writes nothing until you say yes.

CSV is for spreadsheets and for moving tasks in from another tracker. **It is
not a backup** — the folder above is the backup. A round trip through CSV loses
attachments, reminders that have already fired, and the board positions of tasks
it creates. See
[docs/format/csv-v1.md](docs/format/csv-v1.md) for exactly what survives, and
[docs/adr/0005](docs/adr/0005-csv-is-interchange-not-backup.md) for why it is
shaped that way.

The list of workspaces, what you call each one, and how you left each one — the
view, the sort, the filters — are remembered per machine, not in the folders:
they sit at different paths on each machine, and a name is not part of the
on-disk format. On macOS that is
`~/Library/Application Support/might-do/settings.json`.

## How it looks

Settings has a light theme, a dark one, and Auto, which follows whatever your
operating system is set to and changes with it — so a machine that goes dark in
the evening takes might-do with it. Auto is the default.

That choice is machine-local too, alongside the workspace list rather than in
the workspace: your laptop can be dark and your desktop light while both are
showing the same tasks.

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

## Building for release

From the repo root, build the app in Release configuration:

```sh
dotnet build src/MightDo.App/MightDo.App.csproj -c Release
```

This compiles the .NET 10 app for the current machine. Use the platform-specific
commands below to produce deployable output for a target OS or build a native
app bundle or installer.

### macOS

```sh
dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r osx-arm64 --self-contained false
```

Use the appropriate RID for your Mac architecture (`osx-x64` for Intel, `osx-arm64`
for Apple Silicon).

For macOS app-bundle and DMG packaging, use the packaging script:

```sh
brew install create-dmg
./tools/package-macos-release.sh
```

Optional architecture override:

```sh
./tools/package-macos-release.sh x86_64
./tools/package-macos-release.sh arm64
```

The script creates both artifacts in `dist/`: `might-do.app` and
`might-do-<rid>.dmg`, plus a `.sha256` checksum and a `.provenance.txt` naming
the commit the build came from.

It signs and notarizes when this machine has been given the credentials:

```sh
export MIGHTDO_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export MIGHTDO_NOTARY_PROFILE=mightdo-notary   # xcrun notarytool store-credentials
./tools/package-macos-release.sh
```

Without them the build is unsigned, which is fine on your own machine and not
fine anywhere else — see [Distributing a build](#distributing-a-build).

### Windows

```powershell
dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r win-x64 --self-contained false

dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r win-arm64 --self-contained false
```

Use `win-x64` for 64-bit Windows or `win-arm64` for ARM-based Windows devices.
The published output is the portable app folder that can then be wrapped in an
installer or packaged for distribution. It is unsigned; Authenticode-sign the
executable and any installer before handing it to anybody — see
[Distributing a build](#distributing-a-build).

### Distributing a build

A build for your own machine needs none of this. A build for somebody else does,
and the gap is not cosmetic: an unsigned artifact gives its user no way to tell
an official build from a modified one, and the only way to open it is to click
past the warning that would have caught a modified one. Telling people to do
that as the normal way to install is teaching them to ignore the check.

Before publishing a release:

- sign and notarize the macOS bundle and DMG (`MIGHTDO_SIGN_IDENTITY`,
  `MIGHTDO_NOTARY_PROFILE` above), and confirm `stapler validate` passes;
- Authenticode-sign the Windows executable and installer;
- publish the `.sha256` checksums and the provenance record alongside the
  artifacts;
- build from a clean checkout in a protected CI environment, so the provenance
  record says which commit produced the bytes and nobody has to take it on
  trust.

Until all of that is in place, builds are for the machine that made them.

### Releasing from GitHub

To ship a release, create a version tag and push it to GitHub:

```sh
git tag v1.2.3
git push origin v1.2.3
```

Git tags use the `v` prefix, but the assembly version is set from the same tag
with the leading `v` stripped, so the app reports `1.2.3` while the release tag
remains `v1.2.3`.

The CI workflow will run the normal build and test jobs, then publish release
artifacts for Linux, Windows and macOS and attach them to the GitHub Release
for that tag. The release notes are generated automatically from the commits in
that tag range.

If you need to publish a release candidate or a follow-up fix, use a matching
version tag such as `v1.2.3-rc1` or `v1.2.4`.

### Linux

```sh
dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r linux-x64 --self-contained false

dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r linux-arm64 --self-contained false
```

Use the RID that matches your Linux architecture (`linux-x64` or `linux-arm64`).
If you want a fully self-contained single-binary deployment, replace
`--self-contained false` with `--self-contained true` and choose the matching RID.

## Repository layout

| Path | What it is |
|---|---|
| `src/MightDo.Core` | Domain, storage, queries, session, watcher, reminders. No UI, no dependencies beyond the base class library. |
| `src/MightDo.Platform` | Machine-local settings and the per-platform notifiers. |
| `src/MightDo.App` | The Avalonia application. |
| `tests/` | Three suites, mirroring the three projects. |
| `fixtures/` | Conformance corpora for the on-disk workspace and CSV interchange formats. |
| `tools/` | The fixture writer and macOS release-packaging script. |
| `spikes/` | Throwaway code backing the measurements in ADR-0003 and ADR-0004. |

## The format is verified

The app keeps committed fixtures and parity scenarios to verify the on-disk
format and behaviour automatically:

- **The format reads both ways.** `fixtures/workspace-v1/` is a corpus that is
  loaded and written back without losing a value; `fixtures/interop/` is what
  this implementation writes and is checked against the same expectations.
- **The interchange format reads both ways.** `fixtures/csv-v1/` pins the export
  byte for byte, the files a foreign tool might hand us, and every documented row
  error — including the round trip that matters most: exporting a workspace and
  importing it back writes nothing at all.
- **The behaviour matches.** A sixteen-step scenario, and the workspace left
  after running it, are committed in `fixtures/parity/` and replayed on every
  test run — down to the board ranks.
- **The views load.** Avalonia's headless platform builds the real visual tree
  with no display, so a XAML file naming a type that does not exist fails a test
  rather than a launch.

```sh
dotnet test
dotnet run --project tools/MightDo.FixtureWriter   # rewrites fixtures/interop
```

These expectations can no longer be regenerated — the oracle that produced them
is gone. Treat a parity or conformance failure as a change in behaviour to
justify, not a fixture to refresh.

## Documentation

- [CONTEXT.md](CONTEXT.md) — the domain vocabulary. Read this first.
- [docs/adr/](docs/adr/) — decisions that would otherwise look surprising.
- [docs/format/workspace-v1.md](docs/format/workspace-v1.md) — the on-disk
  format, with a conformance corpus in [fixtures/](fixtures/). What any other
  implementation is written against.
- [docs/format/csv-v1.md](docs/format/csv-v1.md) — the import and export format,
  with its own corpus in [fixtures/csv-v1/](fixtures/csv-v1/). A view of a
  workspace shaped for a spreadsheet, not a second copy of one.

## Not in this version

Deliberately deferred, each with a chosen approach already recorded:
recurring tasks (spawn-on-complete when a task reaches a `Final` status), a
system-tray presence so reminders fire while the app is closed, sync via a
server, and importing from Microsoft To Do. Code signing is wired into the
macOS packaging script and waits only on a Developer ID — see
[Distributing a build](#distributing-a-build).

Reminders currently notify only while might-do is running. Anything that fell
due while it was closed waits in the overdue banner when you next open it. The
in-app banner is the promise; an operating-system notification is attempted on
top and allowed to fail — see
[docs/adr/0004](docs/adr/0004-reminders-notify-in-app-first.md), which also
explains why no maintained cross-platform library does this. Today macOS
notifications appear credited to Script Editor rather than to might-do; fixing
that requires replacing the `osascript` notifier with a native implementation.
Windows shows no operating-system notification yet because it likewise needs a
native implementation tied to a packaged application identity.
