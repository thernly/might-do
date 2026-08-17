# Reminders notify in-app; an OS notification is best-effort

A due reminder is surfaced inside might-do — the overdue banner and the reminders
panel — and that is the behaviour we promise. Raising a real operating-system
notification is attempted on top, per platform, and is allowed to fail without
the feature being considered broken.

This is a smaller commitment than it first appears, because the README already
scopes it: reminders notify only while might-do is running, and anything that
fell due while it was closed waits in the overdue banner. Firing while the app is
closed needs a background presence, which is deliberately deferred along with the
system tray.

## Why not a notification library

There isn't a maintained one that works on macOS. Checked before deciding:

| Package | Stars | Status | macOS |
|---|---|---|---|
| `pr8x/DesktopNotifications` | 214 | Archived Jan 2026 | Minimal — author had no Mac able to run the modern API |
| `specklesystems/AvaloniaDesktopNotifications` | 7 | Archived May 2026 | Not supported |
| `nnym/DesktopNotifications` | 0 | Unclear | Incomplete |

Every fork in the lineage is archived, and macOS is the unimplemented platform in
all of them — the original author's note is that `NSUserNotificationCenter` was
deprecated in macOS 10.14 and they had no machine to implement
`UNUserNotificationCenter` on. Adopting an archived dependency to get the
platform it doesn't support is not a trade worth making.

Avalonia's own `WindowNotificationManager` is an in-app overlay, not an OS
notification. It is a fine way to render the in-app half and nothing more.

## The macOS problem, specifically

`UNUserNotificationCenter` requires a **signed, bundled `.app`**. Code signing is
already on the deferred list, so the native path is blocked until that changes —
not by difficulty, but by order of work.

What does work today, verified from an unsigned, unbundled `dotnet` process
([`spikes/platform-spike/`](../../spikes/platform-spike/), `notify`): shelling out
to `osascript -e 'display notification …'` returns exit code 0 and posts a real
banner. It carries a title and subtitle, which is all a reminder
needs.

The wart is attribution. The notification is credited to whichever host the
script runs under — Script Editor, in practice — so it will not say "might-do"
and will not carry our icon. Cosmetic, visible, and gone once the app is signed
and bundled.

`terminal-notifier` is the usual way around this. It is not installed on a stock
machine (confirmed: absent here), so it can be used if present but never assumed.

## Considered options

**Ship nothing but the in-app banner.** Defensible given the app must be running
anyway — if it's running, its window is where the user is looking. Rejected
because a reminder the user only sees after switching to the app is barely a
reminder; might-do is not the front window most of the day.

**Block on code signing and do it natively.** Correct end state, wrong order. It
would hold reminders hostage to a task the README already deferred for its own
reasons.

**P/Invoke `UNUserNotificationCenter` unsigned and hope.** Rejected: the API
requires a bundle identifier and refuses to deliver without one. This fails at
runtime rather than at build time, which is the worst shape for a notification
bug — silence that looks like "no reminders were due".

**In-app as the contract, OS notification as an opportunistic extra (chosen).**
Each platform gets its best available route: WinRT toast on Windows, libnotify
over D-Bus on Linux, `osascript` on macOS. A failure downgrades to the in-app
banner, which was the promise anyway.

## Consequences

- No new dependency, and no archived one.
- The notification abstraction is one small interface with a per-platform
  implementation and a no-op fallback. Since the in-app path is the contract,
  the fallback is not a degraded mode.
- On macOS, notifications are misattributed until the app is signed and bundled.
  Worth a line in the README so it reads as known rather than broken.
- `osascript` spawns a process per notification. At reminder frequency — a
  handful a day — this is irrelevant, and it would only become a concern if
  reminders were ever fired in bulk.
- Reminder scheduling stays in-process and in-memory, driven off the loaded
  tasks. Nothing is registered with the OS, so nothing needs unregistering when
  a reminder is edited or its task is trashed.
- Delivery while the app is closed remains out of scope, unchanged. When the tray
  presence is picked up, this decision should be revisited whole: a tray app that
  can notify while closed probably also wants to be signed, at which point the
  native macOS API becomes available and the `osascript` shim can go.
