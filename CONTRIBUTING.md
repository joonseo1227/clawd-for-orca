# Contributing

Thanks for your interest in Clawd for Orca. Bug reports, fixes and small focused improvements
are welcome. For larger changes, please open an issue first so we can agree on the approach.

## Repository layout

`macOS/` and `Windows/` hold the two apps, each with its own `VERSION`. `Bridge/clawd-bridge.js` at
the root is shared by both.

```
macOS/                 the Mac app (Swift package)
  Sources/             source code
  Tests/ClawdTests/    unit tests
  Resources/           English and Korean strings (String Catalog)
  Icon/                app icon
  Scripts/             demo scripts, appcast.py for releases
  build.sh             build, sign, notarize, install
  appcast.xml          the update feed, written by Scripts/appcast.py
  VERSION              macOS app version, also its build number
Windows/               the Windows app (.NET solution)
  Clawd.Core/          logic without UI: Orca client, agents, transcripts, the pet's simulation
  Clawd.Core.Tests/    unit tests
  Clawd/               WinUI 3 app
  Clawd.Tools/         icon, demo data, debug commands
  Installer/           MSI installer (WiX)
  Scripts/             demo script
  VERSION              Windows app version
Bridge/                live terminal bridge (runs on Orca's Node)
docs/                  documentation and screenshots
```

## Building the macOS app

You need macOS 15 or later on Apple silicon and Xcode 26 or later (Swift tools 6.2). Run these
from `macOS/`:

```sh
cd macOS
swift build                 # debug build, fastest for compile checks
./build.sh                  # release build of Clawd.app, signed ad-hoc unless you have a Developer ID
./build.sh --install        # also install to /Applications and relaunch
Scripts/demo.sh             # run on made-up demo data (macOS/Scripts/demo/make_demo.py)
```

`build.sh` reads `VERSION`, copies `../Bridge/clawd-bridge.js` into the bundle and embeds
Sparkle.framework, the updater, without its XPC services (only sandboxed apps need them). The
version is also the build number (`CFBundleVersion`), which Sparkle compares to find newer
releases, so `VERSION` is numbers and dots only. The script always recreates `Clawd.app` from
scratch rather than copying over a signed binary, because macOS kills a process whose signed
binary was overwritten in place. Keep it that way if you change the script.

To exercise the Orca features you need Orca running with at least one agent. The binary that
`build.sh` leaves at `macOS/clawd` also has a few debug modes:

```sh
./clawd --orca                   # print the agents Clawd sees and any permission prompt on screen
./clawd --sessions               # live Claude Code sessions and their Orca pane keys
./clawd --timeline <file.jsonl>  # print the chat timeline built from a transcript
```

Environment variables useful while testing:

- `CLAWD_FAKE_ORCA=<file>` replays a saved `orca worktree ps --json` output instead of asking Orca;
  `CLAWD_FAKE_TRANSCRIPTS=<dir>` reads transcripts from a folder (both set by `macOS/Scripts/demo.sh`).
- `CLAWD_DEBUG=1` logs terminal keystrokes to stderr.
- `CLAWD_TERMINAL_VIEW=1` opens the chat directly in terminal mode;
  `CLAWD_SHOW_PAIRING=1` opens the pairing sheet at launch.
- `CLAWD_DEFER_UPDATE=1` announces an update found at launch with a card and a menu item, as
  updates found later are, instead of Sparkle's window.

To try an update without publishing it, build a newer version with `./build.sh --dmg`, write a
feed for it with `Scripts/appcast.py --output <dir>/appcast.xml --url-prefix http://127.0.0.1:8765/`,
copy the disk image into `<dir>` and serve it with `python3 -m http.server 8765 --bind 127.0.0.1`.
Then point an older copy at it: set `SUFeedURL` in its `Info.plist` to
`http://127.0.0.1:8765/appcast.xml`, re-sign it with the same identity, and set
`defaults write com.joonseo1227.clawd-for-orca SULastCheckTime -date "2020-01-01 00:00:00 +0000"`
so it checks at launch. Remove the `SU…` defaults afterwards.

### Signing and notarizing

If a Developer ID certificate is in your keychain, `build.sh` signs with it and the hardened
runtime; otherwise it uses an ad-hoc signature that works only on the Mac that built it. It signs
Sparkle's helpers and framework before the app, without `--deep`, as Sparkle documents. Set
`CLAWD_SIGN_IDENTITY` to choose an identity, or `CLAWD_SIGN_IDENTITY=-` to force ad-hoc.
Notarizing needs a notarytool keychain profile, created once:

