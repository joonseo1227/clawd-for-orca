// Clawd's live terminal page: xterm.js fed with Orca's terminal stream by LiveTerminalView.
//
// Host -> page (PostWebMessageAsJson):
//   {t:'load', d}  reset and replay a snapshot      {t:'feed', d}   append output
//   {t:'clear'}    empty the screen                 {t:'theme', dark}
//   {t:'font', size}                                {t:'focus'}
// Page -> host (postMessage, JSON):
//   {t:'ready'} {t:'input', d} {t:'resize', cols, rows} {t:'link', url} {t:'copy', d}
'use strict'
;(() => {
  const host = window.chrome && window.chrome.webview
  const post = (m) => { if (host) host.postMessage(m) }

  // Default-coloured cells stay transparent so the flyout's Mica shows through. The RGB part
  // still matters: inverse video of the default background draws text in it, made opaque.
  // ANSI colours are VS Code's terminal palettes, readable on both; Claude Code mostly uses
  // 24-bit colour, which xterm.js draws exactly as sent.
  const themes = {
    light: {
      background: 'rgba(243, 243, 243, 0)', foreground: '#1b1b1b', cursor: '#1b1b1b', cursorAccent: '#f3f3f3',
      selectionBackground: 'rgba(0, 95, 184, 0.25)',
      scrollbarSliderBackground: 'rgba(0, 0, 0, 0.2)', scrollbarSliderHoverBackground: 'rgba(0, 0, 0, 0.35)',
      scrollbarSliderActiveBackground: 'rgba(0, 0, 0, 0.45)',
      black: '#000000', red: '#cd3131', green: '#00bc00', yellow: '#949800', blue: '#0451a5', magenta: '#bc05bc',
      cyan: '#0598bc', white: '#555555', brightBlack: '#666666', brightRed: '#cd3131', brightGreen: '#14ce14',
      brightYellow: '#b5ba00', brightBlue: '#0451a5', brightMagenta: '#bc05bc', brightCyan: '#0598bc', brightWhite: '#a5a5a5'
    },
    dark: {
      background: 'rgba(32, 32, 32, 0)', foreground: '#f2f2f2', cursor: '#f2f2f2', cursorAccent: '#202020',
      selectionBackground: 'rgba(96, 205, 255, 0.3)',
      scrollbarSliderBackground: 'rgba(255, 255, 255, 0.2)', scrollbarSliderHoverBackground: 'rgba(255, 255, 255, 0.35)',
      scrollbarSliderActiveBackground: 'rgba(255, 255, 255, 0.45)',
      black: '#000000', red: '#cd3131', green: '#0dbc79', yellow: '#e5e510', blue: '#2472c8', magenta: '#bc3fbc',
      cyan: '#11a8cd', white: '#e5e5e5', brightBlack: '#666666', brightRed: '#f14c4c', brightGreen: '#23d18b',
      brightYellow: '#f5f543', brightBlue: '#3b8eea', brightMagenta: '#d670d6', brightCyan: '#29b8db', brightWhite: '#e5e5e5'
    }
  }

  // Links come from terminal output, so only web and mail links open; the host checks again.
  const openLink = (uri) => {
    try {
      const scheme = new URL(uri).protocol
      if (scheme === 'http:' || scheme === 'https:' || scheme === 'mailto:') post({ t: 'link', url: uri })
    } catch {}
  }

  const term = new Terminal({
    allowTransparency: true,
    allowProposedApi: false,
    // D2Coding draws Hangul at exactly two cells; Cascadia Mono has none and falls back to it.
    fontFamily: '"Cascadia Mono", "D2Coding", Consolas, monospace',
    fontSize: 13,
    cursorBlink: false,
    cursorInactiveStyle: 'outline',
    scrollback: 5000,
    minimumContrastRatio: 1,
    theme: themes.light,
    linkHandler: { activate: (_event, uri) => openLink(uri), allowNonHttpProtocols: true }
  })
  const fit = new FitAddon.FitAddon()
  term.loadAddon(fit)
  term.loadAddon(new WebLinksAddon.WebLinksAddon((_event, uri) => openLink(uri)))

  // OSC 52 lets terminal output replace the clipboard unasked; the user copies by selecting.
  term.parser.registerOscHandler(52, () => true)

  term.open(document.getElementById('terminal'))

  term.onData((d) => post({ t: 'input', d }))
  term.onBinary((d) => post({ t: 'input', d }))

  // Ctrl+C copies when there is a selection (otherwise it's ^C for the agent), as in Windows
  // Terminal; Ctrl+Shift+C always copies. Ctrl+V and Ctrl+Shift+V are left to the browser,
  // whose paste event xterm.js turns into a (bracketed) paste.
  term.attachCustomKeyEventHandler((e) => {
    if (!e.ctrlKey || e.altKey || e.metaKey) return true
    const key = e.key.toLowerCase()
    // Ctrl+T (chat view) and Ctrl+W (close) belong to the chat window, as on the Mac with ⌘.
    if ((key === 't' || key === 'w') && !e.shiftKey) {
      if (e.type === 'keydown') post({ t: 'shortcut', k: key })
      // Unprevented, Windows still delivers the key's control character ('\x14', '\x17') as text
      // input, which xterm.js would type into the agent.
      e.preventDefault()
      return false
    }
    if (key === 'c' && (e.shiftKey || term.hasSelection())) {
      if (e.type === 'keydown' && term.hasSelection()) {
        post({ t: 'copy', d: term.getSelection() })
        term.clearSelection()
      }
      e.preventDefault()
      return false
    }
    if (key === 'v') return false
    return true
  })

  // Fit to the page and tell the host the grid; it resizes the agent's PTY to match.
  let size = { cols: 0, rows: 0 }
  let fitQueued = false
  const refit = () => {
    if (fitQueued) return
    fitQueued = true
    requestAnimationFrame(() => {
      fitQueued = false
      const el = document.getElementById('terminal')
      if (!el.clientWidth || !el.clientHeight) return
      try { fit.fit() } catch { return }
      if (term.cols !== size.cols || term.rows !== size.rows) {
        size = { cols: term.cols, rows: term.rows }
        post({ t: 'resize', cols: term.cols, rows: term.rows })
      }
    })
  }
  new ResizeObserver(refit).observe(document.getElementById('terminal'))
  if (document.fonts) document.fonts.ready.then(refit)

  // Output is written once per animation frame, however many chunks arrived in it.
  let pending = []
  let writeQueued = false
  const flush = () => {
    writeQueued = false
    if (pending.length === 0) return
    const data = pending.join('')
    pending = []
    term.write(data)
  }
  const feed = (d) => {
    pending.push(d)
    if (!writeQueued) { writeQueued = true; requestAnimationFrame(flush) }
  }
  // RIS goes through the parser, so it lands after anything xterm.js is still writing.
  const reset = () => { pending = []; term.write('\x1bc') }

  const handle = (m) => {
    if (!m || typeof m !== 'object') return
    switch (m.t) {
      case 'load': reset(); term.write(String(m.d || '')); break
      case 'feed': feed(String(m.d || '')); break
      case 'clear': reset(); break
      case 'theme': term.options.theme = m.dark ? themes.dark : themes.light; break
      case 'font':
        if (typeof m.size === 'number' && m.size > 4) { term.options.fontSize = m.size; refit() }
        break
      case 'focus': term.focus(); break
    }
  }
  if (host) host.addEventListener('message', (e) => handle(e.data))

  refit()
  post({ t: 'ready' })
})()
