# FxDeck

**A Stream Deck–style command deck for FiveM / RedM, running in your phone's browser.**

FxDeck is a single Windows executable that sits in the system tray, connects to the console socket of the FiveM / RedM client running on the same PC, and serves a grid of buttons to your phone over your home Wi‑Fi. Tap a button, and the command it holds (`e wave`, `say hello`, a chain with delays, …) is sent to the game console. No Stream Deck hardware, no subscription, no app store.

[日本語版 README はこちら](README.ja.md)

<p align="center">
  <img src="Docs/images/deck-landscape.png" alt="The deck on a phone (landscape)" width="720">
</p>

## Features

- **Phone as a deck** — a fixed grid of large, square keys (3×2 / 5×3 / 8×4 / custom), landscape or portrait, swipe between profiles (pages). Works as a PWA: add it to the home screen and it opens full screen.
- **Icons** — Material Design Icons, Font Awesome Free, Unicode emoji, or your own images (PNG / JPEG / WebP / GIF, resized to 256×256).
- **Command macros** — `cmd1; cmd2` chains, `{500ms}` delays, `;;` as a 500 ms shorthand (compatible with fxcommands notation). "Hold to run" for dangerous keys such as `quit`.
- **Press / release and stages** — a key can send one command when pressed and another when released (hold to `e sit`, let go to `e c`), or cycle through up to five stages with their own icon and command (a sit / stand toggle).
- **Live console** — the game's console output streams to a drawer on the phone.
- **Edit on the PC, see it on the phone** — the admin UI runs in your desktop browser; every change is saved automatically and pushed to connected phones immediately. "Test send" sends a command while you edit.
- **Command suggestions from your server** — while in‑game, one click reads the permission‑filtered command list your server's chat already knows; the button editor then completes command names as you type and offers a searchable command picker.
- **Connect with a QR code** — no pairing, no app. The QR code carries an access token; the deck is token‑protected, the admin UI is reachable from `localhost` only.
- **Works when Wi‑Fi doesn't** — optional Cloudflare Tunnel (TryCloudflare with no account, or a fixed URL with your own Zero Trust tunnel) for phones on a different network.
- **Import / export** — profiles or the whole configuration as `.fxdeck` files, images included.
- **Dark / light theme, Japanese / English UI**, start with Windows, Windows Firewall helper.

<p align="center">
  <img src="Docs/images/admin-profiles.png" alt="Admin UI: editing a profile" width="720">
</p>

<p align="center">
  <img src="Docs/images/deck-console.png" alt="Console drawer on the deck" width="440">
  <img src="Docs/images/deck-portrait.png" alt="The deck in portrait" width="203">
</p>

## Requirements

