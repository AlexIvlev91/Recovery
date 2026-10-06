# Recovery

Photo & video recovery tool for Windows, in the spirit of EaseUS Data Recovery Wizard,
plus a disk-space analyzer.

## The app (`RecoveryGui.exe`)

One window with a sidebar of tools — **Recycle Bin**, **Undelete by name**,
**Deep scan**, **Disk usage**, **Check & repair** (Ctrl+1…5). Pages stay alive
when you switch, so a long scan keeps running in the background (a dot in the
sidebar shows it's busy). The sidebar shows whether the app runs as
administrator; tools that read the raw disk offer **Restart as administrator**
(UAC prompt, reopens on the same page) instead of failing with "access denied".

Common to every list: search/filters apply live, column headers sort, ticked
items that match the current filters are what gets restored/kept ("what you see
is what you get" — the footer says when ticked items are hidden by a filter),
right-click menus offer open / show in folder / copy path, and destination
folders and the window size are remembered (`%LOCALAPPDATA%\Recovery\settings.json`).
Writing recovered data onto the drive it is being recovered from always asks
first. Unexpected errors are reported in a dialog instead of closing the app.

## Current status — Tier 1: Recycle Bin recovery

Scans `$Recycle.Bin` on every ready drive, parses the `$I` metadata files
(original path, size, deletion time; v1 Vista–8.1 and v2 Win10+ formats),
matches them to the `$R` payloads, and restores photos/videos with their
original names. Media inside deleted *folders* is found too, with folder
structure preserved on restore. Restores copy — nothing is ever removed
from the bin.

```
recover list                            # show recoverable photos/videos
recover restore --all --dest D:\out     # restore everything
recover restore --index 1,3,7 --dest D:\out
```

In the GUI the **Recycle Bin** page shows a thumbnail grid (or a details list)
with search by name/original folder, a Photos/Videos filter, sorting (date,
size, name, folder), a preview pane (EXIF-rotated) with file details, and
**Restore N files (size)…** with a progress bar. A "Sort into type folders"
toggle restores into JPG/PNG/MP4/… subfolders instead of the original folder
layout; double-click opens an item straight from the bin. Thumbnails load in
the background, visible tiles first. Thumbnails and previews use the Windows shell
(IShellItemImageFactory) — the same pipeline Explorer uses — so video frames,
HEIC, and RAW render through whatever codecs are installed, with a GDI+
fallback and a placeholder tile as last resort.

Run elevated to also see other users' bins (including the SYSTEM and admin
SIDs); non-elevated runs scan the current user's bin and warn about the rest.

## Tier 2: deep scan / signature carving

`CarveEngine` raw-scans byte ranges for media signatures and extracts hits:

- **JPEG** — `FF D8 FF` header, scan to `FF D9` end marker
- **PNG** — chunk-chain walk to `IEND` (exact length)
- **ISO-BMFF** (MP4/MOV/M4V/3GP/**HEIC**/**AVIF**) — `ftyp` + top-level box
  walk; the major brand picks the extension (AVIF/HEIC are still images, not
  video, and are labeled accordingly); requires a `moov`/`mdat` box so stray
  fragments aren't carved. Still-image containers stop at their `mdat` and are
  size-capped, so the walk can't run into adjacent free space (which otherwise
  produced multi-GB bogus files)
- **RIFF/AVI** — header size field

By default it scans only the volume's **free (unallocated) space**, obtained
via `FSCTL_GET_VOLUME_BITMAP` (`FreeSpaceMap`), so live files aren't
duplicated; `--all-space` scans everything. Raw volume access
(`RawVolumeByteSource`, sector-aligned reads over `\\.\X:`) requires
administrator rights. Carving can also run against a raw disk-image file.

```
recover carve --drive E --dest D:\out              # free space of E: (admin)
recover carve --drive E --dest D:\out --all-space  # whole volume
recover carve --drive E --dest D:\out --by-type    # sort into JPG/PNG/MP4/… subfolders
recover carve --drive E --dest D:\out --types jpg,png   # only search these types
recover carve --image disk.img --dest D:\out       # from an image file
```

The GUI's **Deep scan** page has the settings on the left (drive, file types
to look for, free space only / entire drive, size band, original-name
matching, destination) and live progress with **speed and time left**,
**Pause/Continue** and a safe **Stop** (stopping keeps whatever was already
found for review). Found files are carved into a **staging folder** and shown
as a thumbnail grid **grouped by type, heaviest first**, with type chips
(with counts), a size range and a name search applied live. Untick what you
don't want; **Keep N files** moves the ticked files that match the filters
into the destination (the rest are discarded), optionally **sorted into
per-type subfolders**. Starting a new scan or closing the app with staged
files asks first.

