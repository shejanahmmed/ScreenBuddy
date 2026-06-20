<div align="center">

<br/>

```
███████╗ ██████╗██████╗ ███████╗███████╗███╗   ██╗██████╗ ██╗   ██╗██████╗ ██████╗ ██╗   ██╗
██╔════╝██╔════╝██╔══██╗██╔════╝██╔════╝████╗  ██║██╔══██╗██║   ██║██╔══██╗██╔══██╗╚██╗ ██╔╝
███████╗██║     ██████╔╝█████╗  █████╗  ██╔██╗ ██║██████╔╝██║   ██║██║  ██║██║  ██║ ╚████╔╝ 
╚════██║██║     ██╔══██╗██╔══╝  ██╔══╝  ██║╚██╗██║██╔══██╗██║   ██║██║  ██║██║  ██║  ╚██╔╝  
███████║╚██████╗██║  ██║███████╗███████╗██║ ╚████║██████╔╝╚██████╔╝██████╔╝██████╔╝   ██║   
╚══════╝ ╚═════╝╚═╝  ╚═╝╚══════╝╚══════╝╚═╝  ╚═══╝╚═════╝  ╚═════╝ ╚═════╝ ╚═════╝    ╚═╝   
```

**Real-time PC screen streaming to Android — zero cloud, zero latency overhead, total control.**

<br/>

