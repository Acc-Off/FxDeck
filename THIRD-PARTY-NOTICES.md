# Third-Party Notices

FxDeck is distributed under the MIT License. It bundles or uses the third-party works listed below. This file is also shown on the About page of the admin UI.

## Icons

### Material Design Icons (Pictogrammers)

- Bundled: `@mdi/font` (web font and CSS)
- Author: Pictogrammers — https://pictogrammers.com/library/mdi/
- License: Pictogrammers Free License (font: Apache License 2.0, code: MIT)
  https://github.com/Templarian/MaterialDesign-Webfont/blob/master/LICENSE

### Font Awesome Free

- Bundled: `@fortawesome/fontawesome-free` (web fonts and CSS)
- Author: Fonticons, Inc. — https://fontawesome.com
- License:
  - Icons: CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/
  - Fonts: SIL OFL 1.1 — https://scripts.sil.org/OFL
  - Code: MIT — https://opensource.org/licenses/MIT
  https://fontawesome.com/license/free
- Attribution: Font Awesome Free by Fonticons, Inc. is licensed under CC BY 4.0. No changes were made.

### Unicode emoji

Emoji are rendered with the fonts installed on the device; no emoji font is bundled.

### Icon search metadata

The icon picker in the admin UI ships a search index generated from the metadata (names, aliases, tags, labels) of the following packages.

| Package | License | URL |
|---|---|---|
| `@mdi/svg` (only `meta.json` is used) | Apache License 2.0 | https://github.com/Templarian/MaterialDesign-SVG |
| `@fortawesome/fontawesome-free` (`metadata/icon-families.json`) | Same as Font Awesome Free above | https://fontawesome.com |
| `emojibase-data` (English and Japanese compact data) | MIT | https://github.com/milesj/emojibase |

## Frontend (npm)

| Package | License | URL |
|---|---|---|
| React / React DOM | MIT | https://github.com/facebook/react |
| Zustand | MIT | https://github.com/pmndrs/zustand |

Build tools (Vite, TypeScript, etc.) are not part of the distributed application.

## Backend (NuGet)

| Package | License | URL |
|---|---|---|
| .NET runtime / ASP.NET Core | MIT | https://github.com/dotnet/runtime |
| QRCoder | MIT | https://github.com/codebude/QRCoder |
| CloudflaredKit | MIT | https://github.com/hsakoh/CloudflaredKit |

## Downloaded at runtime

| Program | License | URL | Notes |
|---|---|---|---|
| cloudflared (Cloudflare Tunnel client) | Apache License 2.0 | https://github.com/cloudflare/cloudflared | Not included in the distribution. Downloaded from GitHub Releases into `%LOCALAPPDATA%\FxDeck\cloudflared\` the first time a tunnel is started. Use of Cloudflare Tunnel is subject to Cloudflare's terms of service. |

## Prior art

| Project | License | URL | Notes |
|---|---|---|---|
| fxcommands | MIT | https://github.com/josh-tf/fxcommands | The FiveM console socket protocol and the command macro notation were studied from this project. No code was copied. |
