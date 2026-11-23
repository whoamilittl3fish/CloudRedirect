# CloudRedirect (whoamilittl3fish fork)

Steam Cloud for "lua" games, redirected to your own cloud storage.

This file describes this fork; the main [README.md](README.md) is upstream's and kept unchanged.

> **This is a fork.** The official CloudRedirect project, its releases and its support live at **[crghofficial/cloudredirect](https://github.com/crghofficial/cloudredirect)**. This fork adds a few fixes and features on top of it (listed below). It publishes no releases: build it yourself (see [Building on Windows](#building-on-windows)). Do not download CloudRedirect builds from random websites; fake sites have distributed malware.

> **Back up any saves you care about.** CloudRedirect works inside the Steam client and rewrites how cloud saves are stored. It has been solid in practice, but a bug can overwrite or lose saves.

> Only use this if you want cloud saves for "lua" games. If all you want is to get rid of a Steam Cloud error, disable Steam Cloud in that game's properties instead.

## What it does

For games unlocked through a lua file, Steam has no cloud storage to use. CloudRedirect hooks Steam's internal cloud save RPCs and serves them from your own provider instead (Google Drive, OneDrive, Cloudflare R2, any S3-compatible storage, or a local folder). Uploads, downloads, conflicts and AutoCloud games all go through the normal Steam UI. Owned games, and lua files that only unlock DLC, keep using normal Steam Cloud.

On Windows it is a C++ DLL loaded by Steam plus a WPF companion app that installs the DLL and signs you in to the provider. On Linux it is a library loaded at Steam start plus a Flatpak app. See the upstream README for the full background.

Optionally it also syncs achievements, stats and playtime for those games (`sync_achievements`, `sync_playtime`).

## What this fork adds

| Change | Details |
|---|---|
| Fixes for newer Steam builds | Resolver and protobuf layout fixes from [DumbCodeGenerator/CloudRedirect](https://github.com/DumbCodeGenerator/CloudRedirect) (with work from mendy-tools): finds `GetAppInfo`, `GlobalEngine` and message offsets dynamically, re-applies AutoCloud save rules on every cloud call |
| No false save conflicts | Playing a game twice in one Steam session no longer re-sends the full file list at exit (which made Steam skip the upload and later show a conflict) |
| Save folders of ticket accounts | When a lua game runs as the account in its ownership ticket, it saves under that account's SteamID while Steam syncs yours. The DLL links that folder to the synced one with a junction when the folder is missing or empty, and never moves existing saves |
| Sync history | Each upload, failure (with its cause), conflict choice, unreachable cloud and local backup is recorded per game. App: **Sync History** tab |
| Previous versions in the cloud | Cloud cleanup keeps the last 2 previous versions of every save file that still exists, instead of deleting them |
| Local backup before the cloud overwrites | Before Steam may overwrite or delete local saves with the cloud copy, they are copied to `Steam\cloud_redirect\local_backups\`. One backup per game, removed once the cloud holds the same content or after 30 days. App: **Restore local backup** button |
| Delete achievements, reset stats or playtime | App: **Achievements & Playtime** tab. Delete one or all achievements, reset all stats (achievements, stats and playtime) or only playtime, locally and in the cloud. Applied the next time Steam starts |
| Update channel | The DLL and app check this fork for updates. With no releases here, an upstream release never replaces your build; update by building again |

## Configuration

Settings live in `%AppData%\CloudRedirect\config.json`. Keys this fork reads:

| Key | Default | Meaning |
|---|---|---|
| `sync_achievements` | `false` | Sync achievements and stats |
| `sync_playtime` | `false` | Sync playtime |
| `keep_previous_versions` | `2` | Previous versions of each save file kept in the cloud (`0` = delete them as upstream does) |
| `local_backups_kept` | `1` | Local backups kept per game (`0` = off) |
| `local_backups_max_days` | `30` | Local backups older than this are deleted (`0` = never) |

## Building on Windows

### 1. Install the tools

In a normal PowerShell window:

```powershell
winget install --id Git.Git -e
winget install --id Microsoft.DotNet.SDK.8 -e
winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.CMake.Project --includeRecommended"
```

| Tool | Why |
|---|---|
| Git | Clone and update the source |
| .NET 8 **SDK** (not only the runtime) | Builds the WPF app; also installs the .NET 8 Desktop Runtime the app needs |
| Visual Studio 2022 Build Tools, *Desktop development with C++* | MSVC, the Windows SDK and CMake for the DLL |

The full Visual Studio 2022 IDE with the same workloads works too.

Optional: CMake on the system `PATH`, so `cmake` works in any PowerShell window and not only in the Developer PowerShell (the Visual Studio generator finds the compiler on its own):

```powershell
winget install --id Kitware.CMake -e
```

Reopen PowerShell after installing. Update it later with `winget upgrade Kitware.CMake`.

### 2. Open a Developer PowerShell

Start menu → **Developer PowerShell for VS 2022**. The CMake that comes with the Build Tools is only on `PATH` there (unless you installed CMake separately above).

To turn an already open PowerShell window into one:

```powershell
Import-Module "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools" -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64"
```

This lasts only for that window.

```powershell
cmake --version      # 3.20 or newer
dotnet --list-sdks   # a line starting with 8.
cl                   # prints the MSVC version
```

If `dotnet` is not found right after installing the SDK, reopen the window.

### 3. Get the source

```powershell
cd C:\Devs
git clone https://github.com/whoamilittl3fish/CloudRedirect.git
cd CloudRedirect
git remote add upstream https://github.com/crghofficial/cloudredirect.git
```

### 4. Build

```powershell
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

The first command configures the build; run it once (or again after deleting `build\`). Rerun only the second one after pulling changes.

| Output | What it is |
|---|---|
| `build\Release\cloud_redirect.dll` | The DLL Steam loads |
| `build\Release\cloud_redirect_cli.exe` | Helper the app uses for cloud operations |
| `ui\bin\publish\CloudRedirect.exe` | The companion app, with the DLL and helper embedded |

Only the DLL (faster, no .NET needed):

```powershell
cmake --build build --config Release --target cloud_redirect
```

Unit tests:

```powershell
ctest --test-dir build -C Release --output-on-failure
```

### 5. Install your build

1. Exit Steam completely (tray icon → Exit).
2. Open `ui\bin\publish\CloudRedirect.exe` and press **Run All Patches** in Setup, or copy `build\Release\cloud_redirect.dll` into `C:\Program Files (x86)\Steam\` yourself (needs admin). Do not use an official release of the app for this: it carries the upstream DLL and replaces yours.
3. In the app, sign in to your cloud provider, and turn on achievement and playtime sync in Settings if you want them.
4. Start Steam. The first line of `Steam\cloud_redirect.log` shows the version that loaded, e.g. `[BUILD:2.6.9]`.

Keep the previous `cloud_redirect.dll` to roll back: exit Steam and put it back.

### 6. Update from upstream

```powershell
git fetch upstream
git merge upstream/master
cmake --build build --config Release
```

Worth doing when a Steam update breaks syncing. In `cloud_redirect.log` that shows up as `KV injector not ready`, `Resolver ... FAILED` or `GlobalEngine: all strategies failed`.

### Troubleshooting

| Message | Fix |
|---|---|
| `cmake : The term 'cmake' is not recognized` | Plain PowerShell: use the Developer PowerShell (step 2) or install CMake separately (step 1) |
| `No CMAKE_CXX_COMPILER could be found` | Not in a Developer PowerShell, or the C++ workload is missing |
| `'dotnet' is not recognized` | Install the .NET 8 SDK, then reopen the window |
| `Does not match the generator used previously` | Delete the `build` folder and configure again |
| `The process cannot access the file ...\CloudRedirect.exe because it is being used by another process` | Close the running app (tray / Task Manager) and build again |
| `CS1668: Invalid search path ... 'LIB environment variable'` | Harmless warning from the Build Tools environment |
| `native\cloud760_tool.exe not found` / `native\steam_api.dll not found` | Harmless: only the 760 Clean Up page is unavailable. To include it, put the 32-bit `cloud760_tool.exe` and `steam_api.dll` in `ui\native\` and build again |

## Linux

The Linux build and install steps are the same as upstream; see the [upstream README](https://github.com/crghofficial/cloudredirect#readme). The changes in this fork are built and unit-tested on Linux but have only been used on Windows.

## Credits

- CloudRedirect by [crghofficial](https://github.com/crghofficial/cloudredirect). The upstream author prefers fixes sent as pull requests over downstream forks; fixes here that make sense upstream are meant to go there.
- Fixes for newer Steam builds by [DumbCodeGenerator](https://github.com/DumbCodeGenerator/CloudRedirect) and mendy-tools.
- Licensed under the MIT license (see [LICENSE](LICENSE)).