Carved output must go to a **different drive** than the one being scanned
(the CLI refuses without `--force`; the GUI warns). Carving works best on
HDDs, SD cards, and USB sticks — on SSDs, TRIM zeroes deleted blocks within
seconds. Fragmented files (common for large videos) may carve incompletely;
contiguous files recover byte-exact.

## Check & repair damaged files

Recovered files are often damaged. `Recovery.Core/Repair` diagnoses media by
content (not extension) and repairs what's fixable, always writing a new copy
— originals are never modified:

- **JPEG** (`JpegRepair`) — full marker/segment walk incl. entropy data and
  fill bytes. Fixes: leading garbage before SOI, trailing garbage, missing
  end-of-image on truncated files (partial image remains viewable).
- **PNG** (`PngRepair`) — chunk walk with CRC verification. Fixes: bad CRCs
  (recomputed), leading/trailing garbage, missing IEND, truncated IDAT
  (partial data salvaged with corrected length). Also **inflates the IDAT
  stream and compares it to the size the IHDR dimensions require**, so a
  structurally-valid-but-truncated image is reported as `Partial` with the
  percentage actually present — not a false `OK`.
- **MP4/MOV/HEIC** (`Mp4Repair`) — top-level box walk. Fixes: truncated
  `mdat` when the moov/meta index is intact (size field patched so the
  surviving media plays), truncated trailing box (dropped), trailing garbage
  (stripped). A missing or truncated moov/meta index is not auto-fixable —
  that needs a donor-based rebuild (roadmap). For **AVIF/HEIC still images**,
  trailing carve-overrun after the `mdat` is trimmed back to the real image.
- Content sniffing falls back to searching the first 1 MB for a JPEG/PNG
  signature, so files with garbage prefixes are still identified.

Mislabeled files (common after carving — e.g. an AVIF written as `.mp4`) can be
renamed to match their real content via **"Fix file types…"** in the GUI or
`recover fix-names --dir <folder> [--apply]`.

```
recover check --dir <folder> [--recurse]   # diagnose: ok/repairable/broken
recover check --bin                        # diagnose Recycle Bin media
recover repair --dir <folder> --dest <out> [--recurse]
```

Health verdicts: **Ok** (complete), **Repairable** (damaged wrapper, fixable
to a fully working file), **Partial** (opens/plays but content is permanently
missing — only the top of an image or start of a video survived; best-effort
output is still written), **Broken** (unfixable), **Unsupported** (bytes
aren't actually that format). "Partial" is the key distinction from a naive
structural check — a truncated-but-parseable file is *not* reported as OK.

The GUI's **Check & repair** page does the same (source: folder or Recycle
Bin) with progress, a color-coded list, health filters with counts
(Repairable / Partial / Broken / Not recognized / OK) and repair of all
fixable files or just the selected ones. **Fix file extensions…** and
**Rebuild video…** live in its header.

Important: "unrecognized format" on a previously-recovered file usually means
the recovery grabbed the wrong clusters and the content is unrelated data —
nothing can repair that; re-carving the original drive's free space is the
only hope.

## Tier 3: undelete by original name (NTFS MFT)

`Recovery.Core/Mft` reads the NTFS Master File Table directly off the raw
volume: it parses the boot sector, walks the `$MFT`'s own run list, applies
record fixups, and enumerates FILE records that are flagged **not in use**
(deleted). For each deleted photo/video it recovers the **original file name**
(from `$FILE_NAME`, preferring the Win32 name over 8.3) and the `$DATA`
location, then reads the bytes back — resident data inline, non-resident data
by following the cluster run list. Unlike carving, this restores real names
and whole files (including videos with their `moov` index intact) — but only
where the file's clusters have not yet been reused.

```
recover undelete --drive C                 # list deleted media with real names
recover undelete --drive C --dest D:\out    # recover under original names
recover undelete --drive C --dest D:\out --by-type
```

Each entry's **full original path** is reconstructed by walking parent MFT
references (e.g. `C:\gruk\photo.jpg`), so results can be filtered by folder.
The whole MFT is scanned even across unreadable regions on a live volume (a
bad batch is retried record-by-record, never aborting the scan).

After listing, the first cluster of every entry is read and compared with its
file type's signature, so each result is marked **Looks intact**,
**Overwritten** (clusters reused by another file), **Wiped** (zeros — TRIM),
**Can't tell** (no reliable signature) or **Unreadable**. The CLI prints the
same verdict per file. Recovery streams data to disk, so files larger than
2 GB work, unreadable clusters are zero-filled (and reported) instead of
shifting the rest of the file, and recovering onto the source drive is refused
(CLI, unless `--force`) or confirmed (GUI).

