# Brand

- **Mark:** a sprout knocked out of a filled coin. Single colour, drawn on a 16px grid so it holds up at tray size. `mark-preview.png` compares it with the earlier outlined mark from 16 to 256 px.
- `mark.svg` is moss (`#346B4F`) for light backgrounds; `mark-dark.svg` is light moss (`#96CCA7`) for dark ones. The sprout is a hole, so the background shows through it.
- **Legibility rules:**
  - Fills only, no strokes, so nothing thins out when the mark is scaled down.
  - The coin is a circle of radius 7.5 centred on the grid. The stem is 2px wide on whole pixels (x 7 to 9) and ends at y 13; the sprout's outer edges sit on whole pixels (x 3 to 12, y 4 to 13).
  - At least 1.75px of coin surrounds the sprout. At 16px each leaf is about 3px across at its widest, and the two leaves meet at the top of the stem.
  - No outlines, rings or second colours. In Avalonia, draw it as one `Path` with an even-odd fill from the same path data.
- **App icon:** a minted coin with a sprout growing out of its face, on a moss tile. Unlike the mark it has depth and a warm gold rim; the mark itself stays flat.
  - `app-icon.svg` (1024 grid) is the master from 40px up: a continuous-corner tile on the macOS grid (824 of 1024), light from the top left, a reeded gold edge around a mint field, soft shadows under the coin and the sprout.
  - `app-icon-small.svg` (16 grid) is the hand-tuned variant for 16 to 32px: full-bleed tile, flat fills, a 1px gold rim, a two-leaf sprout and a stem on whole pixels at 32px.
  - `tools/Mossmint.Screenshots/AppIcon.cs` draws both and must match the SVGs. After editing, regenerate the `.ico` and `mossmint-256.png` in `src/Mossmint.Desktop/Assets/`, plus `app-icon-512.png`, `app-icon-1024.png` and `app-icon-preview.png` (16 to 256px on dark and light strips) here: `dotnet run --project tools/Mossmint.Screenshots -- --icons src/Mossmint.Desktop/Assets docs/brand`.
- **Wordmark:** "Mossmint" in Manrope Bold, −0.01em tracking, sentence case.
- **Tagline:** Make room to grow.
- The mark is licensed MIT with the code. Decorative icons (Lucide, ISC) stay separate from the financial status set.
