# Windows app: design notes

## Decisions

- **WinUI 3 without XAML files.** The UI uses WinUI's own controls (SelectorBar, InfoBar,
  Expander, ToggleSwitch, ContentDialog, ProgressRing, Mica and Desktop Acrylic backdrops, Segoe
  Fluent Icons, the Fluent type ramp and theme brushes), built in C# instead of `.xaml` files. The
  XAML compiler only runs on Windows; without it the whole solution compiles on macOS and Linux.
  The few custom colours are styles with `{ThemeResource}` setters (`UI/Ui.cs`), so they follow
  light and dark mode. `App` implements `IXamlMetadataProvider` itself, which the XAML compiler
  would otherwise generate.
- **Unpackaged and self-contained, installed by an MSI.** `WindowsPackageType=None` and
  `WindowsAppSDKSelfContained=true` give a plain folder that runs without installing the Windows
  App SDK runtime or signing an MSIX. `Installer/Package.wxs` (WiX) wraps that folder in a
  per-user MSI (`perUserOrMachine` with `MSIINSTALLPERUSER`, so no administrator prompt) that
  installs to `%LOCALAPPDATA%\Programs\Clawd`, has a running Clawd quit (`Clawd.exe --quit`) before replacing files,
  removes the sign-in Run value on uninstall and starts Clawd when it finishes (also after a
  `/passive` update, through `CLAWD_RELAUNCH=1`). Notifications work through
  `AppNotificationManager.Register()`, which needs
  `Microsoft.WindowsAppRuntime.Insights.Resource.dll` next to `Clawd.exe` (`Clawd.csproj` adds
  it). Starting at sign-in uses the per-user Run key and honours the Startup apps switch in
  Windows Settings.
- **The pet is a Win32 layered window** (`Pet/LayeredWindow.cs`): `WS_EX_LAYERED | TOOLWINDOW |
  TOPMOST | NOACTIVATE`, drawn with `UpdateLayeredWindow` from a premultiplied BGRA buffer.
  Transparent pixels are click-through, the window never takes focus, and drawing uses the same
  `Sprite.Render` as the Mac app through a small software rasterizer (`Core/Pet/Rasterizer.cs`)
  at a whole number of device pixels per sprite unit, so it stays crisp at 100, 125, 150 and 200%.
- **Coordinates.** The pet's physics (`Core/Pet/PetBrain.cs`, ported from
  `macOS/Sources/Pet.swift`) runs in DIPs with y up, as in AppKit. `ScreenSpace` maps it to the
  monitor Clawd is on (physical pixels, y down) using that monitor's DPI and work area.
- **Tray and menus** are Win32 (`Shell_NotifyIcon`, `TrackPopupMenuEx`), as in Explorer. Dark
  menus use uxtheme's `SetPreferredAppMode` (ordinal 135, undocumented but stable since 1903).
