# FileShare — Plan (v2: large files up to 50 GB)

Windows ↔ Android file transfer over local WiFi. No cloud, no accounts. Handles **single files up to 50 GB** without crashes, and **auto-resumes** after connection drops.

## Core insight (drives the design)

TCP already retransmits lost packets itself. What actually kills big transfers is:
1. **The connection dying** (WiFi roam, interference, phone sleep) → solved with **offset-based resume**.
2. **32-bit size overflow** past 4 GB → solved with **64-bit sizes everywhere** (`long` / `Long`), the #1 large-file bug.
3. **Memory blowups** → solved with **strict streaming**, never reading the file into memory.

So: no per-chunk bitmaps, no parallel streams — TCP guarantees ordering, and one WiFi link gains nothing from parallelism. Resume = remember the byte offset, reconnect, seek, continue.

## Stack (unchanged)

| Side | Tech | Why |
|---|---|---|
| Windows | C# / .NET 8, WinForms | Single small .exe, async I/O, trivial UI |
| Android | Kotlin + WebView (HTML UI, assets/app.html) | JS bridge to the transfer service, no UI framework |
| Protocol | Raw TCP + JSON header | Same spec both sides |

## Protocol v2 (resume + cumulative acks)

**Discovery (UDP 53515):** every 2s each app **auto-detects every live IPv4 interface** (WiFi, Ethernet, VPN, hotspot adapters) and broadcasts on **each subnet's own broadcast address plus 255.255.255.255**:
`{"app":"fileshare","name":"<device name>","platform":"windows|android","tcp":53516}`
Peers listen on all interfaces, prune after 6s; adapter/hotspot changes are picked up on the next tick.

**Transfer (TCP 53516), one connection per file:**
```
sender → receiver: {"v":2,"name":"movie.mkv","size":53687091200,"sha256":"...","path":"Vacation/Day1"}\n
receiver → sender: {"resume":12345678900}\n      ← bytes it already has (0 if fresh)
sender → receiver: <file bytes from that offset, 1 MB chunks>
receiver → sender: every 8 MB: {"ack":<cumulative_bytes>}\n
on completion:    {"ok":true}  |  {"ok":false,"reason":"hash"}\n
```
`path` is **optional** (v2-compatible): `/`-separated relative folder path inside a transferred folder. Absent/null = flat file at the receive root. Receivers sanitize each segment (drop `.`, `..`, drive markers, invalid chars), verify the canonical result stays inside the receive folder, create matching subfolders, and keep `.part`/`.meta` there. Same-named files in different subfolders transfer concurrently (per-file lock key is folder+name). Windows: drag/drop or "Add folder…" queues whole folders; Android: "Folder" picker (SAF tree walk) and a share-sheet target ("Share > FileShare" via SEND/SEND_MULTIPLE) stage files for sending. Android publishes received files to `Download/FileShare/<subfolder>` (MediaStore, API 29+), else its app folder.

**Resume mechanics:**
- Partial file stored as `name.part` + sidecar `name.part.meta` (`{"size":N,"sha256":"...","done":N}`).
- Receiver writes sequentially, **fsync + checkpoint every 64 MB**; last ACKed offset is the resume point.
- New transfer with matching name+size+sha → auto-resume, no prompt. Mismatch → start fresh.
- Sender keeps the last ACKed offset; on failure it does NOT trust its own count — on reconnect it re-sends the header and the receiver's `resume` value wins.

**Auto-retry loop (both sides):** up to 6 reconnect attempts, backoff 1→2→4→8→16→30s. Each successful reconnect resumes from the receiver's offset. Stall watchdog: no bytes for 30s → drop, reconnect, resume. Cancel button: closes socket, **keeps the .part** for later resume.

## Large-file rules (crash prevention)

- **64-bit everywhere:** sizes, offsets, progress math. One unit test with a fake 50 GB size to prove no int32 leaks in.
- **Streaming only:** 1 MB read/write buffers, sequential I/O. Budget: RSS < 50 MB on Android during a 50 GB transfer.
- **Receiver preallocates** the file (SetLength / truncate-to-size) before streaming to avoid fragmentation stalls.
- **SHA-256 streamed on both ends**, compared at completion (hardware-accelerated, ~negligible vs WiFi speed; catches silent corruption that a size check can't).
- **Disk-space check before starting** (need `size − resume offset` free); refuse with a clear message, not a crash at byte 49 GB.
- **Windows:** all transfer I/O async, off the UI thread; progress bar fed by a 250ms timer reading a shared counter. Window can be minimized for hours.
- **Android:** transfer runs in a **Foreground Service (dataSync)** with a progress notification — without it Android kills the app mid-transfer. `WifiLock` (high-perf) keeps WiFi awake; `MulticastLock` only for discovery. Files land in the app's external files dir, then move to `Downloads/FileShare` on completion (no SAF partial-write problems).

## UI changes from v1

- **Windows:** progress shows %, MB/s, ETA, and states: Sending / Paused (reconnecting…) / Verifying hash. **Pause/Resume button**: pause cancels the in-flight send but keeps the receiver's offset; Resume re-sends paused + pending items (receiver's offset wins). On startup, lists `.part` files in the receive folder with a Resume button. **Incoming transfers get a live receive strip** (file, device name, %, progress bar, MB/s, ETA), and the send bar shows an up-speed line.
- **Android:** same progress in the foreground notification + inline. **Pause / Resume / Cancel buttons** per transfer (Pause/Resume drive the same offset-based resume). Received-files list.

## Milestones

1. **M1 — Discovery:** both apps see each other (console/logcat fine).
2. **M2 — Transfer:** send a file both directions, correct progress + final hash match.
3. **M3 — Resume (the new core):** programmatically kill the connection mid-transfer (200 MB test file, abort at ~50%) → auto-reconnect → resume → final hash matches. Repeat with app process killed and restarted.
4. **M4 — 50 GB soak:** transfer a 50 GB pseudo-random stream — sender and receiver generate the same seeded stream locally, so no 50 GB test fixture is stored; the hash proves correctness. Monitor RAM + disk on both ends.
5. **M5 — UI polish:** progress states, .part resume lists, icons, error toasts.

## Acceptance

- 50 GB file completes; Android RSS stays < 50 MB; Windows UI stays responsive.
- Kill WiFi for 60s mid-transfer → transfer auto-resumes and finishes, final SHA-256 matches.
- Kill either app mid-transfer → restart offers resume from the checkpoint.
- ≥30 MB/s on decent WiFi (50 GB ≈ under 30 min); no throughput regression from the ack/checkpoint overhead (<2%).
- No corrupt partials ever left on disk.

## Risks

- **Thermal/battery throttling on the phone** during hour-long transfers — foreground service keeps it alive, but the AP may throttle a hot phone; throughput dips are expected, resume handles the rest.
- **Non-seekable SAF sources** (rare cloud-provider files picked from Android): sender-side resume degrades to "restart from 0" — detect and note in the toast.
- AP/client isolation & blocked broadcast → unchanged mitigation: manual **Connect by IP** in both UIs.
- Unsigned exe SmartScreen warning → unchanged.

## Out of scope (v2)

Per-chunk ACK bitmaps, parallel/multi-stream TCP, encryption, WiFi Direct, folder transfer, resume-after-PC-reboot-loses-.part (checkpoint file covers it), iOS.
