# Dupin

**Dupin** transforms the top center of your Windows display into an interactive, fluid **Dynamic Island & AI Companion**. Resting quietly as an expressive mascot pill when idle, Dupin expands on hover or `Win + Ctrl + N` into a unified command hub—featuring real-time Windows media controls with synced lyrics, a multi-provider AI assistant (**Gemini**, **Claude**, and **OpenAI**), a drag-and-drop file shelf, a Pomodoro focus timer, daily task & hydration tracking, and smart system toast alerts.

---

## ✨ Features

- **🎭 Reactive Mascot & Dynamic States**: Blinks, dozes, and reacts to system events (charger plug-in, Caps Lock changes, low battery, and incoming notifications) before collapsing into an ultra-minimal pill when idle.
- **🎵 Live Media & Real-Time Synced Lyrics**: Connects directly to Windows Global System Media Transport Controls (Spotify, Apple Music, YouTube, browser audio) with album art, transport controls, and auto-scrolling time-synced lyrics.
- **🤖 Multi-Model AI Assistant**: Stream instant answers with live reasoning/thought indicators using **Gemini 3.8 Flash**, **Claude 3.5 Sonnet**, or **OpenAI GPT-4o**—or jump straight into your installed desktop AI apps with one click.
- **📂 Drag-and-Drop File Shelf**: Drag files onto the top notch to temporarily park them, then drag them out later into emails, chats, or folders.
- **⏱️ Focus Timer, Tasks & Hydration**: Built-in Pomodoro focus timer, persistent daily to-do checklist, and an hourly water intake tracker with gentle reminders.
- **🔔 Smart Toast Alert Hub**: Captures incoming Windows notifications into a clean, copyable alert feed with animated banner previews.
- **⚡ Native & Lightweight**: Global hotkey toggle (`Win + Ctrl + N`), automatic fullscreen suppression during games/videos, multi-monitor awareness, and optional launch at Windows startup.

---

## 🚀 Building & Running

### Prerequisites
- Windows 10 (19041+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build & Run (Development)
```powershell
dotnet build WindowsNotch.csproj
dotnet run --project WindowsNotch.csproj
```

### Publish Standalone Production Release
```powershell
dotnet publish WindowsNotch.csproj -c Release -r win-x64 --self-contained true -o publish
.\publish\Dupin.exe
```

---

## ⌨️ Shortcuts & Controls
- **Expand / Collapse**: Hover over the top notch or press **`Win + Ctrl + N`**
- **System Tray**: Right-click the Dupin tray icon to toggle visibility, enable **Launch Dupin at Windows Startup**, or exit.
- **Data Storage**: Settings, API keys, tasks, hydration logs, and parked shelf items are persisted locally in `%APPDATA%\Dupin`.
