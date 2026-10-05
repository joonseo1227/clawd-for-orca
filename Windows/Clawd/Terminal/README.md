# Live terminal (Windows)

The Windows counterpart of `macOS/Sources/LiveTerminal.swift`. The bridge process itself is `Clawd.Core/Bridge/OrcaBridge.cs`; the app wires the two together in `AppController.Live.cs`.

- `LiveTerminalView.cs`: a UserControl hosting WebView2 with xterm.js. It loads `Assets/Terminal/terminal.html` from `https://clawd-terminal.local/` (a virtual host mapped to the app folder), so it never touches the network.

## Vendored (Assets/Terminal/vendor, unmodified npm builds, MIT)

| Package | Version | Files |
|---|---|---|
| @xterm/xterm | 6.0.0 | xterm.js, xterm.css, LICENSE-xterm.txt |
| @xterm/addon-fit | 0.11.0 | addon-fit.js, LICENSE-addon-fit.txt |
| @xterm/addon-web-links | 0.12.0 | addon-web-links.js, LICENSE-addon-web-links.txt |

The files were downloaded from registry.npmjs.org, and each tarball's sha512 matched the registry `integrity` value. To update them, download the new tarballs and copy `lib/*.js`, `css/xterm.css` and `LICENSE` the same way.

## Behaviour carried over from the Mac

- Default-coloured cells are transparent, and the foreground and cursor follow the app's light or dark theme. ANSI and 24-bit colours are drawn as the agent sends them.
- The font stack is Cascadia Mono, then D2Coding (Hangul at two cells), then Consolas, at 13 px with 12 px padding.
- The mouse wheel goes to the app as xterm mouse reports when it enables mouse mode (xterm.js handles this itself). Otherwise the wheel scrolls the scrollback.
- OSC 52 (clipboard writes from output) is swallowed.
- Only http, https and mailto links open. The page and the host both check the scheme.
- Ctrl+C copies when text is selected and otherwise sends ^C. Ctrl+Shift+C always copies, and Ctrl+V or Ctrl+Shift+V pastes (bracketed paste when the app asks for it).
- Ctrl+T and Ctrl+W are passed to the chat window (`Shortcut` event) instead of the agent.
