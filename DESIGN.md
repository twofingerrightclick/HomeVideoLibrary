---
name: Home Video Library
description: A quiet native library of handwritten VHS memories.
colors:
  library: "#191D1F"
  drawer: "#23282B"
  text: "#E7EBEC"
  secondary-text: "#A6B2B8"
  paper: "#EAE5D5"
  ink: "#252D30"
  primary: "#B7D4DD"
  primary-ink: "#182A31"
  tape: "#292C2D"
typography:
  tape-label:
    fontFamily: Caveat
    fontSize: "27px"
    fontWeight: 500
  headline:
    fontFamily: system-ui
    fontSize: "27px"
    fontWeight: 600
  body:
    fontFamily: system-ui
    fontSize: "13px"
    fontWeight: 400
rounded:
  control: "4px"
  tape: "5px"
spacing:
  tight: "8px"
  group: "16px"
  section: "24px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.primary-ink}"
    rounded: "{rounded.control}"
    padding: "7px 13px"
    height: "34px"
---

# Design System: Home Video Library

## Overview

**Creative North Star: "The personal tape shelf"**

The user's pinned visual world combines the organization of a dark iPhoto library with the physical clarity of VHS spines. Native controls support real library work; warm paper and handwritten descriptions carry the personal character. No supplied reference photograph is shipped as an asset.

**Key Characteristics:**

- Dark, quiet navigation surrounding pale handwritten tape labels.
- Dense chronological browsing with modest physical depth.
- Familiar desktop inputs and explicit playback, editing, and selection actions.

## Colors

The neutral shell recedes around warm label paper. Pale blue identifies primary import and save actions. Four muted printed tape details vary by stable video identity; they communicate material character, not metadata or selection.

**The Paper Rule.** Light material belongs to personal tape labels and the current month/year marker.

## Typography

Bundled Caveat supplies the handwritten tape labels; its OFL license accompanies the font. Avalonia's native default face handles commands, forms, metadata, and headings. Tape labels stay on one line and truncate rather than expanding row height. Small 8–10px VHS printing is decoration; video descriptions, controls, and status use the larger readable hierarchy.

**The Handwriting Rule.** Handwriting belongs to the user's video label, never to navigation or form controls.

## Layout

The desktop shell uses a collapsible 236px left drawer. Main content has 28px horizontal margins. Library tapes occupy virtualized rows with a 72px pitch and a 60px shell. At 1000px of available list width, the view fits two spines per row, preserving row-major chronological order. The month/year label is outside the scrollable list and follows its visible offset.

Playback uses the main area with a collapsible 300px right inspector. Controls remain below, outside the embedded native video surface. Wrapped command groups and independently scrollable inspectors support the 900px minimum window width.

## Elevation & Depth

Tape shells use narrow side rails, a restrained rim, and a downward shadow (`0 3 5 0 #70000000`). Hover raises a spine by 2px over 140ms. The small frame preview uses a deeper downward shadow and a 160ms image crossfade. Native controls retain their platform focus and disabled states.

## Shapes

Controls have small rounded corners. Tape shells have a slightly larger molded edge with inset rectangular paper and printed end detail. Avoid turning the tape rows into generic cards or full-width image thumbnails.

## Components

### Buttons and inputs

Use Avalonia controls with visible focus, semantic accessible names, 34px minimum button/input height, and 4px corners. Primary import and save actions use the pale-blue fill. Maintenance operations belong inside Library settings; album creation and rename live under Manage albums.

### VHS spine

Each spine has an always-visible, bounded checkbox beside its paper label. A plain label click plays; modifier-click or Space toggles selection, Enter plays the keyboard-focused tape. The separate Play, Edit clips, and Add to album controls remain available.

### Pointer preview

Only the hovered label requests frames. Ten quantized positions, a 160ms debounce, cancellation, and stale-result checks bound the work. Chosen embedded thumbnails are sorted by position; otherwise a cache-only capture is requested. The view holds at most two decoded 416px-wide bitmaps for crossfade and disposes them on exit. The popup does not receive pointer input.

### Editors

The right inspector keeps description, tags, chapters, and chosen thumbnail capture together. Dirty navigation preserves Save, Discard, and Keep editing choices. The clip editor exposes output order and range fields; its inclusion disclosure explains the primary-picture/audio limitation.

## Do's and Don'ts

- **Do** preserve native focus, keyboard operation, and explicit selection controls.
- **Do** use real descriptions and honest empty states.
- **Do** keep controls outside the native video surface.
- **Don't** distribute watermarked reference imagery.
- **Don't** preload thumbnail bitmaps for the entire library.
- **Don't** write hover captures into MKV metadata.
