# FileShare

Windows ↔ Android file transfer over local WiFi. No cloud, no accounts. Single files up to 50 GB with automatic resume after any connection drop. See `plan.md` for the design.

## What's here

```
FileShare.Win/       Windows app (.NET 8, WinForms, single exe)
FileShare.Android/   Android app (Kotlin, WebView + HTML UI in app/src/main/assets/app.html, JS bridge, Gradle 9.3.1 + AGP 9.1)
plan.md              architecture + protocol spec
```

## Windows

Run anywhere, no install: `out\windows\FileShare.exe` is a **self-contained single file (about 63 MB)** with the .NET runtime bundled and compressed. No "install .NET" prompt on a fresh PC.

Build for development (needs the .NET 8 SDK; this machine has it at `%LOCALAPPDATA%\dotnet-sdk`):

```
dotnet build D:\FileShare\FileShare.Win -c Release
```

Rebuild the standalone exe:

```
dotnet publish D:\FileShare\FileShare.Win -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:SatelliteResourceLanguages=en -o out\windows
```

Protocol self-test (loopback: clean send + hard-disconnect + auto-resume + nested-folder send, hash-verified):

```
FileShare.exe --selftest
```

## Android

Build:

```
cd D:\FileShare\FileShare.Android
.\gradlew :app:assembleDebug
```

APK: `app\build\outputs\apk\debug\app-debug.apk` (2.2 MB)

Install on a phone: enable "Install unknown apps" for your file manager/browser, copy the APK over, or with USB debugging on: `adb install app\build\outputs\apk\debug\app-debug.apk`

## Using it

1. Both devices on the same WiFi (router or phone hotspot). Windows firewall prompt on first run → tick **both** Private and Public networks: a hotspot with no internet is often classified Public, and ticking only Private blocks discovery and transfers there. No internet connection is used or needed: discovery and transfers stay on the local network.
2. Each app **auto-detects** the other on every network interface (WiFi, Ethernet, VPN, phone-hotspot subnets — UDP broadcast discovery, port 53515). Blocked? Use **Connect by IP**.
3. Select a device, pick **files or folders**, send.
   - **Windows:** "Add files…", "Add folder…", or drag files/folders onto the window. A folder arrives on the receiver with its structure intact (`Photos\2026\trip.jpg`).
   - **Android:** "Pick files", "Folder" (whole folder tree), or **Share → FileShare** from any app (photos, videos, docs, multiple files), then pick a device and hit Send.
   - Received files: Windows → `Documents\FileShare` (configurable, subfolders created as needed); Android → `Downloads/FileShare/<subfolder>` via MediaStore.
4. Live progress everywhere: Windows shows a receive strip (device name, file, %, MB/s, ETA) plus an aggregate up-speed line for sends; Android shows a percentage and progress bar on every sending and receiving file, and a foreground notification with % and bytes. Up to **4 files transfer in parallel** (one TCP stream per file), with 1 MB socket buffers and TCP no-delay on both ends, so a folder of files does not wait on one handshake at a time and a clean 5 GHz link can reach 80 Mbps. The WiFi link remains the ceiling.
4. If the connection drops mid-transfer, the sender retries with backoff (up to 6 attempts) and resumes from the receiver's exact byte offset. Cancelled/failed sends resume on re-send; nothing is ever re-downloaded from zero unless the file changed.

## Ports

| Port | Protocol | Purpose |
|---|---|---|
| 53515/udp | discovery broadcast | announce self every 2 s, prune peers after 6 s |
| 53516/tcp | transfer | JSON header → resume offset → streamed bytes → acks → ok/false |

## Status

- [x] M1 discovery — both platforms
- [x] M2 transfer both directions with progress + SHA-256 verify — Windows verified by selftest
- [x] M3 resume after hard disconnect — Windows verified by selftest (Android shares the identical protocol)
- [x] M4 50 GB design: 64-bit sizes, 1 MB streaming, <50 MB RAM budget, fsync checkpoints every 64 MB, disk-space errors surfaced, foreground service on Android
- [x] M6 folders + share-sheet: folder pick/drop on both sides, relative-path protocol (backward compatible), traversal-safe receivers, Android share target and SAF tree walk — Windows verified by selftest
- [ ] M5 on-device soak test (needs a real phone on the same WiFi; `adb devices` currently shows none connected)
