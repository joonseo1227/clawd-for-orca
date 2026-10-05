# Security policy

## Reporting a vulnerability

Please do not report security issues in public GitHub issues.

Report them privately through GitHub's
[private vulnerability reporting](https://github.com/joonseo1227/clawd-for-orca/security/advisories/new)
(Security tab > Report a vulnerability). Include:

- what the issue is and what an attacker could do with it,
- steps to reproduce, and
- the Clawd version (Settings shows it), your OS (macOS or Windows) and its version, and the
  Orca version.

You should get a reply within a week. Once a fix is available, the advisory will be published
with credit to you unless you prefer otherwise.

## Scope

Areas that matter most:

- handling of the Orca pairing code and device token (stored in the Keychain on macOS and the
  Credential Manager on Windows, passed to the bridge over stdin),
- the bridge process (`Bridge/clawd-bridge.js`, shared by both apps) and how Clawd launches it,
- use of Orca's runtime socket (a named pipe on Windows) and auth token,
- anything that could send input to an agent's terminal without the user asking for it.

Vulnerabilities in Orca itself or in Claude Code should be reported to Stably AI or Anthropic
respectively.

## Supported versions

Only the latest release receives security fixes.
