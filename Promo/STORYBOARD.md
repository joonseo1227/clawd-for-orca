# Clawd for Orca: 35-second promo storyboard (v6)

1920×1080, 30 fps, 1050 frames. The 3D render is done once per language, so the app UI is in that language (`--lang ko|en`, captures in `shots-light/<lang>/`). Rendered in Blender Cycles. Captions and the end title are laid over by ffmpeg, from `copy/<lang>.json`, so one render serves every language.

## The idea

You hand work to coding agents and look away (at your phone). Clawd lives at the bottom of your screen and mirrors what the agents do. When an agent needs you, Clawd breaks out of the screen to get your attention. You click it and approve right there, without opening Orca.

## Look

- **Bright and airy.** In the spirit of the Windows 11 launch film: wide empty space on a soft pastel gradient (pale sky, lilac, peach), only the key objects in sharp focus. A generic studio-style display (black glass, even thin bezel, camera dot, aluminium chassis and stand) on a pale oak desk. The UI floats as Liquid-Glass-like panels: thick refractive glass with a frosted face and a clear, bright rim; text sits crisply on a light veil.
- **Clawd.** Built voxel-for-pixel from `Sources/Sprite.swift`. On the screen it is the small glowing pixel sprite, exactly as in the app. On the desk it is the full 3D character. Its props and activities follow `Sources/Sprite.swift` and `Pet.swift`, and `Orca.swift` decides which activity matches which agent tool:
  - laptop with rising green bits for Edit
  - hammer and bench with star bursts for Bash
  - thought bubble and light bulb for Read
  - juggling for several busy agents
  - "!" with flapping claws when an agent needs you
  - hearts and confetti to celebrate
- **UI.** Real light-mode captures. Each one is cropped evenly inside its own rim and printed crisp on a 6 mm milky glass slab with the same corner radius, so there is one edge. Flat backgrounds are keyed to the frosted glass. The popover is the one opened from the pet, with its arrow pointing down at Clawd.
- **Phone.** Short-form vertical clips (a harbour at blue hour, a cake on an oak board, a skate park), photographic and brand-free, under a glass cover with reflections in a metal frame, with an action column, a caption line and a progress bar. The screen sits inside the bezel and has the body's corner radius.

## Shots

| # | Time | Picture | Caption (ko / en) |
|---|------|---------|-------------------|
| 1 | 0–6 s | Focus is on the phone in the foreground. The clips swipe up twice. On the soft monitor behind, the pixel Clawd types and then hammers. | 에이전트가 일하는 동안 / While your agents work, → 마음 편히 딴짓하세요 / go ahead, scroll. |
| 2 | 6–7.5 s | Breakout. The pixel Clawd shows "!" and hops, and focus racks from the phone to the screen. It jumps out of the screen: its pixels extrude into voxels, a warm light bursts and a ripple spreads on the screen. It lands on the desk with a squash. | |
| 3 | 7.5–13 s | On the desk Clawd hops with "!" while the "권한 필요" card rises with a warm glow. The camera pushes past the phone until the card is readable from across the room. | 확인할 게 생기면 Clawd가 불러요 / Clawd calls you when it matters. |
| 4 | 13–18.6 s | A cursor clicks Clawd. The popover springs out of the arrow tip above its head, with a slight overshoot. The prompt lifts out as its own glass slab, focus racks to 허용, the cursor presses it (it sinks in, darkens, and a ring spreads from the cursor tip), and Clawd celebrates with hearts. | Orca를 안 열어도 바로 승인해요 / Approve without opening Orca. |
| 5 | 18.7–23.5 s | The sidebar switches to api-server's question. The cursor clicks the composer, "새 테이블로 옮겨 줘" is typed in, the send button appears and is pressed, the reply bubble lands and the agent is working again. | 질문에는 바로 답하고 / Answer questions in place. |
| 6 | 23.6–26.3 s | web-app's live step timeline, row by row. | 어디까지 했는지도 한눈에 / See how far they've got. |
| 7 | 26.4–30.5 s | The cursor clicks the terminal tab. "테스트도 돌려줘" is typed at the Claude Code prompt, then Enter: the prompt moves into the history and the new output appears line by line, the terminal scrolling up with it (3 PASS, 48 tests). The approximate-terminal footer is masked. | 터미널도 그 자리에서 / Even the terminal, right there. |
| 8 | 30.6–33.4 s | The popover closes back into Clawd. A "작업 완료" card appears with hearts and confetti. | 끝나면 끝났다고 알려 줘요 / It tells you when it's done. |
| 9 | 33.5–35 s | Clawd hops back into the screen and is the pixel sprite again. The end title sits over the bright wallpaper. | **Clawd for Orca** / 에이전트 일, 이제 Clawd로 확인하세요 / Check on your agents with Clawd. |

## Notes

- All on-screen text lives in `copy/ko.json` and `copy/en.json`. The 3D render contains no text.
- Glass objects appear and leave by scale and position, never by fading the glass.
- Demo data only: checkout-flow, api-server, ios-widget, web-app, docs-site.
