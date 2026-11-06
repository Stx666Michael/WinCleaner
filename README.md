# WinCleaner

A small, modern Windows storage cleaner. It scans your drives (or any folders you pick), finds junk and large
files, and lets you choose exactly what to remove. **Scanning is read-only. Nothing is deleted until you tick
items and confirm.**

## Screenshots

Pick what to scan, review the results, then confirm. (Shown with demo data.)

| | |
|---|---|
| ![Home screen: choose drives or folders to scan](docs/screenshots/01-home.png) | ![Results: categories, risk badges and checkboxes](docs/screenshots/02-results.png) |
| ![Confirm dialog: Recycle Bin by default](docs/screenshots/03-confirm.png) | ![Summary after cleaning](docs/screenshots/04-summary.png) |

## Run it

Requires Windows 10/11 (x64). Build it (see [Build from source](#build-from-source)), then run
`publish\WinCleaner.exe`. It needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

You can also pass folders to scan instead of the system drive: `WinCleaner.exe D:\Games "C:\Users\me\Videos"`.

## What it finds

| Category | Examples | Risk |
|---|---|---|
| Temporary files | user and Windows temp folders, only if untouched for 24 h | Safe |
| Browser caches | Chrome, Edge, Brave, Firefox caches (never profiles, cookies or passwords) | Safe |
| App and developer caches | shader caches, npm, pip, NuGet HTTP cache, Internet cache | Safe |
| Crash reports and dumps | CrashDumps, WER, Minidump (plus loose `.dmp` files, as Review) | Safe / Review |
| Windows Update downloads | `SoftwareDistribution\Download` | Safe |
| Recycle Bin | emptied permanently | Safe |
| Old log files | `.log` / `.etl` over 1 MB, not modified for 30 days | Review |
| Old downloads | installers and archives in Downloads, untouched for 90 days | Review |
| Unused `node_modules` | whole folder as one item, no changes for 90 days | Review |
| Large files | anything over your chosen size (default 500 MB) | Review |

Items marked **Locked** are shown so you can see what uses space, but cannot be selected: Windows, Program Files,
ProgramData, `pagefile.sys` / `hiberfil.sys` / `swapfile.sys`, files with the System attribute, other users'
profiles.

**Review** items live in your own folders (for example a model file in a project). They are never pre-selected.
"Select safe items" only ticks the Safe ones.

Some Windows items (Windows temp, Windows Update downloads) need administrator rights. Use
**Restart as administrator** in the results screen to include them.

## How deletion stays safe

- The default is **Move to Recycle Bin**. Permanent delete is an explicit choice in the confirm dialog.
- Before each item is removed it is re-checked: still the same size and modified time as in the scan, not a
  protected path (case, `..`, `\\?\` and 8.3 tricks are normalised), and no junction or symlink anywhere in the
  path. A changed or suspicious item is skipped and reported.
- Junctions and symlinks are never followed, when scanning or deleting. Deleting a folder that contains one
  removes the link only, never what it points to.
- OneDrive-style cloud-only placeholders are ignored.
- A file that is in use is skipped and listed; the rest of the run continues.
- Every action is logged to `%LOCALAPPDATA%\WinCleaner\clean-log.txt`.
- Recycled files only free space once you empty the Recycle Bin. A drive with no Recycle Bin (such as a USB stick)
  refuses "Move to Recycle Bin" rather than silently deleting.

## Build from source

```
dotnet build
dotnet test
dotnet publish src/WinCleaner.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Layout: `src/WinCleaner.Core` (rules, scanner, cleaner; no UI), `src/WinCleaner.App` (WPF + WPF-UI, MVVM),
`tests/WinCleaner.Tests` (39 tests that run against throwaway sandbox folders and never touch real files).

## Notes

- Windows x64 only.
- .NET 8 reaches end of support on 10 Nov 2026. The app makes no network connections, but you may want to
  retarget to a newer .NET LTS later (change `TargetFramework`, install that runtime).
- Duplicate-file detection is not included.
