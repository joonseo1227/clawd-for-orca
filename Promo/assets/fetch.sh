#!/bin/zsh
# Downloads the CC0 assets the scene uses from Poly Haven (polyhaven.com, all CC0) into Promo/assets/.
#   ./Promo/assets/fetch.sh
# hdri/      room light and reflections (lythwood_lounge)
# tex/       desk veneer (oak_veneer_01)
# models/    desk lamp, potted plant (glTF, 1k textures)
# clips/     sources for the phone clips, rendered by scene/phone_clips.py (not needed on the render PC)
set -eu
cd "$(dirname "$0")"
DL=https://dl.polyhaven.org/file/ph-assets
get() { [ -s "$2" ] || { mkdir -p "$(dirname "$2")"; curl -fsSL "$1" -o "$2"; echo "  $2"; } }
get $DL/HDRIs/hdr/2k/lythwood_lounge_2k.hdr hdri/lythwood_lounge_2k.hdr
for m in diff_2k rough_2k nor_gl_2k; do
  get $DL/Textures/jpg/2k/oak_veneer_01/oak_veneer_01_$m.jpg tex/oak_veneer_01_$m.jpg
done
# a glTF model with its 1k textures; the .bin is shared across resolutions
model() {
  local id=$1
  get $DL/Models/gltf/1k/$id/${id}_1k.gltf models/$id/${id}_1k.gltf
  get $DL/Models/gltf/8k/$id/$id.bin models/$id/$id.bin
  for m in diff nor_gl arm; do get $DL/Models/jpg/1k/$id/${id}_${m}_1k.jpg models/$id/textures/${id}_${m}_1k.jpg; done
}
model desk_lamp_arm_01
model potted_plant_04
model strawberry_chocolate_cake
for h in golden_bay skate_park comfy_cafe; do get $DL/HDRIs/hdr/4k/${h}_4k.hdr clips/${h}_4k.hdr; done
echo "assets ok"
