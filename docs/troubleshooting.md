# Troubleshooting

Start with the logs. Almost everything below is diagnosable from them.

## Reading the logs

```
%APPDATA%\TaskbarFolders\logs\
```

One file per day per process: `manager-<date>.log` for the Manager, `launcher-<date>.log` for anything started by clicking a tile or pinning. Files are kept for 14 days. Each line is

```
<ISO-8601 UTC timestamp> [<Level>] <Category>: <message>
```

with an exception, if there was one, on the following line. Only `Information` and above are written.

When reporting a problem, attach the relevant `launcher-*.log` and `manager-*.log` — they are far more useful than a screenshot.

### Popup startup timing

Every popup writes two lines that say where the time between the click and the popup went:

```
Startup timing (ms from tStart, processAge=…): aumid=… settings=… di=… theme=… vm=… window=… placed=… show=… loaded=…
First render (ms from tStart, processAge=…, runtime=…): shown=… activated=… icons=… rendered=…
```

The checkpoints are cumulative milliseconds from the first line of the launcher's own code, so the cost of a step is the difference to the checkpoint before it.

| Value | Reached when |
|---|---|
| `aumid`, `settings`, `di`, `theme`, `vm` | The identity is stamped, settings are read, the services are built, the theme is merged, the group is loaded |
| `window` | The popup window object exists |
| `placed` | Its native window exists and size and position are computed |
| `loaded` | Layout is done. Nothing has been drawn yet; `show` equals `loaded` |
| `shown`, `activated`, `icons` | `Show()` has returned, activation was requested, icon loading was started |
| `rendered` | The first frame has been handed over for drawing |
| `processAge` | Milliseconds since the process started, taken when that line was written |
| `runtime` | The part of `processAge` spent before the launcher's own code ran: starting .NET and loading WPF |

`rendered` is not the moment pixels arrive on screen, and it does not include the time Windows needs to start the process after the click. A slow popup with small numbers here is being held up outside the launcher.

### Launcher exit codes

The launcher records why it gave up before it exits.

| Mode | Code | Meaning |
|---|---|---|
| Popup | 1 | No group id — neither `--group-id` nor a usable AppUserModelID |
| Popup | 3 | Startup threw; the exception is in the log |
| Popup | 4 | Unhandled exception after startup |
| Pin | 0 | Pinned |
| Pin | 1 | You declined the Windows dialog |
| Pin | 2 | Pinning unavailable on this edition, policy or Windows version |
| Pin | 3 | Pinning failed unexpectedly |
| Pin | 5 | Windows reported success, but no tile carrying the group's identity was found |

## Clicking a pinned tile does nothing

A brief busy cursor and then nothing means the launcher started and exited.

1. Open today's `launcher-*.log`.
2. Look for the most recent shutdown line and its exit code.

