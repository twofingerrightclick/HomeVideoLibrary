---
name: Home Video Library
description: A shelf of handwritten VHS memories, operated like an 80s/90s VCR.
tokens: src/HomeVideoLibrary.UI/Themes/Tokens.axaml
components: src/HomeVideoLibrary.UI (gallery: src/HomeVideoLibrary.Gallery)
---

# Design System: Home Video Library

## Overview

**Creative North Star: "The tape shelf and the deck."** The library is a shelf of VHS spines with handwritten
labels. Playing a tape puts it in a deck: molded plastic keys, rubber remote buttons, an LCD counter and a
chaptered scrub bar.

The visual language comes from `ui-reference/`: black VCR chassis, silkscreen legends above keys, plastic keys
that travel in when pressed, matte rubber remote keys, REC red, power green, Smart Search yellow and an amber
VFD. No reference photograph ships as an asset.

## Tokens and the component library

Every color, shadow, radius, size and font is a `Vcr.*` token in `HomeVideoLibrary.UI/Themes/Tokens.axaml`.
Some control-local tokens sit at the top of each theme file. App views never use hex colors; app-level
surfaces (drawer edge, toast, sticky pill, notices, shelf rows) live in
`Desktop/Styles/AppStyles.axaml` and use tokens only.

The library restyles stock controls (Button becomes a plastic key, ToggleButton a latch key) and adds:
PlasticButton, RubberButton, LatchButton, LegendFrame, `TabControl.inspector`, ScrubBar, ChapterListItem,
ChapterPlaceholderRow, TagChipInput, VhsSpine, LcdCounter, `Slider.volume`, SectionHeader and VcrDialog. The
gallery (`dotnet run --project src/HomeVideoLibrary.Gallery`) shows every control in every state.

**The Paper Rule.** Light material belongs to tape labels, the sticky month pill and toasts.

**The Handwriting Rule.** Caveat handwriting is the user's tape label only, never navigation or form controls.

## Library window

- **Drawer (248 px, collapsible):**
  - Search, with Cmd/Ctrl+F; Esc clears it.
  - LIBRARY › All videos, with a count.
  - ALBUMS, with a + key that creates "New album" and opens an inline rename.
  - SETTINGS and the index status.
  - Album rows are drop targets for dragged tapes and have a context menu: Rename, Add selected videos, Delete
    album.
- **Toolbar:** sidebar latch, view title and count, ADD VIDEOS… (when an album is shown) and IMPORT VIDEOS
  (primary). Importing shows no progress here; it is a job in the Activity drawer.
- **Selection bar** (only with a selection):
  - The count, plus how many the view hides.
  - PLAY, ADD TO ALBUM ▾, NEW ALBUM.
  - TRIM / SPLIT (one selected) and MERGE (two or more).
  - REMOVE FROM ALBUM (when an album is shown), MOVE TO TRASH, and REMOVE FROM LIBRARY (only for videos whose file is
    missing).
  - A clear key.
- **Shelf:** one virtualized column with one spine per row, in a stack centered at no more than 760 px wide.
  - A month divider precedes the first tape of each month. A paper pill pinned at the top shows the month at the
    viewport top.
  - A search that matched a chapter adds a "▸ 12:34 · title" line that opens the tape at that chapter.
- **Spine:** VhsSpine.
  - Its printed sticker art runs vertically, as on a real spine label; the handwritten label reads horizontally.
  - Sticker stock, accent and brand mark are a stable pick per video.
  - The shell shows the running time, or MISSING / UNREADABLE (details in the tooltip).
  - A selection checkbox sits on the leading edge.
  - Spines keep a real VHS spine's 187:25 proportions at every size. In the full 760 px stack (less 28 px of margin
    per side) a spine is 704 px wide and about 94 px tall; in a narrower window it shrinks with the stack.
  - Hover brings the tape slightly forward (2.5% scale, deeper shadow; no shift, no indicator light). Moving the
    pointer along the spine scrubs a frame preview above it, taken from the tape's pre-built hover strip (20 frames
    in `.cache/strips`, never the chosen thumbnails); "Preparing preview" shows until the strip exists. Reduced
    motion (macOS setting) keeps the tape still.
  - Clicking a tape plays it coming out toward the viewer (180 ms scale-up and fade) before its page opens; skipped with reduced motion.
- **Status bar:** the status line and the Activity key. Its light is on while a job is queued or running and its text
  summarizes the current job, how many wait, and failures. It opens the Activity drawer, a native popup so it also
  opens over a playing video: every import, render, Trash move and backfill as a row with progress, ETA when known,
  "Queued (#n)", cancel or remove, and OPEN for a finished job's output. Imports and renders are separate FIFO
  lanes, one job at a time each. A chime plays when a batch finishes if button sounds are on. Closing with jobs
  running asks whether to wait for them, cancel them or keep the app open.
