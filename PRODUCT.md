# Home Video Library

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

Cross-platform desktop application for Windows, macOS, and Linux using Avalonia.

## Stack

User-selected C# and Avalonia. SQLite for library organization. MKV for portable video metadata.

## Users

People organizing and watching their own home videos and recording meaningful moments as chapters.

## Product Purpose

Import home videos into a chosen library folder, browse them as a chronological stack of VHS tapes, watch them, and annotate their stories without turning routine playback into a complex editing workflow.

## Capabilities and Constraints

- Import copies source videos into the configured library and produces validated MKV files. Originals are preserved.
- Descriptions, arbitrary video and chapter tags, chapter markers and descriptions, and up to 20 chosen thumbnails belong inside the MKV, rather than only in SQLite.
- SQLite holds video paths and album membership. Search across descriptions, chapters, and tags may use a rebuildable index.
- Playback includes seeking, a collapsible right inspector, chapter navigation, and the M shortcut to mark a chapter.
- Albums support multi-selection, drag-and-drop, and adding videos that are not already members.
- Secondary editing supports trim, merge, split into two clips, and saving outputs.
- Deleting a video moves its MKV to the operating system's Trash and then forgets it, never the reverse, and only after the user confirms. Restoring it means importing it again from the Trash; its album memberships are not kept. A drive without a Trash gets a second, explicit "delete permanently" question for those videos only. A video whose file is missing can only be removed from the library (its row), never "trashed".
- Implementation proceeds in sequential agent stages. Terra reviews bugs after every stage; the original implementer fixes findings. Tests are limited to changed behavior.

## Brand Commitments

Name: Home Video Library. A modern dark desktop viewer with an iPhoto-like left library/album drawer and a right video inspector. The defining library view is a stack of approximately 60-pixel VHS spines, with handwritten descriptions, truncation, subtle hover protrusion, chronological year/month orientation, and pointer-driven thumbnail previews. Wider windows may fit two spines per row.

## Evidence on Hand

Three supplied references: iPhoto's organization layout, an illustration of VHS spines, and a photographed stack of handwritten VHS labels. References inform composition and material; watermarked imagery is not a distributable product asset.

## Product Principles

- Video annotations travel with the video.
- Preserve source footage and publish converted outputs only after validation.
- Make chapters part of normal viewing.
- Let personal labels and footage provide the visual character.

## Open Decisions

No packaging or distribution target was specified beyond cross-platform desktop. No real library media was supplied; test footage must be synthetic and labeled as such.