- **Exit code 1** — the shortcut lost its AppUserModelID, or was created by hand without one. Open the Manager, select the group, and click **Pin to taskbar** again, or re-pin from **Show shortcut…**; both regenerate the shortcut.
- **Exit code 3** — startup failed and the exception is logged. If the group's JSON has been hand-edited, check that it is still valid JSON.
- **No new lines at all** — the launcher never ran. The pinned shortcut may point at a launcher that has moved: this happens if a portable install was reorganised so that `Manager\` and `Launcher\` are no longer siblings. Restore the layout and re-pin.

If the popup opens but is invisible, check the *Enable popup animations* setting — turning animations off removes the animation path entirely and is a useful way to isolate that.

## "Pin to taskbar" reports that pinning is unavailable

Exit code 2. Programmatic pinning needs Windows 10 version 2004 or newer, and some editions and managed environments block it outright.

Use the manual path: the Manager opens the shortcut folder for you, or click **Show shortcut…**. Right-click the `.lnk` → **Show more options** (Windows 11 22H2 and later) → **Pin to taskbar**.

## Pinning appears to succeed but no tile appears

Since v0.4.8 the Manager checks this for you: after Windows reports a pin, it looks for a pinned shortcut carrying the group's AppUserModelID. If none is there you get **Pin not confirmed** rather than a "Pinned" message with nothing behind it, and the shortcut folder opens so you can pin by hand.

If you see that message:

Windows only persists a programmatic pin when the AppUserModelID is already known to the Start menu index. TaskbarFolders writes a Start menu entry for every group and notifies the shell, but a slow or busy indexer can still lose the race.

1. Close and reopen the Manager. On startup it re-checks and repairs every group's Start menu entry.
2. Try **Pin to taskbar** again.
3. Check that `%APPDATA%\Microsoft\Windows\Start Menu\Programs\TaskbarFolders\` contains an entry for the group. The pin-time log lines in `launcher-*.log` list exactly what that folder contained when the attempt was made.

If it still fails, pin manually via **Show shortcut…** — the result is identical.

## Nothing is generated when I add an app

No icon, no shortcut, and **Show shortcut…** opens nothing.

- **The group has no apps.** An empty group deliberately produces no icon and no shortcut. Add at least one.
- **Every icon extraction failed.** If none of the apps yielded an icon, generation stops rather than producing an iconless tile. Check the paths still exist.
- **The launcher binary was not found.** The Manager needs to know where `TaskbarFolders.Launcher.exe` is in order to write a shortcut targeting it. It logs every path it probed at error level — search `manager-*.log` for "Launcher binary not found". In a portable install this means `Manager\` and `Launcher\` are no longer siblings.

## The popup opens in the wrong place

Placement is DPI-sensitive. On mixed-DPI or scaled setups it should still land next to the tile you clicked; if it does not, note the display scaling percentage, the monitor arrangement, and which monitor the taskbar is on, and include them in the report. Placement at scaling other than 100 % was corrected in v0.4.3 — if you are on an older build, upgrade first.

## The popup closes immediately, or will not close

The popup closes when it loses focus, after a successful launch, and via a three-second fallback if Windows never gave it focus at all. A failed launch keeps it open and shows an error strip naming the app.

There are no keyboard shortcuts — Escape does not close it. Click elsewhere.

## My settings keep reverting

**Affects v0.4.4 and earlier.** Opening Settings showed the built-in defaults rather than your saved values, and clicking **Save** wrote those defaults over `settings.json`, including switching autostart off. Fixed in v0.4.5.

On an affected build, avoid clicking Save unless you intend to set every value in the dialog, or edit `%APPDATA%\TaskbarFolders\settings.json` directly with the Manager closed.

## The group icon looks wrong or out of date

- Only the **first four** apps contribute to the composite icon. Reorder is not available in the UI, so change which apps are in the group, or edit the order in the group's JSON with the Manager closed.
- Extracted source icons are cached under `icons\cache\`. Deleting that folder is safe — icons are re-extracted on the next change.
- Windows caches taskbar icons independently. If the file changed but the tile did not, unpin and re-pin.

## Known limitations

Current, deliberate or not-yet-implemented — not bugs to report:

- **The popup has no keyboard support.** No Escape to dismiss, no focused item on open.
- **A group with more apps than the popup can show loses the rest.** The popup stops at 800 DIP, which is eight rows — 24 apps at the default three columns — and the grid does not scroll. Anything past that is off-screen with nothing indicating it exists; a visible launch-failure strip costs a further row.
- **The popup's label colour follows your Windows app theme, not your wallpaper.** Since v0.4.7 the labels carry a halo in the opposite tone, so they stay legible either way, but on a wallpaper of the same brightness as the text they read as outlined rather than crisp.
- **No renaming, reordering, custom icons, or launch arguments** from the interface. `arguments` and the per-group `columns` value (1–6, default 3) exist in the JSON only; a per-group `theme` field is serialised but ignored.
- **Deleting a group does not unpin its tile.** Unpin it yourself.
- **Renaming a group by editing its JSON leaves the old Start menu entry behind.** There is no reconciler yet.
- **Renaming a group's JSON file changes its identity**, orphaning its icon, shortcut and any existing pin.
- **x64 only.** There is no ARM64 build.
- **No single-instance guard.** Nothing stops two Managers running at once; the second may overwrite the first's changes.
- **Uninstalling leaves `%APPDATA%\TaskbarFolders` and the Start menu entries in place.** See the [user guide](user-guide.md#uninstalling) for removing them.

## Still stuck

Open an issue with the [bug report template](https://github.com/gianluca-schwekendiek/taskbar-grouping/issues/new/choose). Include your Windows version and build, the TaskbarFolders version, your display scaling, your monitor arrangement, and the relevant log excerpt. [SUPPORT.md](../SUPPORT.md) covers where else to ask.
