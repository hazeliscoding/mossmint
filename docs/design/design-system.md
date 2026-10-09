<!-- Imported from the Mossmint Claude Design project (Mossmint.dc.html). -->

# Mossmint design system (desktop, Avalonia)

Built on the Quorum component vocabulary (components, type scale, density, motion, status rules) re-tuned to the Mossmint palette. Reference mockup: `Mossmint.dc.html` (1440×900, dark; light via the Theme tweak).

## Color tokens

Use theme resources (`DynamicResource`), never literals in views. Dark is the first theme; light is a re-tuning, not an inversion.

| Token | Dark | Light | Use |
|---|---|---|---|
| Canvas (bg-0) | `#111A17` | `#F7F6F0` | window background |
| Panel (bg-1) | `#16221E` | `#F2F1EA` | sidebar, top bar, inspector |
| Surface (bg-2) | `#1B2823` | `#FFFFFF` | cards, tables |
| Elevated (bg-3) | `#25372F` | `#EDF2EC` | hover, popovers |
| Selected (bg-4) | `#2E4239` | `#E2EAE3` | pressed, current nav |
| Inset | `#0C1411` | `#F1F3EE` | code / path wells |
| Border 1/2/3 | `#243229` / `#304439` / `#43594C` | `#E3E6DF` / `#D1D7CF` / `#B2BDB4` | hairline / default / strong |
| Text 1/2/3/4 | `#F3F5F0` / `#B4C1B9` / `#7E8F85` / `#51625A` | `#202B25` / `#536159` / `#7A877F` / `#AEB8B1` | primary / secondary / faint / disabled |
| Accent (moss) | `#96CCA7` | `#346B4F` | primary actions, selection, focus, forecast line |
| Lavender | `#BBA8E4` | `#7867A8` | *Estimated / Scheduled / Projected* and inflows |
| Healthy | `#8ED39F` | `#2F7D55` | Confirmed, covered, funded |
| Warning | `#E6C879` | `#8C6200` | Needs review, stale, differs from lender |
| Critical | `#F29E9B` | `#B43E39` | Shortfall below cushion, formula error, broken sync |
| Loss (outflow emphasis) | `#D9AE6A` | `#A8722A` | negative deltas on "what changed" |

Contrast: all text tokens on their surfaces ≥ 4.5:1 (text-3 is for labels ≥ 11px semibold only). Accent on bg-2 dark = 8.9:1; light accent on white = 6.4:1. Verify warning/critical text variants in high-contrast mode by mapping them to `SystemColors`.

Signal rule: color is never the only carrier. Every state pairs a dot/shape with a text label (StatusBadge). Positive/negative on charts is encoded by position relative to the cushion line plus a label, not hue.

## Typography

- Sans: **Manrope** (OFL) for all human-readable text.
- Mono: **IBM Plex Mono** (OFL) for every number, date, ID, path and cell reference, with `tnum` tabular numerals. Both licenses permit bundling in an MIT app; ship the font files with LICENSE in `Assets/Fonts/` and declare them in `App.axaml` as `FontFamily` resources. Fallback: Segoe UI / Cascadia Mono.
- Scale (px / weight / line): display 28/600/1.15 · page title 20/600 · section 15/600 · card title 13/600 · body 13/400/1.5 · compact 12/400 · label 11/600 uppercase +.06em · micro 10/500 uppercase +.08em · data 12/500 mono · data value 13/500 mono · metric 24/500 mono · metric-lg 32/500 mono.
- Money: mono, tabular, cents dimmed when space is tight; negative with a true minus `−`; never red for "spending" (red is reserved for shortfall).

## Spacing, density, shape

- 8px grid with 2/4/6 increments. Window padding 20–24px, card padding 14–16px, card gap 12px.
- Sidebar 216px (48px collapsed, labels in tooltips). Top bar 44px. Inspector 380px, becomes an overlay drawer below ~1100px window width. Title bar 32px.
- Density attribute on any ancestor: comfortable (40px rows), compact (32px, default), dense (26–30px, cash flow table, ledgers).
- Radii: 2px dense, 4px controls, 6px cards, 8px panels/modals. Round only for status dots and avatars. 1px hairline borders; cards carry a 1px inner highlight, no drop shadow at rest.

## Motion

Instant 80 · fast 120 · standard 180 · panel 240 · complex 320 ms. Panels slide in 12px from the right; toasts rise 6px; chart lines extend. All durations zero when the OS reduce-motion setting is on (`PlatformSettings`), and no element bounces.

## Focus, keyboard, hover

- Focus: two-ring halo (2px canvas + 2px accent), visible on every focusable element including table rows and chart points.
- Hover lightens one surface step; press moves 0.5px down; buttons never change size.
- Shortcuts: `Ctrl+K` search · `Ctrl+N` quick add · `Ctrl+1…8` primary sections · `Ctrl+,` settings · `Esc` closes inspector/modal · `?` on any metric opens its explanation · `Alt+←/→` import wizard steps.
- Tab order per screen: top bar → sidebar → page actions → content left-to-right, top-to-bottom → inspector.

## Components (what exists in the mockup)

App shell/sidebar with badges · Windows title bar · profile switcher (name only until unlocked) · status badge (Manual · Offline, Stale, Sync pending) · attention drawer · financial metric card (label, value, human headline, three mono facts, `?` explanation, click to drill) · forecast line chart with cushion threshold, paycheck markers and lowest-point marker · data source chips: Confirmed (healthy), Scheduled (info/lavender), Estimated (lavender), Needs review (warning), Synced (running) · waterfall explanation list · payoff progress bar · extra-payment comparison columns · paycheck assignment card · bills table with Mark paid + undo toast · ledger with transfer linkage and duplicate flag · reconciliation issue card (resolve / flag as unverified) · import preview counts · provenance table · empty-ish states on Connections · destructive confirmation (type-to-confirm) · scenario comparison cards and Apply modal with change summary.

Input states: default (border-2), focused (focus halo), invalid (critical border + message under the field, never color only), disabled (text-4, no border change). Currency input accepts `1,234.56` and `1.234,56` per locale, paste, and arrow-key stepping.

## Charts

Every chart has a data table equivalent (the Cash Flow screen is the forecast table). Labels never below 11px. Series: moss solid = confirmed+scheduled; lavender dashed = projected; gray dashed = cushion threshold. Markers: annotation (paycheck), anomaly (lowest point). Hover shows a crosshair and the day's events.

## Logo and assets

Working mark: a sprout rising from a coin-circle, single-color, built on a 16px grid so it survives at taskbar/tray size. Supply SVG master, 16/20/24/32/48/256 PNG, and `.ico`. License the mark CC0 or MIT alongside the code; keep decorative iconography (Lucide, ISC) separate from the financial status set.
