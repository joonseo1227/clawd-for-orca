# Promo video

A 35-second 3D promo for Clawd for Orca, built entirely from scripts. See `STORYBOARD.md` for the shots.

## Render (one command)

```sh
./Promo/render.sh ko            # Korean app UI + captions -> Promo/out/clawd-for-orca-promo-ko.mp4
./Promo/render.sh en            # English app UI + captions -> Promo/out/clawd-for-orca-promo-en.mp4
./Promo/render.sh ko --resume   # keep frames already in Promo/out/frames_ko
```

Requirements: Blender 4.2+ (tested on 5.2), `ffmpeg`, and Swift (Xcode command line tools). Set `BLENDER=/path/to/blender` if `blender` is not on PATH.

Cycles finds a GPU on its own: Metal on a Mac, then OptiX, CUDA, HIP or oneAPI on a PC. It renders at 24 samples with adaptive sampling, OpenImageDenoise and persistent data. The film is 1050 frames (35 s). To split a language across machines, run `blender -b -P Promo/scene/promo.py -- --lang ko --frames A-B` on each. Frames go to `Promo/out/frames_<lang>/`, and frames already on disk are skipped. Gather them all there, then run `composite.py --lang <lang>`.

On the Windows PC (`ssh clawd-pc`), `./Promo/pc-render.sh <lang> 0-1049` renders a language there and copies the frames back. `./Promo/pc-render.sh preview <name> <lang> [promo.py args]` renders stills or an animatic into a separate folder on the PC and copies it to `Promo/out/preview/<name>/`, without touching the final frames.

Quick checks:

```sh
# a few stills
blender -b -P Promo/scene/promo.py -- --still 60,250,500 --out /tmp/check --samples 16 --scale 50
# fast animatic: EEVEE, every 3rd frame, 20% size (about 1 minute)
blender -b -P Promo/scene/promo.py -- --engine eevee --lang ko --frames 0-1049 --step 3 --samples 4 --scale 20 --out Promo/out/animatic6_ko
python3 Promo/scene/composite.py --lang ko --frames Promo/out/animatic6_ko --step 3 --out Promo/out/animatic_v6.mp4
```

## Text and languages

The 3D render contains no text. Captions and the end title come from `copy/<lang>.json`: each cue has an id, text, start, end and placement (`left`, `right`, `center` or `title`). `ui/Textures.swift` renders every copy file into `captions/<lang>/`, and `composite.py --lang <lang>` lays them over the frames. To add a language, add `copy/<lang>.json` and run `swift Promo/ui/Textures.swift Promo/textures Promo/captions`.

## Screenshots

Every UI surface is a PNG texture. The scene looks in `shots-light/<lang>/` (light-mode @2x captures of the app in that language), then `shots-light/ko/`, then `textures/` (drawn fallbacks, plus the desktop, phone overlay and cursor drawn by `ui/Textures.swift`; the phone clips come from `scene/phone_clips.py`).

| File | Used for |
|------|----------|
| `chat-permission-pet.png`, `chat-question-pet.png`, `chat-reply-pet.png`, `chat-replied-pet.png`, `chat-timeline-pet.png`, `chat-terminal-pet.png`, `chat-terminal-typed-pet.png`, `chat-terminal-after-pet.png` | The popover states opened from Clawd, arrow at the bottom. Anything below the window (the arrow and the 2D Clawd) is cropped away, and the scene's glass slab has its own arrow. `-reply-` and `-terminal-typed-` are `-question-` and `-terminal-` with the text typed in: the scene types it glyph by glyph on the same face, and finds the field, the glyphs, the caret and the send button from the difference between the two. `-terminal-after-` scrolls up into place from `-terminal-typed-` (the scroll is measured too). |
| `card-permission.png`, `card-done.png` | The cards over Clawd's head |

Each capture is cropped evenly inside its own rim, and the glass slab gets the measured corner radius. The window's flat background colours are keyed out so the frosted glass shows through. How much is set per kind of object in `layout.json` → `key`; the key tells the sidebar and the content area apart by position, so grey text stays solid. Most positions are measured from each language's own capture when the scene is built (`"auto"` in `layout.json`):

- `reveal_steps_v`: the rows revealed one by one (a stop under each row of content)
- `lift_rect`: the prompt that lifts out (its box, cut a little right of its longest line). The 허용 / Allow button inside it is found the same way (the filled blue button).

If a new capture's layout moves, check the hand-set ones: `permission_rect` (the prompt and its header, left as a hole when it lifts out), `sidebar_u`, `header_v`, `composer_v`, `tab_point` and `hide_rect`. All are fractions of the window, u from the left and v from the top.

## Assets

The room, the desk veneer and some props are CC0 assets from [Poly Haven](https://polyhaven.com), downloaded into `Promo/assets/` by `./Promo/assets/fetch.sh`:

| Asset | Used for |
|-------|----------|
| `oak_veneer_01` (texture) | the desk top (bleached) |
| `golden_bay`, `comfy_cafe`, `skate_park` (HDRIs), `strawberry_chocolate_cake` (model) | the phone clips |

The backdrop is a soft pastel gradient world built in `scene/promo.py`. `scene/phone_clips.py` renders the three phone clips into `textures/phone-clips.png` (`blender -b -P Promo/scene/phone_clips.py`).

## Files

- `scene/promo.py`: the whole Blender scene, as a pure function of time:
  - the desk, monitor and phone
  - voxel Clawd with the props and activities from `Sources/Sprite.swift` and `Pet.swift`
  - the breakout out of the screen
  - glass cards and the popover
  - lights and camera
- `scene/layout.json`: which captures to use, and positions inside them.
- `scene/composite.py`: captions per language, then H.264 encoding.
- `scene/phone_clips.py`: the phone clips, rendered from the CC0 assets.
- `ui/Textures.swift`: captions, end title, drawn fallbacks, the desktop (wallpaper and a terminal with an agent session) and cursor (SwiftUI `ImageRenderer`, system fonts). `ONLY=desktop.png swift Promo/ui/Textures.swift Promo/textures Promo/captions` renders just the named files.
- `copy/`: on-screen text per language.
- `out/`: frames, animatics and final videos (ignored by git).
