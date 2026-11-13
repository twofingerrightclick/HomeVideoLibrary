# Known bugs

## Timeline preview frames interrupt playback audio on macOS

**Status:** Open; deferred at the user's request on 2026-11-12.

### Symptoms and reproduction

Play a video with audible audio, then move the pointer left and right over the playback timeline without clicking or leaving its hit area. Producing/displaying preview frames causes popping and can interrupt audio; faster movement makes it worse. The timestamp remains visible while the thumbnail portion can disappear and reappear. The cursor can also change from a hand to an arrow when the thumbnail appears.

### Investigation

- Native thread captures show VLC's CoreAudio callback blocked in `libauhal_plugin -> free_small -> _os_unfair_lock_lock_slow -> __ulock_wait2` while releasing an audio buffer.
- System audio logs recorded callbacks taking approximately 45–71 ms against an approximately 11.35 ms audio-cycle budget during the coordinated reproduction. System logs alone do not identify the responsible client.
- Both captures also contain `SystemNative_ForkAndExecProcess -> fork -> __fork` on the application's main thread. On an uncached preview, `ScrubPreviewViewModel` calls `PreviewCache`, which captures a frame through `MatroskaMetadataService` and launches FFmpeg with `MediaProcess.RunAsync` / `Process.Start()`.
- The captured runtime is .NET 10.0.14. Its macOS process-start implementation uses `fork()`. Apple's allocator locks malloc zones around a fork; another thread allocating or freeing native memory can consequently block. This is the strongest explanation for the captured audio stalls, ahead of popup/rendering overhead or decoder CPU contention.
- Process memory remained approximately 467–470 MiB in the second capture; these observations do not establish a thumbnail memory leak. One-second process polling can miss brief FFmpeg processes.

**Confidence:** Strong mechanism supported by sources and both ends appearing in the captures, but aggregated stack samples do not establish which fork held the allocator lock for each audible pop. The audio root cause is not yet confirmed by an isolated comparison.

### Changes already made

The preview now retains its previous image while the next loads, reserves the thumbnail area's size, and explicitly sets the popup/tooltip hand cursor and disables hit testing. Desktop UI and control tests passed (209 total). These changes address flicker and cursor configuration; their native cursor behavior and effect on audio have not been verified in a restarted application. They do **not** remove per-preview FFmpeg process startup.

### Next investigation when resumed

Compare ordinary playback while loading an uncached preview using a macOS process-start path without `fork()` (for example, `posix_spawn`), leaving extraction, decoding, and display otherwise unchanged. Confirm whether the pops and allocator waits disappear. Moving `Process.Start()` to `Task.Run` alone does not remove process-wide allocator locking.

For a longer-term solution, assess a persistent preview worker started before playback or reuse of prebuilt preview assets, avoiding a subprocess launch from the playback process for every uncached hover frame. No further audio fix has been implemented.

### Primary sources

- [.NET 10.0.14 process startup](https://github.com/dotnet/runtime/blob/v10.0.14/src/native/libs/System.Native/pal_process.c)
- [Apple malloc fork handling](https://github.com/apple-oss-distributions/libmalloc/blob/main/src/malloc.c)
- [VLC CoreAudio callback and audio-block release](https://github.com/videolan/vlc-3.0/blob/master/modules/audio_output/coreaudio_common.c)