- **Settings:** a real modal window with the library folder, refresh/rebuild with a scrollable per-file error
  list, media tool health and the button-sound switch.

## Video page

A toolbar (◀ LIBRARY, title, the UNSAVED light, REVERT, SAVE and a More menu), the video over a transport deck, and a
right inspector that can be hidden and resized.

- **Transport deck:** scrub bar with chapter marks and an in/out range while editing, track search, REW, PLAY/PAUSE, FF,
  LCD TIME and TOTAL counters, MARK (M), volume with mute, and a fullscreen key with an inspector latch.
- **Inspector tabs** (Cmd/Ctrl+1 to 5): INFO (title, description, recording date, file, albums), CHAPTERS, TAGS, FRAMES
  (the chosen thumbnails stored in the MKV) and EDIT.
- **Chapters** expand in place into their own editor (title, start, description, tags); one row is open at a time.
- **Editing is explicit:** nothing is written until SAVE. An amber UNSAVED light shows while edits are pending.
- **Fullscreen:** the controls come from a transparent native overlay window over the video and hide, with the
  pointer, 2.5 s after the pointer last moved while the video plays.
- **EDIT tab and Merge page:** trim (in/out, PRECISE), split at the playhead, and merge in a chosen order. The commit
  key is driven by a plan computed from the file: SAVE when every picture is copied (a precise cut that re-encodes
  only the frames at its cuts counts), RENDER when a picture must be re-encoded. The ⓘ key beside it explains in plain
  words what will happen and why. The edit runs as a render job; the original is never changed.

## Pointer previews

Every preview or tooltip that follows the pointer (shelf frame preview, scrub-bar tooltip) uses the library's
`Popup.pointer-preview` style: it opens above its target and flips below only when the screen has no room.

## Native video surface

LibVLC draws into a native child view. Avalonia content in the same window cannot draw over it. Therefore:

- Anything that must appear over the video is a native window: the scrub bar's hover tooltip (a native popup
  above the bar, with chapter title, time and frame), modal prompts (PromptWindow and VcrDialog in a real window)
  and fullscreen controls.
- Overlay-layer adorners, toasts and drag hints never sit over the video area.

## Interaction

- Every key binds a view-model command.
- Long operations show progress and can be cancelled.
- Modal questions (unsaved changes) are awaited in one flow. Leaving a page with unsaved edits asks: Save and
  leave, Discard or Keep editing.
- Destructive actions (Move to Trash, Delete permanently, Remove from library) ask in a real modal window that lists
  the affected videos with their size and albums. CANCEL is the default key, so Enter and Esc both keep the videos.

**Shortcuts.** Each view has one ShortcutRouter. It ignores keys typed into text fields and tag entry (command
chords such as Cmd/Ctrl+S still work there) and plays the sound of the on-screen key it stands for.

| Scope | Keys |
|---|---|
| Global | Cmd/Ctrl+F search, Cmd/Ctrl+I import, Cmd/Ctrl+, settings |
| Library | Arrows/Home/End/Page move between tapes; Enter play; Space / Shift+Space select / range; Cmd/Ctrl+A select all; Esc clears selection, then search; Cmd/Ctrl+N new album from selection; F2 rename the focused album; Delete/Backspace move to Trash, or remove from the shown album; Cmd+Backspace / Ctrl+Delete move to Trash in any view; Cmd/Ctrl+Shift+M merge |
| Video page | Space / K play; Left / Right seek 5 s (Shift 1 s); J / L seek 10 s; Home / End; [ ] or Page Up / Down previous / next chapter; Up / Down volume; 0 mute; M add chapter; I / O trim in / out; F2 rename chapter; Delete / Backspace remove the focused chapter; Shift+C capture frame; F fullscreen; Esc leaves fullscreen, then the page; Cmd/Ctrl+S save; Cmd/Ctrl+\ inspector; Cmd/Ctrl+1 to 5 tabs |
| Merge page | Alt+Up / Down move the selected clip; Esc back |

## Sound

Button sounds are off by default and toggled in Settings, which saves the choice to `settings.json`:

- Plastic keys, latches and tabs click crisply; rubber keys thud.
- Spines, chips and list rows are silent.
- Sounds are synthesized at runtime, with no audio assets.
- Seek shortcuts, key repeats and programmatic changes never make a sound.

## Do's and Don'ts

- **Do** keep keyboard focus visible. Every tape is reachable by keyboard.
- **Do** use real descriptions and honest empty states.
- **Do** put anything that must cover video in a native window.
- **Don't** distribute watermarked reference imagery.
- **Don't** preload thumbnail bitmaps for the entire library.
- **Don't** write hover captures into MKV metadata.