- Windows 10 / 11 (x64). The recommended download is self‑contained; no .NET installation is needed.
- The FiveM or RedM client on the same PC (FxDeck talks to the client's console socket at `127.0.0.1:29200`).
- A phone (or tablet) with a modern browser — Safari on iOS, Chrome on Android — on the same network as the PC, or a Cloudflare Tunnel (see below).

## Getting started

1. Download `FxDeck-<version>-win-x64.exe` from [Releases](https://github.com/Acc-Off/FxDeck/releases) and run it. It is not code‑signed, so Windows SmartScreen may ask once ("More info" → "Run anyway"). FxDeck lives in the system tray; the admin UI opens in your browser on first run (later: double‑click the tray icon).
   - `…-slim.exe` is a much smaller build (about 3 MB instead of 60 MB) that needs the .NET 10 **Desktop Runtime** and **ASP.NET Core Runtime** (x64) from https://dotnet.microsoft.com/download/dotnet/10.0. Pick it only if you already have them.
2. When Windows Firewall asks whether to allow FxDeck, click **Allow**. If you cancelled it, use the **Allow** button on the Connect page instead (it runs `netsh` with a UAC prompt).
3. Start FiveM / RedM. The status in the admin UI turns to "FiveM connected".
4. On the Connect page, scan the **From the same network** QR code with your phone. The deck opens; add it to your home screen for a full‑screen app.
5. Go to **Profiles**, click a key in the preview and give it a title, an icon, a colour and a command. It appears on the phone as you type.

### Command syntax

| Notation | Meaning |
|---|---|
| `e wave` | One console command |
| `e think; {2000ms}; e c` | Run in sequence, waiting 2 s in between (`;` or a line break separates commands) |
| `a ;; b` | `;;` waits 500 ms |
| `{ 1500 ms }` | Delays are case‑insensitive and tolerate spaces; capped at 60 s |

Commands are sent to the game one frame at a time with a small gap, so a long chain behaves like typing them into the console.

### Hold keys and stages

- **On release** — fill in the optional "On release" field and the key becomes a hold key: the command is sent the moment you touch it and the release command when you lift your finger (or when the phone loses the connection, so nothing stays stuck). Example: `e sit` on press, `e c` on release.
- **Stages** — add stages in the editor (up to 5). Each stage has its own title, icon, colour and commands; every press moves to the next one and the key shows a row of dots for where it is. The current stage is kept by the PC, so every phone sees the same thing; it resets when FxDeck restarts or when a press fails.

### When the phone can't open the deck

- Both devices must be on the **same network**. Mobile data, guest Wi‑Fi and access‑point isolation all get in the way.
- Check **Windows Firewall** on the Connect page ("If the phone can't open it").
- If a multi‑homed PC shows the wrong address in the QR code, choose the adapter under **Settings → Deck**.
- Otherwise use **From another network** on the Connect page: FxDeck starts a Cloudflare Tunnel (downloads `cloudflared` on first use, about 55 MB) and shows a second QR code with a public `https://…trycloudflare.com` URL. The URL changes on every start. Stop the tunnel when you are done.
- For a stable URL, create a tunnel in Cloudflare Zero Trust, point its public hostname at `http://127.0.0.1:<deck port>` (20200 by default) and enter the tunnel token and the public URL under **Settings → Tunnel**. If the hostname points at a different port Cloudflare answers 502.

### Security notes

- The QR code and the deck URL contain the access token. Anyone who has them can send console commands to your game. Don't share screenshots of them; **Settings → Security → Reissue the deck token** invalidates every phone at once.
- The admin UI listens on `127.0.0.1` only and has no authentication; it is meant for the person sitting at the PC.
- Traffic on the LAN is plain HTTP (self‑signed HTTPS is unusable on phones). It is intended for a home network. Cloudflare Tunnel traffic is HTTPS.

### Command suggestions from the server

- While you are on a server, click **Settings → Input assist → Extract commands from the connected server**. FxDeck reads the suggestion list the chat UI already holds — the same permission‑filtered (ACE) list you see when typing `/` in chat — and caches it in `commands-cache.json`.
- The button editor then suggests command names as you type, with help texts and argument hints (`jail <id> [time]`); it also works in the middle of macro chains. The **Command list** button next to the command fields opens a searchable picker. Keybind halves (`+x` / `-x`) and internal commands are hidden there by default behind a toggle.
- **How it reads them**: the FiveM client exposes a loopback‑only NUI debugging port (`127.0.0.1:13172`). FxDeck connects to it **only when you click the button**, passively reads the chat resource's in‑memory state, and sends nothing to the game or the server — there is no background polling. It is an undocumented surface, so a game update may break it, and servers that replace the standard chat resource cannot be read; the suggestions are a convenience, and typing commands manually always works. Extraction only works in‑game (not on the main menu).

### Where things are stored

`%LOCALAPPDATA%\FxDeck` (tray menu → **Open data folder**):

| Path | Contents |
|---|---|
| `config.json` | Settings and profiles. Editing it by hand is fine; changes are picked up live |
| `deck-token` | The access token |
| `commands-cache.json` | Command list extracted for the input assist (safe to delete; re‑extract any time) |
| `assets\` | Uploaded key images (`<sha256>.png`) |
| `logs\fxdeck.log` | Log (1 MB rotation) |
| `cloudflared\` | `cloudflared.exe`, downloaded when a tunnel is first started |

### Command line

```
FxDeck [options]

  --console            Print the deck URL, QR code and log to the calling terminal
  --host <ip>          Game console host (default: config.json, initially 127.0.0.1)
  --port <port>        Game console port (initially 29200)
  --deck-port <port>   Deck UI port (initially 20200)
  --admin-port <port>  Admin UI port (initially automatic)
  --data-dir <dir>     Data directory (default %LOCALAPPDATA%\FxDeck; FXDECK_DATA_DIR works too)
  --send "<macro>"     Send one macro without starting the web server, then exit
  --timeout <ms>       How long --send waits for the game (default 10000)
  -v                   Verbose log
```

`FxDeck.exe --send "e wave"` is handy for scripts and hotkeys.

## Building from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Node.js 22 or later.

```
git clone https://github.com/Acc-Off/FxDeck.git
cd FxDeck
dotnet build FxDeck.slnx        # also runs npm ci && npm run build and embeds the SPA
dotnet test FxDeck.slnx --no-build
dotnet publish src/FxDeck -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Add `--self-contained false` (and drop the compression flag) for the slim (framework‑dependent) build. Releases are built by [.github/workflows/release.yml](.github/workflows/release.yml) when a `v*` tag is pushed.

For development without the game, `dotnet run --project src/FxDeck.Emulator` emulates the FiveM console socket on port 29200, and `dotnet run --project src/FxDeck -- --console --data-dir <temp>` runs FxDeck against it with a scratch data directory. Front‑end hot reload: `cd src/FxDeck.Web && npm run dev`.

```
src/FxDeck/            Windows tray app + ASP.NET Core (Minimal API, Kestrel, WebSocket)
src/FxDeck.Web/        Vite + React + TypeScript SPA (deck and admin UI), embedded into the exe
src/FxDeck.Emulator/   Console-socket emulator for development and tests
tests/FxDeck.Tests/    xUnit tests
Docs/                  Design documents (Japanese)
```

Design documents live in [Docs/](Docs/) (Japanese only for now): [DesignNote.ja.md](Docs/DesignNote.ja.md) (architecture, protocol, data model) and [UIUX.ja.md](Docs/UIUX.ja.md) (screens and flows). Maintainer notes are in [DevelopmentNote.ja.md](Docs/DevelopmentNote.ja.md).

## How it works

FiveM and RedM clients expose a console socket on `127.0.0.1:29200`. FxDeck keeps a connection to it, sends commands as `CMND` frames and relays `PRNT` frames (console output) to the phone. The protocol is **undocumented** and was studied from [fxcommands](https://github.com/josh-tf/fxcommands); it may change with a game update. The web side is a Kestrel server with two listeners — the admin API on loopback only and the deck on all interfaces behind a token — and a single React SPA. The command suggestions use a second undocumented surface: an on‑demand, passive read of the chat NUI's state through the client's CEF debugging port (`127.0.0.1:13172`).

## Disclaimer

FxDeck is an independent project. It is not affiliated with or endorsed by Cfx.re / Rockstar Games (FiveM, RedM) or Elgato (Stream Deck). Use it in accordance with the rules of the servers you play on.

## License

[MIT](LICENSE). Third‑party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and on the About page of the admin UI. Font Awesome Free icons are used under CC BY 4.0.
