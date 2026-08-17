# platform-spike

Throwaway .NET console app that produced the measurements behind
[ADR-0003](../../docs/adr/0003-live-reload-by-debounced-rescan.md) and
[ADR-0004](../../docs/adr/0004-reminders-notify-in-app-first.md).

It is kept because those ADRs make empirical claims — event latency, which
changes get reported, whether a watcher survives its root being deleted — and a
claim you can re-run is worth more than one you have to trust. It is not part of
the application and nothing depends on it.

Requires the .NET 10 SDK (measured on 10.0.400, macOS 25.6).

```sh
cd spikes/platform-spike

# Every change scenario against a local temp workspace, plus burst and
# directory-swap. Prints which events arrived for each.
dotnet run -c Release

# First-event latency, events per logical save, and whether the watcher
# survives its root being deleted and recreated.
dotnet run -c Release latency

# The same scenarios against a real cloud-sync folder. Creates one temp
# subfolder inside the path you give it and deletes it afterwards.
dotnet run -c Release cloud "$HOME/Library/CloudStorage/OneDrive-Personal"

# Whether an unsigned, unbundled process can raise a real macOS notification.
# Posts one visible banner.
dotnet run -c Release notify
```

The `cloud` run writes into whatever folder you point it at, which for a real
sync folder means it briefly syncs to that account. That is the point — a File
Provider volume behaves differently from a local disk — but it is why the path is
an argument rather than a default.
