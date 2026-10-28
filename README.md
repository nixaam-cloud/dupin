# Dupin

**Dupin** transforms the top center of your Windows display into an interactive, fluid **Dynamic Island & AI Companion**. Resting quietly as an expressive mascot pill when idle, Dupin expands on hover or `Win + Ctrl + N` into a unified command hub—featuring real-time Windows media controls with synced lyrics, a multimodal AI assistant (**Gemini**, **Claude**, and **OpenAI**) with **AI Screen Snip**, an interactive drag-and-drop file shelf, live weather with animated mascot eye effects, a hardware HUD interceptor, a Pomodoro focus timer, daily task & hydration tracking, and persistent system toast alerts.

---

## 📦 Download Standalone `.exe`

Download the latest self-contained single-file **`Dupin.exe`** directly from [**GitHub Releases**](https://github.com/nixaam-cloud/dupin/releases/latest) — no installation or separate runtime required.

---

## ✨ Features

- **🎭 Expressive Mascot & Weather Eye Animations**: Blinks, dozes, and displays live animated **☀️ Sun** (golden aura & rotating rays), **☁️ Cloud** (drifting clouds), and **🌧️ Rain** (storm cloud & falling raindrops) effects directly on the mascot eyes based on your local weather or manual selection.
- **🔊 Hardware HUD Interceptor**: Hooks into Windows Shell hooks, media keys, CoreAudio endpoints, and monitor brightness to display fluid pill HUDs for **Volume**, **Mute**, **Microphone Mute**, and **Brightness** changes.
- **✂️ AI Screen Snip & Multimodal Vision**: Drag a marquee over any screen region across any monitor to feed a cropped bitmap directly into **Gemini 3.8 Flash**, **Claude 3.5 Sonnet**, or **OpenAI GPT-4o** multimodal vision endpoints, with live reasoning/thought indicators.
- **📂 Interactive File Shelf**: Drag files onto the top notch to park them, drag them out into chats/emails, or right-click (or use inline quick buttons) to **Copy File Content**, **Compress to Zip**, or **Send to AI Vision/Analysis**.
- **🎵 Live Media & Real-Time Synced Lyrics**: Connects directly to Windows Global System Media Transport Controls (Spotify, Apple Music, YouTube, browser audio) with album art, transport controls, and auto-scrolling time-synced lyrics.
- **🌤️ Live Weather Module**: Automatic IP geolocation and city search powered by Open-Meteo with live temperature, humidity, wind speed, and interactive mascot eye effect preview.
- **⏱️ Focus Timer, Tasks & Hydration**: Built-in Pomodoro focus timer, persistent daily to-do checklist, and an hourly water intake tracker with toggleable reminders.
- **🔔 Persistent Toast Alert Hub**: Captures incoming Windows notifications directly from the Windows Notification database into a persistent, copyable alert feed until cleared.
- **🖥️ Multi-Monitor Cursor Tracking & Tab-Drag Immunity**: Dynamically anchors to whichever display currently holds your cursor (`MonitorFromPoint`) with per-monitor DPI scaling, suppresses during fullscreen games/videos, and ignores accidental hover while dragging browser tabs or windows near the top edge.

---

## 🚀 Building & Running from Source

### Prerequisites
- Windows 10 (19041+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build & Run (Development)
```powershell
dotnet build WindowsNotch.csproj
dotnet run --project WindowsNotch.csproj
```

### Publish Standalone Single-File Release
```powershell
dotnet publish WindowsNotch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release_single
.\release_single\Dupin.exe
```

---

## ⌨️ Shortcuts & Controls
- **Expand / Collapse**: Hover over the top notch or press **`Win + Ctrl + N`**
- **System Tray**: Right-click the Dupin tray icon to toggle visibility, enable **Launch Dupin at Windows Startup**, or exit.
- **Data Storage**: Settings, API keys, weather preferences, tasks, hydration logs, notification history, and parked shelf items are persisted locally in `%APPDATA%\Dupin`.
