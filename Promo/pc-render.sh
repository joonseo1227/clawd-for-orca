#!/bin/zsh
# Renders promo frames on the Windows PC over SSH (host alias `clawd-pc`, see ~/.ssh/config) and
# copies them back into Promo/out/frames_<lang>/.
#
#   ./Promo/pc-render.sh <lang> <frames> [samples] [extra promo.py args...]
#                                     e.g. ./Promo/pc-render.sh ko 0-1049
#                                          ./Promo/pc-render.sh en 0-1049 24 --step 3 --scale 50
#   ./Promo/pc-render.sh check        is the PC reachable, which Blender and GPU
#   ./Promo/pc-render.sh preview <name> <lang> [promo.py args...]
#                                     renders into C:\clawd-render\preview\<name> (never the final
#                                     frames) and copies it back to Promo/out/preview/<name>, e.g.
#                                          ./Promo/pc-render.sh preview v7 ko --still 60,508 --samples 24
#                                          ./Promo/pc-render.sh preview anim_ko ko --frames 0-1049 --step 2 --scale 50 --samples 8
#
# The PC keeps its own copy of the scene in C:\clawd-render, refreshed from this folder on every
# run; for a final render that language's frames on the PC are cleared first, so a run is always fresh.
set -eu
cd "$(dirname "$0")"
HOST=${CLAWD_PC:-clawd-pc}
REMOTE='C:\clawd-render'

# Runs a PowerShell script (stdin) on the PC; avoids quoting through ssh and cmd. In this mode
# PowerShell reads line by line, so keep every statement on one line.
pcps() { ssh -o BatchMode=yes "$HOST" 'powershell -NoProfile -NonInteractive -Command -'; }

FIND_BLENDER='$b = (Get-ChildItem "C:\Program Files\Blender Foundation" -Recurse -Filter blender.exe -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1).FullName; if (-not $b) { throw "Blender not found" }'

# Sends the scene and the CC0 assets it loads (Promo/assets, without the phone-clip sources) to the
# PC. Only folders that changed since the last send go over (a stamp of names, sizes and mtimes is
# kept on both sides). Optional argument: one more folder under Promo on the PC to clear first.
FOLDERS=(scene shots-light textures captions copy ui assets)
stamp() { find "$1" -type f ! -name .DS_Store ! -path 'assets/clips/*' -exec stat -f '%N %z %m' {} + | sort | md5 -q; }
send() {
  local remote_stamps local_stamps="" changed=() d s
  remote_stamps=$(ssh -o BatchMode=yes "$HOST" 'type C:\clawd-render\Promo\.stamps 2>nul' 2>/dev/null | tr -d '\r' || true)
  for d in $FOLDERS; do
    s="$d $(stamp $d)"; local_stamps+="$s"$'\n'
    print -r -- "$remote_stamps" | grep -qxF "$s" || changed+=($d)
  done
  local list="" c
  for c in $changed; do list+="${list:+, }\"$c\""; done
  [ -n "${1:-}" ] && list+="${list:+, }\"$1\""
  if [ -n "$list" ]; then
    pcps <<EOF
New-Item -ItemType Directory -Force "$REMOTE\Promo" | Out-Null
foreach (\$d in $list) { \$p = "$REMOTE\Promo\\\$d"; if (Test-Path \$p) { Remove-Item -Recurse -Force \$p } }
EOF
  fi
  print -r -- "$local_stamps" > .pc-stamps
  if (( ${#changed} )); then
    echo "    sending: $changed"
    COPYFILE_DISABLE=1 tar -czf - --exclude '.DS_Store' --exclude 'assets/clips' $changed \
      | ssh -o BatchMode=yes "$HOST" "tar -xzf - -C C:/clawd-render/Promo"
  else
    echo "    nothing changed"
  fi
  # the stamps go last, so an interrupted send is retried next time
  ssh -o BatchMode=yes "$HOST" "tar -xf - -C C:/clawd-render/Promo" < <(COPYFILE_DISABLE=1 tar -cf - -s '/^.pc-stamps$/.stamps/' .pc-stamps)
  rm -f .pc-stamps
}

if [ "${1:-}" = check ]; then
  pcps <<EOF
$FIND_BLENDER
"blender: \$b"
& \$b --version | Select-Object -First 1
nvidia-smi --query-gpu=name,memory.used,memory.total --format=csv,noheader
EOF
  exit 0
fi

if [ "${1:-}" = preview ]; then
  NAME=${2:?preview name}; LANG_=${3:?lang (ko|en)}; shift 3
  print -r -- "==> Sending the scene to $HOST:$REMOTE"
  send
  echo "==> Preview $NAME ($LANG_) on $HOST: $*"
  pcps <<EOF
$FIND_BLENDER
Set-Location "$REMOTE"
if (Test-Path "$REMOTE\preview\\$NAME") { Remove-Item -Recurse -Force "$REMOTE\preview\\$NAME" }; New-Item -ItemType Directory -Force "$REMOTE\preview\\$NAME" | Out-Null
& \$b -b -P Promo\scene\promo.py -- --lang $LANG_ --out "$REMOTE\preview\\$NAME" $* 2>&1 | ForEach-Object { "\$_" } | Select-String -Pattern "Cycles GPU|Error|Traceback|ANIM|STILL|button_rect" | ForEach-Object { \$_.Line }
"rendered: " + (Get-ChildItem "$REMOTE\preview\\$NAME").Count + " files"
EOF
  mkdir -p "out/preview/$NAME"
  ssh -o BatchMode=yes "$HOST" "tar -cf - -C C:/clawd-render/preview/$NAME ." | tar -xf - -C "out/preview/$NAME"
  echo "==> Done: $(ls "out/preview/$NAME" | wc -l | tr -d ' ') files in Promo/out/preview/$NAME"
  exit 0
fi

LANG_=${1:?lang (ko|en)}
FRAMES=${2:?frames, e.g. 0-1049}
SAMPLES=${3:-24}
shift $(( $# < 3 ? $# : 3 ))
EXTRA="$*"

print -r -- "==> Sending the scene to $HOST:$REMOTE"
send "out\\frames_$LANG_"

echo "==> Rendering $LANG_ $FRAMES at $SAMPLES samples on $HOST ${EXTRA:+($EXTRA)}"
pcps <<EOF
$FIND_BLENDER
Set-Location "$REMOTE"
& \$b -b -P Promo\scene\promo.py -- --lang $LANG_ --frames $FRAMES --samples $SAMPLES $EXTRA 2>&1 | ForEach-Object { "\$_" } | Select-String -Pattern "Cycles GPU|Error|Traceback|ANIM" | ForEach-Object { \$_.Line }
\$n = (Get-ChildItem "$REMOTE\Promo\out\frames_$LANG_" -Filter *.png -ErrorAction SilentlyContinue).Count
"rendered: \$n frames"
EOF

echo "==> Copying frames back"
mkdir -p "out/frames_$LANG_"
ssh -o BatchMode=yes "$HOST" "tar -cf - -C C:/clawd-render/Promo/out frames_$LANG_" | tar -xf - -C out
echo "==> Done: $(ls "out/frames_$LANG_" | wc -l | tr -d ' ') frames in Promo/out/frames_$LANG_"