```sh
xcrun notarytool store-credentials clawd-notary --apple-id <apple-id> --team-id <team-id>
```

Use `CLAWD_NOTARY_PROFILE` for a different profile name.

Updates are signed separately, with the EdDSA key whose public half is `UPDATE_PUBLIC_KEY` in
`build.sh`. Its private half lives in the maintainer's keychain under the account
`clawd-for-orca`, where `Scripts/appcast.py` finds it. Keep an offline backup
(`.build/artifacts/sparkle/Sparkle/bin/generate_keys --account clawd-for-orca -x <file>`): without
it, installed copies can't be sent another update and would have to be replaced by hand.

### Running the macOS tests

```sh
cd macOS
swift test                       # all unit tests (Swift Testing)
swift test --filter Transcript   # only the suites whose names match
```

The tests in `macOS/Tests/ClawdTests` cover the pure logic: permission dialog parsing, the
terminal draft merge, Orca answers and pairing codes, file drops, the transcript reader and
timeline grouping, and Markdown blocks. They need no Orca, network or keychain access, and never
launch the app. Transcript tests write to a temporary folder and remove it afterwards.

## Building the Windows app

You need the .NET 10 SDK. Run these from `Windows/`:

```sh
cd Windows
dotnet test Clawd.Core.Tests                    # unit tests; also run on macOS and Linux
dotnet build Clawd -c Debug -p:Platform=x64     # builds on any OS; running it needs Windows
dotnet build Installer -c Release               # the MSI, on Windows only, after dotnet publish (see Windows/README.md)
```

See [Windows/README.md](Windows/README.md) for running, publishing and demo mode, and
[Windows/NOTES.md](Windows/NOTES.md) for how the Windows app differs from the Mac app. When you
change shared behaviour, change both apps, or open an issue for the other one.

## How it works

- **Agent status.** Every two seconds Clawd asks Orca for each agent's status (working, blocked,
  waiting, done or idle), the tool it is using and its last message, the same information as
  `orca worktree ps --json`. Requests go through Orca's local socket and take about a
  millisecond. Orca writes the socket's location and token to
  `~/Library/Application Support/Orca/orca-runtime.json`; if the socket is unavailable, Clawd
  uses the `orca` CLI that ships with Orca instead.
- **Terminal actions.** Sending messages, keystrokes and permission answers, reading the screen
  and resizing the terminal all go through Orca's `terminal.*` commands (`orca terminal send`,
  `orca terminal read --screen` and so on).
- **Following the work.** Claude Code records each session in
  `~/.claude/projects/<project>/<sessionId>.jsonl`. Orca tags every process started in a pane with
  `ORCA_PANE_KEY`, which Clawd uses to match each pane to its session, so multiple Claude sessions
  in the same folder are kept apart. Without a match, Clawd falls back to the folder and the last
  request.
- **Live terminal.** [`Bridge/clawd-bridge.js`](Bridge/clawd-bridge.js) runs on the Node that
  ships with Orca and reuses Orca's own connection code rather than reimplementing the
  encryption. It runs only while a terminal view is open and stops after two minutes of
  inactivity.
