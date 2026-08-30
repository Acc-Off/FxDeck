# FxDeck

A Stream Deck Mobile–style command deck for FiveM / RedM: a single Windows executable (system tray + local HTTP server) that serves a grid of buttons to a phone's browser and sends the commands to the game's console.

- Name: **FxDeck**. License: MIT.
- User-facing documentation is [README.md](README.md) (English, canonical) and [README.ja.md](README.ja.md). Update both together.

## Source of truth

The design is settled in `Docs/`. Implement what the documents say; if you need to deviate, **fix the document first**, then write the code.

- [Docs/DesignNote.ja.md](Docs/DesignNote.ja.md) — architecture, components, protocol, authentication, networking, data model, distribution, decisions
- [Docs/UIUX.ja.md](Docs/UIUX.ja.md) — screens, deck / admin UI look and behaviour, main flows, error states
- [Docs/DevelopmentNote.ja.md](Docs/DevelopmentNote.ja.md) — build / verification / release steps, how to add UI strings, **past pitfalls**, known issues. Read it before starting work

The design documents are in Japanese for now.

## Stack

- .NET 10 (TFM `net10.0-windows`), ASP.NET Core Minimal API + Kestrel, WinForms `NotifyIcon` (tray), CloudflaredKit (tunnel)
- Front end: Vite + React + TypeScript + Zustand, no UI library. One SPA routed under `/admin/*` and `/deck/*`; the build output is embedded into the exe as EmbeddedResource
- Distribution: `PublishSingleFile` + `SelfContained` (no Trimmed / AOT)

## Layout

```
src/FxDeck/            The app (WinExe)
  FxConsole/           Console socket: protocol, frame parser, TcpFxConsoleClient
  Commands/            CommandMacroParser (; ;; {NNNms}) and MacroExecutor (FIFO)
  Config/              AppConfig (config.json), ConfigStore (hot reload), DeckTokenStore, ConfigValidator, ConfigPackage (.fxdeck), AssetStore (user images)
  Localization/        Strings (one Strings.xx.cs per language), Localizer
  Web/                 FxDeckHost (Kestrel with two listeners, DI), DeckEndpoints, AdminEndpoints, DeckHub (WebSocket), DeckAuth, EmbeddedWebRoot, LanAddress, QrRenderer
  Services/            FirewallService, AutoStartService, AppLifecycle, TunnelService
  Tray/                TrayApplicationContext, TrayIcons, SingleInstance, ConsoleAttach
  Logging/             FileLoggerProvider
src/FxDeck.Emulator/   Emulator of the FiveM console socket (independent implementation; shares no codec with the app)
src/FxDeck.Web/        Front end
  src/shared/          types, api, i18n, locales/ja.ts (canonical keys) and en.ts
  src/deck/            Deck screen
  src/admin/           Admin UI
  scripts/             gen-icons.mjs (PWA icons), gen-icon-index.mjs (icon search index → src/generated/, not in git)
tests/FxDeck.Tests/    xUnit
Docs/                  DesignNote.ja.md, UIUX.ja.md, DevelopmentNote.ja.md, images/ (README screenshots)
THIRD-PARTY-NOTICES.md Third-party licenses (embedded; shown on the About page)
```

## Working rules

- Build / test: `dotnet build FxDeck.slnx` → `dotnet test FxDeck.slnx --no-build`. C# only: `-p:SkipWebBuild=true`. Replacing the exe fails while the app or the emulator is running.
- Verify behaviour against the emulator (`dotnet run --project src/FxDeck.Emulator`) and **always with `--data-dir` pointing at a scratch directory**. Never touch the real configuration in `%LOCALAPPDATA%\FxDeck`.
- Do not fix UI by guessing: **render it in headless Chrome and look** (steps in DevelopmentNote §2; headless Chrome has a 500 px minimum width, so measure mobile widths through the DevTools protocol).
- If you start a tunnel for testing, stop it when you are done — it is a public URL.
- Inline scripts passed through the Bash tool lose `\\`. Write scripts that contain backslashes to a file with the Write tool, then run the file.

## Implementation notes

- The FiveM console socket (TCP 127.0.0.1:29200, `PPCR` / `CMND` / `PRNT`) is an **undocumented protocol**. Keep it contained in `FxConsoleClient`; test against the emulator, not the real game.
- The admin API is loopback-only; the deck API requires the token. Never blur that boundary.
- Never pass browser-supplied strings to elevated commands (`netsh` via `runas`).
- Icon sets: MDI + Font Awesome Free + Unicode emoji. No SVG upload. Third-party licenses go in `THIRD-PARTY-NOTICES.md`.

## Language

- Documentation is English by default, with Japanese versions as `.ja.md` alongside (README follows this; the design documents in `Docs/` are Japanese-only for now). Commit messages, identifiers, comments and logs are English. **No Japanese literals in executable code** — the only exceptions are the dictionaries (`Strings.ja.cs`, `locales/ja.ts`) and test data that exercises multi-byte strings.
- UI strings: Japanese is canonical, English is kept in step. **Never hard-code UI text** — add it to the dictionaries (how, and how to add a language: DevelopmentNote §4).
