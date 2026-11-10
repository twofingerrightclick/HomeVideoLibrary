---
target: src/HomeVideoLibrary.Desktop/Views/MainWindow.axaml
mode: Operate
---

# Native library surface

Thesis: a personal video collection should feel like a shelf of labeled tapes, with modern native organization and playback.

Pinned world: dark iPhoto-like navigation; original native VHS shells, ruled pale labels, licensed Caveat handwriting; a hovered tape comes slightly toward the viewer (small scale-up and deeper shadow, no movement). No web or stock-photo substitute.

First viewport: the collapsible left drawer provides search and albums; the main area shows chronological tape rows and a floating light month/year marker. Empty collections show folder/import guidance without synthetic entries. Selection actions stay visible above the shelf.

Story: choose a root, import, browse or search, preview by moving across a spine, click to watch, mark chapters and save the story into the MKV. Check tapes to organize, or to trim, split or merge them into new videos.

Implementation: virtualized ListBox rows; one tape per row in a centered stack of at most 760 px, the spine keeping its 187:25 proportions. Hover frames come from pre-built per-video strips of 20 frames under `.cache`, not the chosen thumbnails; requests are debounced, cancelled and stale-guarded. The video page's right inspector holds the INFO, CHAPTERS, TAGS, FRAMES and EDIT tabs; merge has its own page; long operations run as jobs in the Activity drawer.

Verification: actual macOS native windows inspected at 1440×900 and 1000×700 logical content sizes using a separately labeled synthetic QA library under /private/tmp. Screenshots are evidence, not product content. Other operating systems remain unverified.
