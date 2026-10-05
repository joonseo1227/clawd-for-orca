#!/bin/zsh
# One command to rebuild the promo for one language: textures + captions -> Cycles frames -> mp4.
#   ./Promo/render.sh [ko|en] [--resume]
# The 3D render uses that language's app captures (Promo/shots-light/<lang>/, falling back to ko),
# frames go to Promo/out/frames_<lang>/, the video to Promo/out/clawd-for-orca-promo-<lang>.mp4.
set -e
cd "$(dirname "$0")"
LANG_=${1:-ko}
BLENDER=${BLENDER:-$(command -v blender || echo /Applications/Blender.app/Contents/MacOS/Blender)}
swift ui/Textures.swift textures captions
[[ "$2" == "--resume" ]] || rm -rf "out/frames_$LANG_"
"$BLENDER" -b -P scene/promo.py -- --lang "$LANG_" --frames 0-1049 --samples 24
python3 scene/composite.py --lang "$LANG_"
