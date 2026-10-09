# Brand

- **Mark:** a sprout knocked out of a filled coin. Single colour, drawn on a 16px grid so it holds up at tray size. `mark-preview.png` compares it with the earlier outlined mark from 16 to 256 px.
- `mark.svg` is moss (`#346B4F`) for light backgrounds; `mark-dark.svg` is light moss (`#96CCA7`) for dark ones. The sprout is a hole, so the background shows through it.
- **Legibility rules:**
  - Fills only, no strokes, so nothing thins out when the mark is scaled down.
  - The coin is a circle of radius 7.5 centred on the grid. The stem is 2px wide on whole pixels (x 7 to 9) and ends at y 13; the sprout's outer edges sit on whole pixels (x 3 to 12, y 4 to 13).
  - At least 1.75px of coin surrounds the sprout. At 16px each leaf is about 3px across at its widest, and the two leaves meet at the top of the stem.
  - No outlines, rings or second colours. In Avalonia, draw it as one `Path` with an even-odd fill from the same path data.
- `app-icon.svg` puts the light moss mark at 80% on a dark canvas tile (`#111A17`), which keeps the coin edge on whole pixels at 16px. The app icon PNGs and `.ico` in `src/Mossmint.Desktop/Assets/` are generated from it; regenerate them after editing.
- **Wordmark:** "Mossmint" in Manrope Bold, −0.01em tracking, sentence case.
- **Tagline:** Make room to grow.
- The mark is licensed MIT with the code. Decorative icons (Lucide, ISC) stay separate from the financial status set.
