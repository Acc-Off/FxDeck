# 開発メモ

メンテナ向けの実務メモ。設計は [DesignNote.ja.md](./DesignNote.ja.md)、画面は [UIUX.ja.md](./UIUX.ja.md) が正で、ここには「どう作業するか」だけを書く。

## 1. ビルドとテスト

- 必要なもの: .NET 10 SDK（`global.json` 参照）、Node.js 22 以降。
- 全体: `dotnet build FxDeck.slnx` → `dotnet test FxDeck.slnx --no-build`。`src/FxDeck` のビルドが `npm ci && npm run build` を呼び、`src/FxDeck.Web/dist/` を EmbeddedResource として exe に埋め込む（フロントのソースが変わったときだけ）。
- C# だけ確認するなら `dotnet build FxDeck.slnx -p:SkipWebBuild=true`（Node 不要）。
- 本体を起動中だと exe の差し替えに失敗する。トレイの「終了」で止めてからビルドする。
- テストは xUnit（`tests/FxDeck.Tests`）。パーサ・プロトコル・エミュレータ相手の結合・Web API・設定・インポート／エクスポート・ファイアウォール判定・トンネル（cloudflared はフェイク `tests/Fakes/FakeCloudflared.cs`。`FxDeckHostOptions.ConfigureServices` で `ICloudflaredService` / `ICloudflaredDownloader` を差し替える）。

## 2. 動作確認

- ゲーム無し: `dotnet run --project src/FxDeck.Emulator`（29200 で待ち受け、受信コマンドをログに出し `PRNT` を返す。5 秒のアイドル切断も再現）→ `dotnet run --project src/FxDeck -- --console --data-dir <一時ディレクトリ>`。`--console` を付けると端末にデッキ URL・QR・ログが出る。
- **設定を壊す検証は必ず `--data-dir` の一時ディレクトリで行う。** 本番の設定は `%LOCALAPPDATA%\FxDeck\config.json`。
- API の E2E: `FxDeck.exe --data-dir <temp> --admin-port 20299 --deck-port 20200` → `curl http://127.0.0.1:20299/api/admin/status`。デッキのトークンは `<temp>/deck-token`（BOM なし）。デッキは `http://127.0.0.1:20200/?t=<token>` で Cookie 交換から入れる。
- 単発送信: `FxDeck.exe --send "e wave"`（Web サーバーを立てずに 1 回送って終了）。
- フロント開発: `cd src/FxDeck.Web && npm run dev`（`/api` は 20200 へ proxy）。または `npm run watch` + 環境変数 `FXDECK_WEBROOT=src/FxDeck.Web/dist` で本体からディスク上の dist を配信。
- 実際の FiveM で確認済みの前提: コマンド送信、コンソール出力の表示、ゲーム再起動後の再接続、`PRNT` のテキストオフセット 40、25ms の送信間隔。プロトコルを触ったら実機でもこの範囲を再確認する。

### UI の確認はヘッドレス Chrome で描画する

CSS を推測で直さず、描画して確認する。

- 静止画だけなら: `chrome.exe --headless=new --disable-gpu --hide-scrollbars --window-size=W,H --virtual-time-budget=6000 --screenshot=out.png <URL>`。**ヘッドレスは最小幅 500px**（390 を指定しても 500 で描画され、スクショだけ切り取られる）なので、モバイル幅の判断には使えない。
- モバイル寸法やクリックが要るときは DevTools プロトコルで操作する: `chrome.exe --headless=new --remote-debugging-port=9333 --remote-allow-origins=* --user-data-dir=<temp>` を起動し、`PUT /json/new?about:blank` でタブを作って `webSocketDebuggerUrl` に Node（組み込み `WebSocket`）で接続 → `Emulation.setDeviceMetricsOverride`（390×844 / 844×390、`mobile: true`）→ `Page.navigate` → `Runtime.evaluate` で操作 → `Page.captureScreenshot`。README のスクリーンショット（`Docs/images/`）もこの方法で撮った（`deviceScaleFactor: 2`、デッキはインストールバナーを閉じ、キーは `PointerEvent` の `pointerdown` → `pointerup` で押す）。

### トンネルの確認

- `curl -X POST http://127.0.0.1:20299/api/admin/tunnel/start`（初回は cloudflared を `<data-dir>/cloudflared/` に落とすので数秒〜数十秒）→ 返ってきた `tunnel.url` に対して `curl <url>/`（200 HTML）と `curl <url>/api/admin/status`（404 であること）→ `POST .../tunnel/stop` で `tasklist | findstr cloudflared` が空になること。**公開 URL が一時的に立つので、終わったら必ず停止する。**
- 固定 URL（Zero Trust）は、ダッシュボードの公開ホスト名が `http://127.0.0.1:<デッキポート>` に向いている必要がある。別ポートに向いていると Cloudflare が 502 を返す（cloudflared 側のログ `originService=` で転送先が分かる）。

## 3. リリース

1. `src/FxDeck/FxDeck.csproj` の `<Version>` を上げてコミットする（About 画面と `GET /api/admin/about` に出る。タグと一致しないとワークフローが失敗する）。
2. `dotnet test FxDeck.slnx` が全件合格することを確認。
3. 手元で publish して実機（PC の Chrome、スマホ）で最低限: 起動 → QR → デッキでキー押下 → 管理画面で編集が反映、を確認。
   - 自己完結: `dotnet publish src/FxDeck -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o <dir>`（約 60MB。圧縮なしだと約 130MB。Trimmed / AOT は使わない。DesignNote §5）
   - slim（フレームワーク依存）: `--self-contained false`、圧縮フラグなし（約 3MB。.NET 10 Desktop Runtime + ASP.NET Core Runtime が必要）
   - 出力先に一緒に出る `aspnetcorev2_inprocess.dll` は IIS 用で不要。配布するのは `FxDeck.exe` だけ
