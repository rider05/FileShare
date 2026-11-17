# FileShare

Windows ↔ Android file transfer over local WiFi. No cloud, no accounts. Single files up to 50 GB with automatic resume after any connection drop. See `plan.md` for the design.

## What's here

```
FileShare.Win/       Windows app (.NET 8, WinForms, single exe)
FileShare.Android/   Android app (Kotlin, Compose, Gradle 9.3.1 + AGP 9.1)
plan.md              architecture + protocol spec
```

## Windows

Build (requires the .NET 8 SDK; this machine has it at `%LOCALAPPDATA%\dotnet-sdk`):

```
dotnet build D:\FileShare\FileShare.Win -c Release
```

Run: `D:\FileShare\FileShare.Win\bin\Release\net8.0-windows\FileShare.exe`
(If `dotnet` complains about missing frameworks, set `DOTNET_ROOT=%LOCALAPPDATA%\dotnet-sdk` first, or install the full .NET 8 Desktop runtime.)

Protocol self-test (loopback: clean send + hard-disconnect + auto-resume, hash-verified):

```
FileShare.exe --selftest
```

## Android

Build:

```
cd D:\FileShare\FileShare.Android
.\gradlew :app:assembleDebug
```

APK: `app\build\outputs\apk\debug\app-debug.apk` (8.4 MB)

Install on a phone: enable "Install unknown apps" for your file manager/browser, copy the APK over, or with USB debugging on: `adb install app\build\outputs\apk\debug\app-debug.apk`

## Using it

1. Both devices on the same WiFi (router or phone hotspot). Windows firewall prompt on first run → allow on **private networks**.
2. Each app **auto-detects** the other on every network interface (WiFi, Ethernet, VPN, phone-hotspot subnets — UDP broadcast discovery, port 53515). Blocked? Use **Connect by IP**.
3. Select a device, pick files, send. Received files: Windows → `Documents\FileShare` (configurable); Android → `Downloads/FileShare` via MediaStore.
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
- [ ] M5 on-device soak test (needs a real phone on the same WiFi; `adb devices` currently shows none connected)
