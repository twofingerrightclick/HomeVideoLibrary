# Home Video Library

**Your home movies. Their stories. Together in one file.**

A cross-platform desktop library for the videos you want to keep. Turn digitized tapes, long family recordings, and everyday clips into a collection you can browse, watch, and remember.

![Home Video Library: a chronological shelf of VHS tapes, handwritten labels, and family albums.](docs/images/library.svg)

*A familiar shelf for your home movies. Sample library shown.*

## The video is the source of truth

A home movie deserves more than a filename. Record who was there, what happened, and the moments you want to find again—right inside the video.

Home Video Library stores titles, descriptions, recording dates, tags, chapters, and chosen thumbnails **inside each MKV file**. Those memories travel with the video when you copy it, move it to another drive, or share it with family. No separate sidecar files to keep together.

The library database tracks file locations and albums, plus a disposable metadata cache. The MKVs hold the stories; albums stay with the library.

## A shelf full of memories

- **Browse a shelf of VHS tapes.** Handwritten-style labels, a chronological stack, and month markers make a growing collection feel familiar. Move across a tape to preview its contents.
- **Watch and mark the moments.** Play a long recording, press **M**, and give a chapter a name, description, and tags. Jump straight back to birthdays, first steps, or a story you want to hear again.
- **Add the context.** Give each video a title, description, recording date, and tags. Choose up to twenty frames to keep inside the MKV.
- **Find what you remember.** Search video and chapter text, filter by tags, or narrow the search to an album.
- **Make albums.** Collect family favorites, holidays, or a year's worth of recordings without duplicating the videos.
- **Keep the parts you want.** Trim a recording, split a tape, or merge clips into a new video. The originals stay intact, and chapters and chosen frames follow the new timeline.

## Mark the moment. Keep the story.

![A home movie playing beside its chapter list, with a new chapter marked at the playhead using the M shortcut.](docs/images/chapters.svg)

Press **M** while watching to mark a moment. Give it a name, add a description and tags, then **Save** to keep the story inside the MKV. Click a chapter to return to it.

## Bring your tapes into the library

Choose where your library lives, then import your videos. Original source files stay untouched. Imports copy existing MKVs or convert other supported formats to MKV, preserving the original video and audio without re-encoding whenever possible.

Import and editing jobs run in the background, so you can keep browsing while your collection grows.

## Run it

Built with **C#, Avalonia, and .NET 10** for **macOS, Windows, and Linux**. Native playback has been smoke-tested on macOS ARM64; Windows and Linux integration still needs verification.

You'll need the .NET 10 SDK, FFmpeg/ffprobe, MKVToolNix, and a matching LibVLC 3 runtime. See the [development guide](DEVELOPMENT.md#build-and-run) for platform setup and runtime configuration.

```sh
dotnet restore HomeVideoLibrary.sln
dotnet run --project src/HomeVideoLibrary.Desktop
```

[Development guide](DEVELOPMENT.md) · [Product specification](docs/spec.md) · [Interface design](DESIGN.md) · [Coding standards](CODING_STANDARDS.md)