The GUI's **Undelete by name** page has search, Photos/Videos and size
filters, "Hide overwritten", **Select intact**, sortable columns, progress
while reading the MFT, and an **in-memory photo preview** (decoded straight
from the disk; nothing is written, so previewing can't overwrite other deleted
data). Requires administrator rights (raw volume access). Limitations: base records
only (very large/fragmented files that use `$ATTRIBUTE_LIST` are skipped), and
compressed/encrypted `$DATA` is reported but not reconstructed. The parser is
covered by a synthetic-volume self-test (`recover selftest-mft`).

## Donor-based video rebuild (moov reconstruction)

`Mp4Rebuilder` reconstructs a playable MP4/MOV whose `moov` index is missing,
using a **donor** — a healthy video from the same device/app. It clones the
donor's track layout and codec configuration (`stsd`/avcC/hvcC), re-scans the
broken file's `mdat` for **H.264/HEVC** frame boundaries (length-prefixed NAL
units, grouped into access units), and synthesizes fresh sample tables
(`stts`/`stsc`/`stsz`/`stco`/`stss`) plus a new `moov`. Output is **video-only**
(interleaved audio is not demuxed) and best-effort — verify playback.

```
recover rebuild-video --broken bad.mp4 --donor good.mp4 --dest fixed.mp4
```

The GUI exposes it via **Check & repair → Rebuild video…** (pick broken →
donor → output; runs in the background). Covered by a synthetic self-test (`recover selftest-mp4`) that builds
a moov-less file and verifies frame count, keyframe detection, and chunk
offsets after rebuild. Limits: needs a true same-source donor; H.264/HEVC only;
a missing/overwritten `mdat` can't be rebuilt (no data to index).

## Disk usage — what is taking up space

`Recovery.Core/DiskUsage` builds a folder tree with recursive sizes for a drive
or folder, broken down by file type (videos, images, audio, documents,
archives & disk images, apps & system files, other). Folders are listed by a
pool of parallel workers (~2 million files / 450k folders on a full C: in
under two minutes cold, seconds when cached). Junctions/symlinks aren't
followed (no loops or double counting), hidden/system files are included, and
OneDrive "online-only" placeholders are excluded because they use no local
space. Totals are logical file sizes (hard links count once per link).

```
recover du --path C:\                         # type breakdown, top folders, biggest files
recover du --path D:\ --type video --min-size 500MB --top 50
```

The GUI's **Disk usage** page starts with a usage bar per drive, then shows:

- a **folder tree** with a size bar per folder (share of its parent);
- a **summary** of the selected folder — size, files, folders, a stacked bar
  by file type whose legend entries are clickable, and for whole drives how
  much used space isn't in any readable file (system, restore points, page
  file…);
- **Map** — a treemap of subfolders and files (area = size, color = main file
  type; click to drill down, right-click for actions);
- **Folders** — subfolders with size, share, file count, main type, last change;
- **Largest files** — anywhere below the folder, filtered by **type**,
  **minimum size**, **age** ("untouched 1 year+") and **name**;
- **File types** — space per extension (double-click lists those files).

Files and folders can be opened, shown in Explorer, or **moved to the Recycle
Bin** (with a warning for Windows/program folders). Deletion is offered only
on drives that have a Recycle Bin, so nothing is ever deleted permanently;
totals update in place without rescanning.

## Roadmap

- Follow `$ATTRIBUTE_LIST` for large/fragmented files during MFT undelete.
- Demux interleaved audio during donor rebuild (currently video-only).

## Build

.NET 8. `dotnet build` at the repo root; the CLI publishes to a single exe with
`dotnet publish src/Recovery.Cli -c Release -r win-x64 --self-contained`.

## Layout

- `src/Recovery.Core` — scanning/restore logic: `RecycleBin/`
  (`RecycleBinScanner`, `DollarIFile`, `RestoreService`), `Carving/`
  (`CarveEngine`, `FormatCarvers`, `ByteSource`, `FreeSpaceMap`), `Mft/`
  (`MftReader`, `MftUndeleteService`), `Repair/`, `DiskUsage/`
  (`DiskUsageScanner`, `DirNode`, `DiskUsageQuery`, `FileCategory`), plus shared
  helpers (`ByteSize`, `FileNaming`, `MediaSignature`, `MediaExtensions`).
- `src/Recovery.Cli` — `recover` command-line front end.
- `src/Recovery.Gui` — WinForms front end (`RecoveryGui.exe`): `MainForm` (sidebar
  shell), `Pages/` (one page per tool), `Controls/` (buttons, chips,
  virtual-mode table, thumbnail gallery + background loader, treemap, usage bar,
  preview pane), `Theme` (colors, fonts, icon glyphs, DPI scaling).