[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Android-0A0A0A?style=for-the-badge&logo=windows&logoColor=white)](.)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Android](https://img.shields.io/badge/Android-API%2024%2B-3DDC84?style=for-the-badge&logo=android&logoColor=white)](https://developer.android.com)
[![Kotlin](https://img.shields.io/badge/Kotlin-Jetpack%20Compose-7F52FF?style=for-the-badge&logo=kotlin&logoColor=white)](https://kotlinlang.org)
[![License](https://img.shields.io/badge/License-MIT-E11D48?style=for-the-badge)](LICENSE)

<br/>

</div>

---

## What is ScreenBuddy?

ScreenBuddy is a **high-performance, peer-to-peer screen mirroring system** that streams your Windows desktop to an Android device over your local network — with hardware-accelerated H.264 encoding, sub-second latency, full touch input control, and zero reliance on any external server, cloud service, or third-party relay.

It is not a remote desktop tool wrapped in a convenience layer. It is built from the ground up using low-level OS primitives: **DXGI Desktop Duplication** for frame capture, **Media Foundation** for GPU-accelerated encoding, and **Android MediaCodec** for hardware decoding — delivering a streaming pipeline that operates as close to the metal as possible.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  WINDOWS HOST                                                               │
│                                                                             │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐                  │
│  │ DXGI Desktop │───▶│  Media Fdn.  │───▶│  TCP Stream  │─────────────┐   │
│  │  Duplication │    │ H.264 Encode │    │    Server    │             │   │
│  └──────────────┘    └──────────────┘    └──────────────┘             │   │
│                                                                        │   │
│  ┌──────────────────────────────┐                                      │   │
│  │  UDP Broadcast Auto-Discovery│  ◀── announces presence on LAN       │   │
│  └──────────────────────────────┘                                      │   │
│                                                                        │   │
│  ┌──────────────────────────────┐                                      │   │
│  │  Win32 SendInput Simulator   │  ◀── receives touch events over TCP  │   │
│  └──────────────────────────────┘                                      │   │
└────────────────────────────────────────────────────────────────────────┼───┘
                                      Local Network (Wi-Fi)              │
                                                                         ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  ANDROID CLIENT                                                             │
│                                                                             │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐                  │
│  │  TCP Stream  │───▶│  MediaCodec  │───▶│  SurfaceView │                  │
│  │   Receiver   │    │ H.264 Decode │    │   Renderer   │                  │
│  └──────────────┘    └──────────────┘    └──────────────┘                  │
│                                                                             │
│  ┌──────────────────────────────┐                                           │
│  │  UDP Discovery Client        │  ──▶  finds PC automatically on LAN      │
│  └──────────────────────────────┘                                           │
│                                                                             │
│  ┌──────────────────────────────┐                                           │
│  │  Touch Input Sender          │  ──▶  forwards touch events to PC         │
│  └──────────────────────────────┘                                           │
└─────────────────────────────────────────────────────────────────────────────┘
```

**Wire Protocol** — All communication runs over a single TCP connection using a lightweight custom framing format:

```
┌──────────────────┬──────────────────┬───────────────────────┐
│  Type (4 bytes)  │  Length (4 bytes) │  Payload (N bytes)    │
│  Big Endian u32  │  Big Endian u32   │  H.264 NAL / Events   │
└──────────────────┴──────────────────┴───────────────────────┘
```

---

## Key Features

| Feature | Implementation |
|---|---|
| **Screen Capture** | DXGI Desktop Duplication API — GPU-side frame access, no GDI overhead |
| **Video Encoding** | Windows Media Foundation H.264 encoder — hardware-accelerated on supported GPUs |
| **Video Decoding** | Android MediaCodec H.264 decoder — renders directly to `SurfaceView`, zero copy |
| **Auto-Discovery** | UDP broadcast on port `7891` — no manual IP entry required |
| **Security** | 6-digit PIN handshake — generated fresh per session, never stored |
| **Touch Control** | Win32 `SendInput` — bi-directional touch event forwarding from Android to PC |
| **Transport** | Raw TCP — minimal framing overhead, operates entirely on LAN |

---

## Project Structure

```
ScreenBuddy/
│
├── windows-app/
│   └── ScreenBuddyCapture/
│       ├── Program.cs            ← Entry point: PIN generation, server bootstrap
│       ├── StreamServer.cs       ← Session coordinator: capture → encode → stream
│       ├── ScreenCapture.cs      ← DXGI Desktop Duplication capture loop
│       ├── H264Encoder.cs        ← Media Foundation H.264 encoding pipeline
│       ├── InputSimulator.cs     ← Win32 SendInput touch event simulation
│       └── StreamClient.cs       ← Per-client TCP handler and protocol framing
│
├── app/
│   └── src/main/java/com/shejan/screenbuddy/
│       ├── MainActivity.kt       ← Jetpack Compose UI, setup flow, AdMob
│       └── H264StreamPlayer.kt   ← TCP client, PIN handshake, MediaCodec decoder
│
└── LICENSE
```

---

## Requirements

### Windows (Server)
- Windows 10 or later (DXGI Desktop Duplication requires Windows 8+)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- x64 architecture
- GPU with Direct3D 11 and H.264 hardware encoding support (Intel Quick Sync, NVIDIA NVENC, AMD VCE)

### Android (Client)
- Android 7.0 Nougat (API 24) or higher
- Device on the same local Wi-Fi network as the Windows host

---

## Getting Started

### 1 — Clone the repository

```bash
git clone https://github.com/shejanahmmed/ScreenBuddy.git
cd ScreenBuddy
```

### 2 — Run the Windows Server

```bash
cd windows-app/ScreenBuddyCapture
dotnet run
```

The server will:
1. Begin broadcasting its presence on your LAN via UDP
2. Display a freshly generated **6-digit PIN** in the terminal
3. Wait for an authenticated Android client connection

### 3 — Build & Install the Android App

```bash
# From the project root
./gradlew assembleDebug
adb install app/build/outputs/apk/debug/app-debug.apk
```

Or open the project in **Android Studio** and run it directly on your device.

### 4 — Connect

1. Launch ScreenBuddy on your Android device
2. The app will auto-discover the PC on your network
3. Enter the 6-digit PIN shown in the server terminal
4. Your PC screen streams directly to the device — touch the display to control your PC

---

## Security Model

ScreenBuddy uses a **session-scoped PIN** as its authentication mechanism. The PIN is:

- Generated cryptographically fresh on every server launch
- Never persisted to disk or transmitted in plaintext before authentication
- Required to complete the TCP handshake — unauthenticated clients are immediately disconnected

This model is intentionally minimal. ScreenBuddy is designed for **private LAN use** — it is not intended to be exposed to the public internet.

---

## Tech Stack

**Windows**
- `.NET 8` — Runtime and build system
- `Vortice.DXGI` / `Vortice.Direct3D11` — Managed wrappers for DXGI Desktop Duplication
- `Vortice.MediaFoundation` — Managed wrapper for Windows Media Foundation H.264 encoding
- `System.Drawing.Common` — Frame handling utilities

**Android**
- `Kotlin` — Primary language
- `Jetpack Compose` — Declarative UI
- `MediaCodec` — Hardware H.264 decoding
- `SurfaceView` — Zero-copy frame rendering
- `Google Play Services Ads` — AdMob integration (setup screen only)

---

## Roadmap

- [ ] Audio streaming (WASAPI capture → Opus encode → Android decode)
- [ ] Multi-monitor support with monitor selection UI
- [ ] Adaptive bitrate control based on network conditions
- [ ] Clipboard synchronization between PC and Android
- [ ] Portrait/landscape auto-rotation support
- [ ] Connection quality indicator on the Android UI

---

## Contributing

Contributions are welcome. Please open an issue before submitting a pull request for any non-trivial change, so the approach can be discussed first.

1. Fork the repository
2. Create a feature branch: `git checkout -b feat/your-feature`
3. Commit your changes: `git commit -m 'feat: describe your change'`
4. Push and open a Pull Request

---

## License

Distributed under the **MIT License**. See [LICENSE](LICENSE) for full terms.

---

<div align="center">

Built with precision by **Shejan Ahmmed**

*No cloud. No relay. Just your screen, your device, your network.*

</div>
