// Clawd ↔ Orca bridge.
//
// Run with Orca's own Node (ELECTRON_RUN_AS_NODE=1 Orca.app/Contents/MacOS/Orca clawd-bridge.js).
// Streaming terminal subscriptions are only offered over Orca's paired, end-to-end encrypted
// WebSocket transport, so this reuses Orca's own client code for it instead of reimplementing
// the crypto. Speaks newline-delimited JSON:
//   stdin  {"op":"pairing","pairing":{endpoint,deviceToken,publicKeyB64}}   (first line)
//          {"op":"subscribe","id":"…","terminal":"term_…"}
//          {"op":"unsubscribe","id":"…"}
//   stdout {"type":"ready","version":1} | {"type":"fatal","message"}
//          | {"id","type":"frame","result":{…}} | {"id","type":"error","message"} | {"id","type":"closed"}
'use strict'
const path = require('node:path')
const readline = require('node:readline')

// Clawd passes the folder of the Orca install it found; the default suits running by hand.
const shared = process.env.ORCA_SHARED_DIR ||
  '/Applications/Orca.app/Contents/Resources/app.asar.unpacked/out/shared'
let subscribeRemoteRuntimeRequest
try {
  ({ subscribeRemoteRuntimeRequest } = require(path.join(shared, 'remote-runtime-client.js')))
  if (typeof subscribeRemoteRuntimeRequest !== 'function') throw new Error('subscribeRemoteRuntimeRequest not found')
} catch (error) {
  // Orca moved, updated or changed its client code: say so instead of dying with a stack trace.
  // Exit only once the line is flushed; stdout to a pipe is asynchronous on macOS.
  process.stdout.write(JSON.stringify({ type: 'fatal', message: String(error?.message || error).split('\n')[0] }) + '\n',
    () => process.exit(1))
  return
}

let pairing = null
const subscriptions = new Map()

function out(message) {
  process.stdout.write(JSON.stringify(message) + '\n')
}

async function subscribe(id, terminal) {
  try {
    const handle = await subscribeRemoteRuntimeRequest(pairing, 'terminal.subscribe', { terminal }, 30_000, {
      onResponse: (response) => {
        if (response.ok) out({ id, type: 'frame', result: response.result })
        else out({ id, type: 'error', message: response.error?.message || 'subscription failed', code: response.error?.code })
      },
      onBinary: () => {},
      onError: (error) => out({ id, type: 'error', message: String(error?.message || error), code: error?.code }),
      onClose: () => {
        subscriptions.delete(id)
        out({ id, type: 'closed' })
      }
    })
    // Unsubscribed while the connection was still being set up.
    if (!subscriptions.has(id)) { handle.close?.(); return }
    subscriptions.set(id, handle)
  } catch (error) {
    subscriptions.delete(id)
    out({ id, type: 'error', message: String(error?.message || error), code: error?.code })
  }
}

readline.createInterface({ input: process.stdin }).on('line', (line) => {
  let message
  try { message = JSON.parse(line) } catch { return }
  if (message.op === 'pairing') {
    pairing = message.pairing
    out({ type: 'ready', version: 1 })
  } else if (message.op === 'subscribe' && pairing) {
    subscriptions.set(message.id, null)
    subscribe(message.id, message.terminal)
  } else if (message.op === 'unsubscribe') {
    const handle = subscriptions.get(message.id)
    subscriptions.delete(message.id)
    try { handle?.close?.() } catch {}
  }
}).on('close', () => {
  // Clawd quit: drop every subscription and leave.
  for (const handle of subscriptions.values()) { try { handle?.close?.() } catch {} }
  process.exit(0)
})
