# Brim

A Dynamic Island-style notch for Windows. Brim sits at the top of your screen, shows the time and what's going on, and opens into a panel of widgets when you point at it. It is free, has no account and no paywall, and every feature is included.

Built natively with .NET and WPF, so it is light on memory and CPU compared with web-based notch apps.

## Features

**The notch**
- Notch, floating pill or full-width bar; on the top, left or right edge; adjustable size, length and thickness
- Tucks into a slim handle while an app is in front, and opens when you point at it
- Live activity in the small notch: album art and music bars, focus timer, screen recording, AI agent status, downloads
- Pop-ups for volume, brightness, charging and low battery
- Hides itself during full-screen games, video and slideshows
- 16 animated backgrounds (several react to your music), four materials, accent and notch colours

**Tabs**
- **Home**: now playing, focus timer, CPU/RAM, network, camera/microphone in use, shortcuts, quick-launch bar
- **Now playing**: seek bar, output-device switching, synced lyrics, full-screen listening mode
- **Controls**: mute, night light, focus, brightness and volume
- **Volume mixer**: per-app volume and mute
- **Bluetooth**: radio on/off, paired devices with battery levels, connect/disconnect
- **Shelf**: drop files on the notch, drag them back out, copy them together, send them to and from your phone with a QR code
- **Clipboard**: history with search, filters, pinned snippets, images, and quick actions for links, colours and email
- **Focus timer** with a task list, and a **Stopwatch**
- **Weather**: current conditions, next 24 hours and 7 days
- **Teleprompter** that can stay invisible to screen sharing
- **Capture**: screenshots (full screen or area) and screen recording with system audio and microphone
- **System stats** and one-click memory trimming

**Extras**
- **Privacy Blur**: Ctrl+Alt+H blurs the whole screen instantly
- **Ring Light**: turns the screen border into a soft light for video calls
- **AI agents (Sidekick)**: alerts when Claude Code, Codex, Cursor or Antigravity finishes or needs you; approve or deny their permission requests from the notch; plan-usage view; a pin-on-top desktop panel

## Requirements

- Windows 10 (version 2004) or Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a release build, or the .NET 10 SDK to build from source

## Build and run

```powershell
git clone <this repository>
cd Brim
dotnet publish Brim/Brim.csproj -c Release -o publish
.\publish\Brim.exe
```

`brim-hook.exe` (the small relay that AI agents call) is built and copied next to `Brim.exe` automatically.

## Privacy

Brim has no account, no telemetry and no analytics. Its settings live in `%APPDATA%\Brim` on your PC. It only talks to the network for these features:

| Feature | Service | What is sent |
| --- | --- | --- |
| Weather | [Open-Meteo](https://open-meteo.com) | Your approximate coordinates |
| Automatic location | ipapi.co, falling back to ipwho.is | Your IP address (only if you haven't picked a city) |
| Synced lyrics | [LRCLIB](https://lrclib.net) | Song title and artist |
| Claude plan limits (off by default) | Anthropic | Your Claude Code sign-in token, read from `~/.claude` |

- **AI agent hooks** only use a loopback port (`127.0.0.1`), so nothing leaves your PC. Connecting an agent edits that agent's own config file (`~/.claude/settings.json`, `~/.codex/config.toml`, `~/.cursor/hooks.json` or `~/.gemini/config/hooks.json`), keeps a `.brim-backup` copy, and can be undone from Settings.
- **Phone transfer** opens a port on your local network only while it is switched on. Each session uses a random secret in the address and stops after 30 idle minutes.

## Good to know

- *Open at sign-in* is on by default and points at wherever `Brim.exe` is when it first runs.
- Cursor has reported issues with honouring hook "allow"/"ask" answers; "deny" is reliable.
- *Claude plan limits* uses an endpoint Anthropic hasn't documented, so it may stop working.
- *Frosted glass* hides the notch from screenshots and screen sharing while it is selected.
- Night light and Bluetooth connect use Windows internals that may change between Windows versions. Brim falls back to opening Windows Settings if they do.

## Developer self-tests

`Brim.exe --snapshot <folder>` renders every screen to PNG files. Adding `--hook-test`, `--settings-test` or `--record-test` runs the agent-hook, settings and screen-recording checks instead. Snapshot mode never saves settings.

## License

MIT. See [LICENSE](LICENSE).

Third-party libraries: [NAudio](https://github.com/naudio/NAudio) (MIT), [QRCoder](https://github.com/codebude/QRCoder) (MIT), System.Management (MIT). Weather data by Open-Meteo (CC BY 4.0). Icons are Windows' built-in Segoe Fluent Icons.

Brim is an independent project. It is not affiliated with or endorsed by Apple, Microsoft, Anthropic, OpenAI, Anysphere (Cursor) or Google. Product names are the property of their owners.
