---
target: src/HomeVideoLibrary.Desktop/Views/MainWindow.axaml
mode: Operate
---

# Native library surface

Thesis: a personal video collection should feel like a shelf of labeled tapes, with modern native organization and playback.

Pinned world: dark iPhoto-like navigation; original native VHS shells, ruled pale labels, licensed Caveat handwriting, restrained 2px hover protrusion. No web or stock-photo substitute.

First viewport: the collapsible left drawer provides search and albums; the main area shows chronological tape rows and a floating light month/year marker. Empty collections show folder/import guidance without synthetic entries. Selection actions stay visible above the shelf.

Story: choose a root, import, browse or search, preview by moving across a label, click to watch, mark chapters and save the story into the MKV. Check tapes to organize or create derivative clips.

Implementation: virtualized ListBox rows; one or two tapes according to actual available width, never column-major ordering. Preview jobs are debounced, quantized, cancelled and stale-guarded. Right-side inspector and clip editor retain the established service workflows.

Verification: actual macOS native windows inspected at 1440×900 and 1000×700 logical content sizes using a separately labeled synthetic QA library under /private/tmp. Screenshots are evidence, not product content. Other operating systems remain unverified.