- **Updates.** The Mac app uses [Sparkle](https://sparkle-project.org). Once a day it reads
  `macOS/appcast.xml` from `main` on GitHub and checks the feed's EdDSA signature, then the disk
  image's before it unpacks it. An update found right after launch opens Sparkle's window; one
  found later is announced on a card and in the menus. The Windows app lists the repository's
  releases through GitHub's API once a day, downloads the newest `windows-v*` MSI in the background
  and uses it only if it matches the SHA-256 digest GitHub records for the file. Restarting to
  update runs the MSI with a progress bar only; it quits Clawd, replaces it and starts it again
  (see [Windows/NOTES.md](Windows/NOTES.md)). Both apps can turn the daily check off in Settings.

Apart from updates, the Windows app works the same way, with a named pipe instead of the socket and
`%APPDATA%\orca\orca-runtime.json` for its location.

Some features depend on Orca's internals and may stop working after an Orca update until Clawd is
updated.

## Code style

- The Swift package builds in **Swift 6 language mode** with **default `MainActor` isolation**
  (`.defaultIsolation(MainActor.self)` in `macOS/Package.swift`). Everything is on the main actor
  unless marked otherwise.
- Mark types and functions that run on background queues `nonisolated`, and keep them
  `Sendable` (see `OrcaRuntime`, `Transcripts`, `PermissionPrompt`). Hop back with `onMain`
  before touching UI or app state.
- Use system components for UI: AppKit and SwiftUI controls, SF Symbols, system materials and
  fonts. Avoid custom-drawn replacements for standard controls. The pet sprite is the
  exception.
- On Windows, build UI in C# against WinUI controls, without `.xaml` files, so the whole
  solution keeps compiling on macOS and Linux.
- No third-party dependencies beyond SwiftTerm and Sparkle (macOS) and the vendored xterm.js (Windows)
  without discussion first.
- Comments explain why, not what. Match the existing tone: short, specific, no filler.
- User-facing strings are localized in English and Korean: the String Catalog
  (`macOS/Resources/Localizable.xcstrings`) on macOS, and `Clawd.Core/Localization/*.resx` and
  `Clawd/Strings/*/Resources.resw` on Windows. Add every new string with both translations, and
  keep the wording the same in both apps; the tests check that none is missing.

## Pull requests

- Keep each pull request to one change, and describe how you tested it (which Orca version,
  which agent).
- Make sure the tests pass: `swift test` in `macOS/` and `dotnet test Clawd.Core.Tests` in
  `Windows/`. CI runs both, builds `Clawd.app` and publishes the Windows folder.
- Never commit pairing codes, tokens, transcripts or screenshots that show private data.

## Releasing

The two apps are versioned and released separately, from `main`, by the maintainer.
`macOS/VERSION` and `Windows/VERSION` hold each app's version, and tags are `macos-v<version>` and
`windows-v<version>`. Installed copies update themselves from these releases (see "Updates" under
How it works), so a published release reaches everyone within a day.

### macOS

1. Update `macOS/VERSION` (for example `1.0.1`) and commit.
2. Tag, push and create the release:
   `git tag macos-v1.0.1 && git push origin macos-v1.0.1 && gh release create macos-v1.0.1 --draft --title "Clawd 1.0.1 for macOS"`.
   Write the release notes now: the update window shows them.
3. Build and upload the app by hand, because notarization needs the Developer ID certificate,
   notarytool profile and update signing key in the maintainer's keychain:

   ```sh
   cd macOS
   ./build.sh --notarize --dmg
   gh release upload macos-v1.0.1 dist/Clawd-1.0.1-macOS.dmg
   ```

   `--notarize` notarizes and staples both the app and the disk image and checks them with
   `spctl`; do not upload if it fails. `--dmg` lays out the disk image window through Finder, so
   the first run may ask to allow Terminal to control Finder.
4. Publish the release on GitHub.
5. Add it to the update feed and push, which is what sends the update to installed copies:

   ```sh
   ./Scripts/appcast.py    # signs the disk image, adds it to appcast.xml with the release notes, signs the feed
   git commit -m "Release macOS 1.0.1" appcast.xml && git push origin main
   ```

   Do this only after publishing: Clawd downloads the disk image from the release, and a draft's
   files can't be downloaded. Change `appcast.xml` only through the script; Clawd rejects a feed
   whose signature doesn't match.

### Windows

1. Update `Windows/VERSION` (for example `1.0.1`) and commit.
2. Tag, push and create the release:
   `git tag windows-v1.0.1 && git push origin windows-v1.0.1 && gh release create windows-v1.0.1 --draft --title "Clawd 1.0.1 for Windows"`.
3. Run the [release workflow](.github/workflows/release.yml) with the tag (Actions > Release >
   Run workflow). It runs the Windows tests, publishes the self-contained win-x64 folder, builds
   the MSI from it and uploads it as `Clawd-<version>-Windows-x64.msi`. It fails if the tag does
   not match `Windows/VERSION`.
4. Edit the release notes on GitHub and publish the release. Installed copies find it through
   GitHub's API within a day. They skip drafts and pre-releases, look for the MSI by name (keep
   `Clawd-<version>-Windows-x64.msi`) and check it against the SHA-256 digest GitHub records for
   the upload.

By contributing, you agree that your contributions are licensed under the [GNU General Public License v3.0](LICENSE).