- **Updates** (`Clawd.Core/Updates`, `Clawd/AppController.Updates.cs`). The repository also
  holds the Mac releases and GitHub's "latest release" is whichever was published last, so Clawd
  lists the releases (`GET /repos/…/releases`, unauthenticated, at most once a day plus manual
  checks) and takes the newest `windows-v*` that isn't a draft or pre-release. The MSI isn't
  code-signed, so the download is trusted only if it matches the SHA-256 `digest` GitHub records
  for every release asset; it is written under a temporary name and renamed once checked. To
  install, Clawd runs `msiexec /i … /passive /norestart CLAWD_RELAUNCH=1` from a hidden Windows
  PowerShell `-Command` (no script file, so execution policy doesn't apply). The installer's own
  QuitClawd action closes Clawd before files are replaced and LaunchClawd starts the new one. If
  msiexec fails before it closes Clawd, Clawd shows the error; if it fails after, the PowerShell
  command starts Clawd again with `--update-failed <code>`. Only the copy in
  `%LOCALAPPDATA%\Programs\Clawd` updates itself.
- **Live terminal.** `Terminal/LiveTerminalView.cs` hosts the bundled xterm.js 6.0.0 in WebView2
  through a virtual host, with no network access. Its profile is kept in
  `%LOCALAPPDATA%\Clawd\WebView2`. `Clawd.Core/Bridge/OrcaBridge.cs` runs `Bridge/clawd-bridge.js`
  unchanged under `ELECTRON_RUN_AS_NODE=1 Orca.exe`, in a job object that ends it with Clawd.
- **Languages.** Korean when the Windows display language is Korean, English otherwise, chosen
  by `Strings.Resolve` in `Clawd.Core/Localization/Strings.cs`. Text in Clawd.Core lives in
  `Localization/Strings.resx` and `Strings.ko.resx`; text in the WinUI app lives in
  `Clawd/Strings/<lang>/Resources.resw`, compiled into `Clawd.pri` and read through `L`
  (`Clawd/L.cs`), which uses the language Core chose so the two never disagree. The .resw files are
  also embedded in `Clawd.dll` as a fallback. `LocalizationTests.cs` checks that both languages
  have the same keys and that every key in use exists.
- **Demo data.** `Clawd.Tools/Demo.cs` holds the same Korean-to-English table as
  `macOS/Scripts/demo/make_demo.py`. Keep the two in step: generated for the same language, the
  `.screen.txt` files must be identical and the JSON equal once `uuid`, `toolu_…` ids and
  timestamps are taken out.
- **Shortcuts.** ⌘ maps to Ctrl: Ctrl+T, Ctrl+W and Ctrl+1 to Ctrl+3. The default global
  shortcut is Ctrl+Alt+J (`RegisterHotKey`).

## Orca on Windows

- Orca writes its runtime file to `%APPDATA%\orca\orca-runtime.json` (`ORCA_USER_DATA_PATH`
  overrides it). Its `transports` list has a `named-pipe` entry, `\\.\pipe\orca-<pid>-<id>`, which
  Clawd uses with the same newline-delimited JSON frames as on macOS, and a `websocket` entry for
  Orca Mobile, which Clawd ignores.
- Orca installs per user to `%LOCALAPPDATA%\Programs\orca\Orca.exe`, with the CLI at
  `resources\bin\orca.exe` and the shared modules the bridge loads under
  `resources\app.asar.unpacked\out\shared\`.
- Claude Code keeps sessions in `%USERPROFILE%\.claude\sessions\<pid>.json` and transcripts in
  `projects\<dir>\<session>.jsonl` (or under `CLAUDE_CONFIG_DIR`). Clawd reads `ORCA_PANE_KEY`
  from the Claude Code process's environment block to match a pane to its session.

## Known limitations

- Orca's chat-style agent panes (`structured-agent-session-…`) have no terminal and do not set
  `ORCA_PANE_KEY`, so their timeline is matched by folder and last prompt, and two such panes in
  one folder can show each other's timeline. The terminal view, sending and permission answers do
  not apply to them.
- In the live terminal, Korean text has gaps between syllables unless the D2Coding font is
  installed, because Cascadia Mono has no Hangul glyphs.
- In the live terminal, Enter while Korean text is still being composed commits and sends in one
  press, as in Windows Terminal. The chat composer only commits it, as on the Mac.
- When Claude Code has mouse reporting on, dragging in the live terminal goes to Claude Code; hold
  Shift to select text instead.
- In light mode, some of Claude Code's 24-bit colours, chosen for dark backgrounds, are hard to read
  in the live terminal. The Mac app shows them the same way.
- Before pairing, the plain-text terminal view shows Claude Code's grey suggested prompt as if it
  were typed text, because Orca's screen text has no colours.
- Ctrl+T and Ctrl+W cannot be sent to the agent's terminal, because the chat uses them.
- The pet stays above full-screen apps and games.
- Dragging files over Clawd gives no feedback until they are dropped.
- Each folder Clawd runs from registers its own notification identity.
- Some colours set in code refresh on the next update after a theme change rather than at once.
- The pet window has no UI Automation peer. The tray icon, chat and settings are accessible.
- The installer is not code-signed, so SmartScreen asks for confirmation. Single-file publish is
  untested.
- Updates are x64 only, like the releases; an ARM64 build would need its own MSI on each release.
  A machine-wide install (`ALLUSERS=1`) doesn't update itself.
