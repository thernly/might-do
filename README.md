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

Entering any `Final` status stamps the completion date; its calendar day can be
corrected afterwards, while leaving the Final status clears it.
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
on-disk format. That file is `might-do/settings.json` in the platform's
application-data folder:

| Platform | Path |
|---|---|
| macOS | `~/Library/Application Support/might-do/settings.json` |
| Windows | `%APPDATA%\might-do\settings.json` |
| Linux | `~/.config/might-do/settings.json` (or under `$XDG_CONFIG_HOME`) |

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

## How it looks

Settings offers two independent choices.

**Theme** is the whole look — palette, corners, type, the lot.

- **Cyrk 66** (the default) is drawn from a 1966 Polish circus poster: square
  corners everywhere, one hot pink accent on night blue, ink keylines and a hard
  offset shadow on anything you can press, and small, widely tracked capitals for
  labels and counts.
- **Sage & Slate** is the older look: soft corners, warm paper, and a slate
  accent.

**Colour scheme** is light, dark, or Auto, which follows whatever your operating
system is set to and changes with it — so a machine that goes dark in the evening
takes might-do with it. Auto is the default.

Neither choice constrains the other: every theme ships both a light and a dark
side, and switching theme keeps the scheme you were in. Both are machine-local,
alongside the workspace list rather than in the workspace, so your laptop can be
dark and your desktop light while both are showing the same tasks.

Both themes set their text in your desktop's own UI font. Only the Cyrk 66
wordmark needs a face that is not on a stock machine, and that one is bundled
under the SIL Open Font License in
[`src/MightDo.App/Assets/Fonts`](src/MightDo.App/Assets/Fonts/README.md), which
also explains why neither the kit's monospace caption face nor its text face is
among them. The poster itself is not distributed with the application — it was
the reference for the palette and nothing more.

### Adding a theme

One file in `src/MightDo.App/Themes/`, holding the complete look: a `Styles`
whose resources define the `App*` brushes for both schemes, and whose own rules
set the geometry, the type and Fluent's control colours. Add a case to
`Theme.SourceFor`, a member to `DesignTheme`, and a radio button to the settings
page. `DesignThemeTests` pins the contract — every theme has to answer for every
key in both schemes — and `PaletteContrastTests` holds every theme to the same
legibility bar, so a new one cannot ship unreadable.

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

Those commands publish framework-dependent output, which needs the .NET 10
runtime on the machine that runs it. Replace `--self-contained false` with
`--self-contained true` to bundle the runtime instead. CI does that for the
Linux and Windows release artifacts; the macOS packaging script does not, so
the macOS bundle and DMG need the runtime installed.

### Automatic versioning

