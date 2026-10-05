# Runs Clawd on demo data: made-up Orca agents and a Claude Code transcript, nothing real.
# Useful for screenshots and for trying the UI without Orca.
#
#   .\Scripts\demo.ps1                 # uses the newest Debug or Release build
#   .\Scripts\demo.ps1 -Exe path\to\Clawd.exe -OpenChat
#   .\Scripts\demo.ps1 -Lang en         # the conversation and the app in English (default: ko, or $env:CLAWD_DEMO_LANG)
param([string]$Exe, [switch]$OpenChat, [switch]$Terminal,
      [ValidateSet('ko', 'en')][string]$Lang = $(if ($env:CLAWD_DEMO_LANG) { $env:CLAWD_DEMO_LANG } else { 'ko' }))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $env:TEMP 'clawd-demo'
dotnet run --project (Join-Path $root 'Clawd.Tools') -- demo $dir $Lang | Out-Null
if (-not $Exe) {
    $Exe = Get-ChildItem (Join-Path $root 'Clawd\bin') -Recurse -Filter Clawd.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Exe) { throw 'Build Clawd first: dotnet build Clawd -c Debug -p:Platform=x64' }
}
Get-Process Clawd -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500
$env:CLAWD_FAKE_ORCA = Join-Path $dir 'agents.json'
$env:CLAWD_FAKE_TRANSCRIPTS = Join-Path $dir 'transcripts'
$env:CLAWD_NO_LIVE = '1'
$env:CLAWD_UI_LANG = $Lang   # the app in the same language as its demo data
if ($OpenChat) { $env:CLAWD_OPEN_CHAT = '1' }
if ($Terminal) { $env:CLAWD_TERMINAL_VIEW = '1' }
Start-Process $Exe
"Clawd is running on demo data from $dir"