4. タグ `vX.Y.Z` を打って push する（`git tag v0.6.0 && git push origin v0.6.0`）。[.github/workflows/release.yml](../.github/workflows/release.yml) がテスト → 2 種の publish → `FxDeck-X.Y.Z-win-x64.exe` / `FxDeck-X.Y.Z-win-x64-slim.exe` / `SHA256SUMS.txt` を添付した GitHub Release を作る。本文は [.github/release-notes.md](../.github/release-notes.md)。
5. Release ページで内容を確認し、必要なら本文を編集する（変更点の箇条書きなど）。

## 4. 文言と言語

- ドキュメントは英語を標準とし、日本語版は `.ja.md` を並べる（README と CLAUDE.md がその形。`Docs/` の設計ドキュメントは当面日本語版のみ）。コミットメッセージは英語、コードの識別子・コメント・ログは英語。**実行コードに日本語リテラルを置かない**（残ってよいのは辞書と、マルチバイト文字列の扱いを検証するテストデータだけ）。`FxDeck.Emulator` は開発用ツールなので英語のみ。既定サンプルキーや `manifest.webmanifest` も英語。
- UI 文言は日本語が正、英語を併記（DesignNote §3.9）。**文言はコードに直書きしない。**
  - フロント: `src/FxDeck.Web/src/shared/locales/ja.ts`（キーの正）と `en.ts` に追加し、`useT()` / `t()` で引く。`en.ts` にキーが無いとビルドが失敗する。
  - サーバー: `src/FxDeck/Localization/Strings.ja.cs`（キーの正）と `Strings.en.cs` に追加し、`Localizer.T` / `Strings.Get` で引く。テスト `EveryLanguageHasExactlyTheJapaneseKeys` が全言語のキー一致を確認する。無い訳は日本語にフォールバック。
- 言語を増やすとき: フロントは `locales/xx.ts` を追加し `i18n.ts` の `Lang` / `loadDictionary` と設定画面の選択肢を拡張。サーバーは `Strings.xx.cs` を追加し `Lang` 列挙・`Strings.Lookup`・`Strings.Resolve`・`ConfigValidator.Languages` に登録。
- `language: auto` は 2 系統: UI（管理画面・デッキ）はブラウザ言語、サーバー側（トレイ・API エラー・`--console`・設定読込前の文言）は OS の UI カルチャ。

## 5. 過去にはまった点（再発防止）

- Kestrel の `ListenLocalhost(0)` は動的ポート非対応 → `Listen(IPAddress.Loopback, 0)`。
- トレイの終了処理を UI スレッドで `GetResult()` すると、HostedService の `StopAsync` 内の await が WinForms の SynchronizationContext に戻ろうとしてデッドロック → 停止は `Task.Run` で、`StopAsync` 内は `ConfigureAwait(false)`。
- Vite は CSS を 1 ファイルにまとめるので、管理画面用の汎用クラス名（`.empty` など）がデッキの `.key.empty` に当たる。クラス名は用途固有にする。
- `Encoding.UTF8` で `File.WriteAllText` すると BOM が付く。外部から読むファイル（deck-token、ログ）は `new UTF8Encoding(false)`。
- `$(IntermediateOutputPath)` は csproj 本文の評価時点では未定義。`$(BaseIntermediateOutputPath)` を使う。
- Windows Firewall は初回警告で［キャンセル］するとブロックルールを作り、それは許可ルールより優先される。「許可する」は既存の FxDeck ルールを消してから追加する。
- partial クラスを複数ファイルに分けたとき、静的フィールドの初期化子が別ファイルの静的フィールドを参照すると初期化順が不定で null になる（`Strings.Table` で踏んだ）。テーブルではなく `switch` かプロパティで引く。
- `admin.css` の入力欄スタイルは `input[type=…]` を型ごとに列挙している。新しい型（password / url 等）を使うときは両方のルールに追加しないとブラウザ既定の見た目になる。
- `@mdi/font` は eot/woff/ttf も参照するので `vite.config.ts` のプラグイン（`enforce: "pre"`）で woff2 だけに絞っている。Font Awesome 7 のメタデータは `metadata/icon-families.json`（5MB）で、ビルド時に軽量インデックスへ変換する（`scripts/gen-icon-index.mjs` → `src/generated/`、git 管理外）。
- 画像はブラウザ側（canvas）で 256×256 PNG にしてからアップロードし、サーバーは既に 256×256 PNG ならそのまま保存する。GDI+ の再エンコードは非決定的で、描き直すとエクスポート→インポートでハッシュが変わるため。
- Git Bash 経由の `perl -e` / `node -e` / ヒアドキュメントでは `\\` が `\` に潰れる。バックスラッシュを含むスクリプトはファイルに書いてから実行する。

## 6. 既知の課題・今後の候補

- 英語文言のネイティブ確認。
- トンネル: アプリがクラッシュした場合に cloudflared が残る可能性（Job Object 未使用）。固定 URL の転送先が違うときに UI で気付ける導線がない（Cloudflare の 502 になるだけ。設定画面の注意書きのみ）。
- 将来機能（UIUX 参照）: フォルダキー、プロファイル切替キー、デッキからの自由入力コマンド。