[Nerdbank.GitVersioning](https://dotnet.github.io/Nerdbank.GitVersioning/)
stamps every build with the base version in `version.json`, a build number
derived from Git history, and the source commit ID. The `1.1` series produces
versions such as `1.1.23`; the About window shows that number, and crash reports
include the commit ID. Rebuilding the same commit keeps the same version;
uncommitted edits do not advance the build number.

The build number counts commits, not releases, so release numbers are not
consecutive: two releases ten commits apart go from `1.1.2` to `1.1.12`.

To start a new minor or major series, change `version` in `version.json` and
commit it. That commit is the first of the series, so `1.2` starts at `1.2.1`
rather than `1.2.0`. CI fetches the full Git history so local and CI builds
agree.

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

Without them the build is unsigned — see
[Distributing a build](#distributing-a-build) for what that means for anyone
you hand it to.

### Windows

```powershell
dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r win-x64 --self-contained false

dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r win-arm64 --self-contained false
```

Use `win-x64` for 64-bit Windows or `win-arm64` for ARM-based Windows devices.
The published output is the portable app folder that can then be wrapped in an
installer or packaged for distribution. It is unsigned — see
[Distributing a build](#distributing-a-build).

### Linux

```sh
dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r linux-x64 --self-contained false

dotnet publish src/MightDo.App/MightDo.App.csproj -c Release -r linux-arm64 --self-contained false
```

Use the RID that matches your Linux architecture (`linux-x64` or `linux-arm64`).

### Distributing a build

Release builds are currently unsigned. Signing is the goal rather than a
precondition: the macOS packaging script signs and notarizes as soon as it is
given a Developer ID, and Authenticode signing for Windows is not wired up yet.

What that costs the person installing: macOS Gatekeeper and Windows SmartScreen
warn before opening an unsigned build, and they have to override the warning to
run it. An unsigned artifact also cannot prove it is the official build rather
than a modified one, so say where a build came from when you hand it over.

What helps in the meantime:

- build from a clean checkout — the GitHub release workflow does — so the
  provenance record names the commit that produced the bytes;
- keep the `.sha256` checksum and `.provenance.txt` with the macOS DMG; the
  release zip carries both;
- when signing credentials are available, set `MIGHTDO_SIGN_IDENTITY` and
  `MIGHTDO_NOTARY_PROFILE` (above) and confirm `stapler validate` passes;
- Authenticode-sign the Windows executable and any installer once a certificate
  exists.

### Releasing from GitHub

To ship a release, start from a clean, up-to-date checkout of `main`, so the
build number is the one `main` gives. Restore dependencies, ask Nerdbank for the
release version, and tag that commit:

```sh
dotnet restore MightDo.slnx
VERSION=$(dotnet msbuild src/MightDo.App/MightDo.App.csproj -nologo -t:GetBuildVersion -getProperty:NuGetPackageVersion -p:PublicRelease=true)
git tag "v$VERSION"
git push origin "v$VERSION"
```

Git tags use the `v` prefix. Nerdbank calculates the version from `version.json`
and Git history; the tag marks the release rather than overriding its version.
CI rejects a tag that does not match the computed version. The app, published
assemblies, and macOS bundle metadata use the same generated version.

The CI workflow will run the normal build and test jobs, then publish release
artifacts and attach them to the GitHub Release for that tag as three zips:
`linux-x64` and `win-x64`, both self-contained, and `macos-arm64`, which holds
the app bundle and DMG and needs the .NET 10 runtime installed. All three are
unsigned — see [Distributing a build](#distributing-a-build). The release notes
are generated automatically from the commits in that tag range.

For a follow-up fix, commit the fix and repeat these commands; the build number
advances automatically. For a prerelease, add a suffix such as `-rc` to the
base version in `version.json` and commit it before calculating the tag. The
same commands produce a tag such as `v1.1.4-rc`, and CI marks any release whose
tag carries a suffix as a prerelease on GitHub. Remove the suffix and commit to
go back to full releases; the build number carries on rather than resetting.

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

might-do was first written in Flutter and then ported to .NET. The Flutter
implementation has been removed, but the workspace corpus and parity
expectation it generated are committed, and with the CSV corpus they are what
the formats and the behaviour are checked against:

- **The format reads both ways.** `fixtures/workspace-v1/` is a corpus that is
  loaded and written back without losing a value; `fixtures/interop/` is what
  this implementation writes and is checked against the same expectations.
- **The interchange format reads both ways.** `fixtures/csv-v1/` pins the export
  byte for byte, the files a foreign tool might hand us, and every documented row
  error — including the round trip that matters most: exporting a workspace and
  importing it back writes nothing at all.
- **The behaviour matches.** The workspace the Flutter implementation was left
  with after a scripted scenario is committed in `fixtures/parity/`; every test
  run replays the same scenario and compares — down to the board ranks.
- **The views load.** Avalonia's headless platform builds the real visual tree
  with no display, so a XAML file naming a type that does not exist fails a test
  rather than a launch.

```sh
dotnet test
dotnet run --project tools/MightDo.FixtureWriter   # rewrites fixtures/interop
```

The Flutter-generated expectations can no longer be regenerated. Treat a parity
or conformance failure as a change in behaviour to justify, not a fixture to
refresh.

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

Deliberately deferred:
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
native implementation tied to a packaged application identity. Linux goes
through `notify-send`.
