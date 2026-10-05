# Clawd for Windows

The Windows version of Clawd, built with WinUI 3 (Windows App SDK 2.5) on .NET 10. It has the same
desktop pet, alert cards, chat, permission answers and terminal view as the Mac app. See the
[main README](../README.md) for what Clawd does; this file covers installing, building and running
it on Windows.

## Install

Download `Clawd-<version>-Windows-x64.msi` from
[GitHub Releases](https://github.com/joonseo1227/clawd-for-orca/releases) and run it. Clawd installs
for your account only, without an administrator prompt, to `%LOCALAPPDATA%\Programs\Clawd`, adds
itself to the Start menu and starts when the installer finishes. It is self-contained, so .NET and
the Windows App SDK do not need to be installed. The installer is not code-signed, so SmartScreen
may ask you to confirm it (**More info** > **Run anyway**).

To start Clawd when you sign in, turn on **Open Clawd at sign-in** in Settings. A newer installer
replaces the installed version. To uninstall, use **Settings** > **Apps** > **Installed apps**;
Clawd's settings and WebView2 cache stay in `%LOCALAPPDATA%\Clawd` until you delete that folder.

## Updates

The installed Clawd keeps itself up to date. Once a day it asks GitHub's API for the repository's
releases (`api.github.com`, without signing in; the request carries only Clawd's version as its
user agent), and when a newer `windows-v<version>` release is out it downloads that release's MSI
to `%LOCALAPPDATA%\Clawd\Updates` in the background. The download is used only if its size and
SHA-256 digest match the ones GitHub lists for the file. Clawd then shows a notification with
**Restart to update**, the same button in Settings and an **Install Clawd <version> and restart**
item in its menus. Any of them runs the installer with a progress bar only, which closes Clawd,
replaces it and starts the new version in a few seconds. If the installation fails, Clawd starts
again and says why.

Turn off **Check for updates automatically** in Settings to stop the daily check; **Check now**
still works. Copies not installed by the MSI (a development build or a copied folder) never update
themselves.

## Requirements

- Windows 11 on x64. Windows 10 21H2 or later should also work, without the Mica background.
- [Orca](https://www.onorca.dev) for Windows, running. Clawd finds it through its uninstall entry,
  the running `Orca.exe`, or `%LOCALAPPDATA%\Programs\Orca`.
- Optional: the [D2Coding](https://github.com/naver/d2codingfont) font, so Korean text lines up in
  the live terminal.

## Usage

| Action | Result |
| --- | --- |
| Click Clawd or the notification-area icon | Opens the chat, starting with any agent that is waiting. |
| Ctrl+Alt+J | Opens or closes the chat from anywhere. The shortcut can be changed in Settings. |
| Ctrl+1 to Ctrl+3 | Answers the permission request on screen. |
| Ctrl+T | Switches between the chat and the terminal. |
| Ctrl+W or Esc | Closes the chat. |
| Double-click Clawd | Opens the agent that needs you most in Orca. |
| Right-click Clawd | Shows the agents, snacks, tricks and settings. |

Clawd starts in the notification area (in the ^ overflow if it is not pinned) and sits on top of
the taskbar.

To connect the live terminal, open **Orca Mobile** in Orca, create the QR code and click
**Copy pairing code**. Then click **Pair…** next to the live terminal in Clawd's Settings and
paste the code. It is stored in the Windows Credential Manager as `Clawd/orca-pairing`.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later).
The Windows App SDK comes from NuGet and is bundled into the app. Visual Studio 2022 17.14 or
later with the **WinUI application development** workload also works: open `Windows\Clawd.sln`,
choose the `x64` platform and set `Clawd` as the startup project.

From PowerShell in the `Windows` folder:

```powershell
dotnet test Clawd.Core.Tests                         # unit tests
dotnet build Clawd -c Debug -p:Platform=x64          # build the app
.\Clawd\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Clawd.exe
```

On an ARM64 PC, use `-p:Platform=ARM64 -r win-arm64` instead.

To build the installer the release workflow ships, publish the self-contained app folder and
build the MSI from it with [WiX](https://wixtoolset.org) (restored from NuGet):

```powershell
dotnet publish Clawd -c Release -r win-x64 -p:Platform=x64 -o publish\Clawd
dotnet build Installer -c Release    # Installer\bin\x64\Release\Clawd-<version>-Windows-x64.msi
```

## Demo mode

```powershell
.\Scripts\demo.ps1 -OpenChat            # add -Lang en for the English demo
```

This writes sample agents and a transcript to `%TEMP%\clawd-demo` and starts Clawd on them, without
contacting Orca. It uses the same data as the Mac app's
[`macOS/Scripts/demo/make_demo.py`](../macOS/Scripts/demo/make_demo.py).

Environment variables for testing:

- `CLAWD_FAKE_ORCA` and `CLAWD_FAKE_TRANSCRIPTS` point Clawd at saved data (set by `demo.ps1`).
- `CLAWD_TERMINAL_VIEW=1`, `CLAWD_SELECT=<pane>` and `CLAWD_DRAFT=<text>` open the chat in a given
  state.
- `CLAWD_WELCOME=1`, `CLAWD_OPEN_SETTINGS=1` and `CLAWD_SHOW_PAIRING=1` open those windows at launch.
- `CLAWD_APPEARANCE=light|dark` and `CLAWD_UI_LANG=en|ko` override the Windows theme and language.
- `CLAWD_START_AT=x,y` places Clawd on the monitor at that point, in physical pixels.
- `CLAWD_NO_LIVE=1` ignores the live terminal pairing.
- `CLAWD_UPDATE_FEED=<file or URL>` reads the release list from there instead of GitHub, in the API's
  format, at every launch; its links may point anywhere, `file:` URLs included. Use it with an
  installed copy to try an update end to end.
- `CLAWD_DEBUG=1` writes a log to `%LOCALAPPDATA%\Clawd\clawd.log`.

## Developer tools

`Clawd.Tools` runs on any OS:

```powershell
dotnet run --project Clawd.Tools -- orca               # agents Clawd sees, and any permission prompt
dotnet run --project Clawd.Tools -- sessions           # live Claude Code sessions and their Orca panes
dotnet run --project Clawd.Tools -- timeline <file>    # the chat timeline built from a transcript
dotnet run --project Clawd.Tools -- icon Clawd\Assets\Clawd.ico   # regenerate the app icon
```

## Layout

```
Clawd.sln
Clawd.Core/         net10.0, no UI: Orca client (named pipe, CLI fallback), agents, permission
                    prompts, terminal matching, transcripts and timeline, attention logic, bridge
                    process, settings, the pet's simulation, sprite and rasterizer
Clawd.Core.Tests/   xUnit tests, ported from the Mac app plus Windows-specific logic
Clawd.Tools/        icon, demo data, debug commands
Clawd/              WinUI 3 app
  AppController*.cs frame loop, attention, chat, menus, live terminal
  Native/           Win32 interop, hidden host window (tray, hotkey), monitors and DPI
  Pet/              the pet and snack windows (layered windows, per-pixel alpha)
  Services/         tray icon, menus, hotkey, notifications, sounds, sign-in, Credential Manager
  UI/               chat, timeline, terminal text view, alert card, settings, welcome
  Terminal/         live terminal view (xterm.js in WebView2), see Terminal/README.md
  Assets/           Clawd.ico, the terminal page and vendored xterm.js
Installer/          the MSI (WiX): Package.wxs
Scripts/demo.ps1
```

`Bridge/clawd-bridge.js` at the repository root is shared with the Mac app and copied into the
app's `Bridge` folder at build time. [NOTES.md](NOTES.md) describes the design decisions and known
limitations.
