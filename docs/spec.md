# Home Video Library

## Product

A cross-platform C# Avalonia desktop app named **Home Video Library**, with a dark modern iPhoto-inspired layout and a distinctive chronological shelf of VHS tape spines.

## Build sequence

1. Bootstrap the solution and establish C# standards.
2. Library initialization, SQLite catalog, reliable media import and embedded MKV metadata.
3. Video playback and the primary chapter editing workflow.
4. Albums and metadata search.
5. VHS shelf interface and hover previews.
6. Secondary trim, merge, and split tools.
7. Review standards and specification coverage, fix findings, and verify changed behavior.

Each implementation stage is assigned sequentially to one agent. Astra owns the main library UI. Review before moving to the next stage and return bugs to the implementing agent.

## Library and import

- On initialization the user chooses the directory containing the library.
- SQLite stores video locations, albums, and album membership. It may contain a rebuildable search cache but must not be the source of truth for video content metadata.
- Import copies source media into the configured library without deleting the source. Each accepted file becomes MKV, with a robust probe/remux/convert pipeline rather than merely changing its extension.
- Validate whether source media can be converted. Prefer preserving streams where compatible, transcode when necessary, verify output, and expose progress, cancellation, and useful errors.
- A video appears in the library only after successful import.

## Playback and metadata (primary priority)

- Selecting a video opens playback with an optional right sidebar showing its description, video tags, and chapters.
- Users can edit a top-level description and arbitrary tags.
- Pressing **M**, or a chapter button, adds a chapter at the current playback position. Chapters support titles/descriptions and tags; users can navigate to them and edit or remove them.
- Persist descriptions, tags, and chapters inside the MKV using standard Matroska mechanisms where possible, including chapter-targeted tags. Files retain their metadata when moved out of the library.
- Users can scrub and choose up to twenty timestamped thumbnails. Selected images and timestamps are persisted in the MKV; extracted preview files are disposable cache.

## Albums and search

- Create albums from multiselected videos and add videos by dragging onto an album or the album drawer.
- Within an album, **Add** shows videos not already in it and lets the user add them.
- The left drawer includes search across video descriptions, chapter text, and tags. Search should be indexed and responsive for a substantial library.

## Library interface

- Dark modern shell with a left drawer for library navigation, search, and albums; a playback/content region; and a right metadata sidebar.
- Render videos as stacked VHS spines, one per row in a centered stack, at a real spine's 187:25 proportions (about 94 pixels high in the widest stack), with handwritten-style labels containing the user's description, truncated when space runs out.
- Read chronologically by year. A floating light month/year label tracks the current scroll position.
- Hovering a tape brings it slightly toward the viewer (a small scale-up and deeper shadow, with no movement) using Avalonia transforms.
- Moving left to right over the spine previews the video's moments from pre-built frame strips (20 frames per video, kept as a rebuildable cache; the chosen thumbnails are not used). A floating image preview transitions between frames without heavy continuous playback requirements.
- Use the supplied iPhoto and VHS photographs as visual direction; create original UI artwork rather than incorporating stock watermarks.

## Secondary video editing

- Trim and split an open video from its EDIT tab; multiselect clips to merge them on a Merge page in a clear order.
- Preserve originals and write new MKV outputs, copying streams wherever possible. The commit button reads SAVE for a copy and RENDER when a picture must be re-encoded, and an info popover explains why. Validate inputs and show the plan before expensive work.
- Run edits as background jobs in an activity drawer, queued one at a time, so the app stays usable.

## Verification

- Verify each changed stage with build checks and focused behavior tests only.
- Review against both this specification and CODING_STANDARDS.md using the code-review skill. Record honest limitations, especially unverified platform behavior and native runtime dependencies.
