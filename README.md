# JTS Project

Personal productivity desktop app for Windows. Plan customers, projects and tasks, run
focus sessions, capture work by voice, and chat with an AI agent over your own data —
all backed by Microsoft Dataverse.

Built with **.NET 10**, **WinUI 3** (Windows App SDK), and an MVVM architecture
(CommunityToolkit.Mvvm). An optional **.NET MAUI** Android companion shares the same
Dataverse backend.

## Solution layout

| Project | Target | Responsibility |
|---|---|---|
| `JTS.Core` | `net10.0` | Shared enums and domain primitives. |
| `JTS.Data` | `net10.0` | Dataverse entity models (Customer, Project, TaskItem, schedule blocks, journal, pomodoro…) and local app paths. |
| `JTS.AI` | `net10.0` | `DeepSeekClient` (chat completions) and `WhisperTranscriber` (Whisper.net + NAudio). |
| `JTS.App` | `net10.0-windows10.0.26100.0` | The WinUI 3 desktop app: pages, view models and services. |
| `JTS.Mobile` | `net10.0-android` | .NET MAUI Android companion (same Dataverse backend). |

## Features

- **Agent** — chat assistant (DeepSeek) that answers over a live snapshot of your
  customers, projects and tasks, and can create or update records.
- **Dashboard** — open tasks, today/overdue work, pomodoro summary and recent journal
  highlights.
- **Tasks** — Kanban board with the status flow *Assigned → Ongoing → Testing → Tested →
  UAT → Production*, priorities, work types, due dates, per-task time tracking, calendar
  assignments and a comment/journal feed.
- **Planner** — weekly planner to schedule task blocks across the week.
- **Focus** — Pomodoro timer with task selection, work/break modes, interruptions,
  session persistence and a journal prompt on completion.
- **Customers & Projects** — CRUD for customers and hierarchical projects (subprojects,
  related-project links, per-project color), reachable from Settings.
- **Quick Capture** — global hotkey `Ctrl+Alt+T` opens a capture window to record audio,
  transcribe, or paste text and turn it into tasks.
- **Timesheet export** — push tracked time and comments to Dataverse timesheet lines.
- **Voice** — Whisper.net and Vosk (offline) transcription plus Windows speech dictation.

## Data & secrets

- All persistent data lives in **Microsoft Dataverse** (Power Platform). There is no local
  database.
- Credentials are **never stored in source or config files**. The desktop app keeps them
  in the **Windows Credential Manager** (`AppSettingsService`); the Android app uses
  `SecureStorage`. This includes the Dataverse client secret and the DeepSeek/OpenAI API
  keys.

## Setup

1. Build and run the app (see below).
2. Open **Settings** and fill in:
   - Dataverse: Tenant ID, Client ID, Client Secret, Environment URL.
   - DeepSeek API key (for the Agent and AI summaries).
3. Optional: configure a Whisper or Vosk model path for local speech-to-text.
4. Create at least one Customer and Project before adding tasks or using voice capture.

## Build & run

```powershell
# Build the whole solution
dotnet build .\JTS.slnx

# Run the desktop app (packaged identity is required for WinUI features)
dotnet run --project .\src\JTS.App\JTS.App.csproj
```

Once installed, you can also launch it from the Start menu (search `JTS`). Do not run
`JTS.App.exe` directly from `bin` — packaged WinUI apps need their registered app identity.

## Requirements

- Windows 11 (target SDK `10.0.26100`).
- .NET 10 SDK.
- A Microsoft Dataverse environment with the JTS tables provisioned.
- A DeepSeek API key for AI features.
