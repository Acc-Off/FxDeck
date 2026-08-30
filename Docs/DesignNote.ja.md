# 設計メモ

発端: 「Stream Deck Mobile にインスパイアされた、fxcommands の独立版」。Windows 上の単一 exe がローカル HTTP サーバーを立て、スマホのブラウザからボタンを押して FiveM にコマンドを送る。

- **アプリ名: FxDeck**。命名理由: 「Stream Deck」「Deck Mobile」は Elgato の商標と混同されるため避ける。「FiveM」「Cfx」は Cfx.re のブランドなので含めず、コミュニティ慣例の `fx` 接頭辞（fxcommands、fxmanifest、FXServer）に倣う。GitHub 上で同名リポジトリが無いことを確認済み（次点は FxTiles）。
- GitHub リポジトリは `Acc-Off/FxDeck`。アセンブリ名・表示名・`%LOCALAPPDATA%` 配下は `FxDeck` に揃える。
- ライセンス: **MIT**

## 1. コンセプト

- PC 上で動く単一 exe が、FiveM/RedM クライアントのコンソールソケット（`127.0.0.1:29200`）に接続し、スマホのブラウザから押したボタンに対応するコンソールコマンドを送る。
- Elgato Stream Deck（ハード／ソフト／Mobile サブスク）への依存をなくした [fxcommands](https://github.com/josh-tf/fxcommands) の独立版。
- 基本は同一 LAN からの IP 直アクセス。**スマホと PC が別ネットワークになるケース**（スマホがモバイル回線のまま／ゲスト Wi-Fi／AP アイソレーション／PC が VPN 経由など）の救済として Cloudflare Tunnel（[CloudflaredKit](https://github.com/hsakoh/CloudflaredKit)）経由を用意する。「外出先から」は主目的ではない。

### やること（スコープ内）

- ボタン（コマンド）のグリッド UI をスマホで表示・実行
- ボタン／ページ（プロファイル）の編集、JSON でインポート／エクスポート
- QR コードでスマホを誘導（LAN 用／トンネル用）
- タスクトレイ常駐、トレイから管理画面をブラウザで開く
- ゲームのコンソール出力（`PRNT` フレーム）をスマホに流す
- fxcommands 互換のコマンド記法（`;` チェーン、`{NNNms}` ディレイ）、押す／離すの別コマンド、ステージ（§3.2）

### やらないこと（初期スコープ外）

- `@fxid:` トークンによる応答取得（サーバー側リソースの協力が必要なため）
- ダイヤル／エンコーダ相当の UI
- Windows 以外の OS
- Elgato のプロファイル形式のインポート

## 2. 全体アーキテクチャ

```
┌────────────────────────── PC (Windows) ─────────────────────────────┐
│                                                                     │
│  ┌──────────────┐   TCP 29200     ┌──────────────────────────┐      │
│  │ FiveM client │ ◄─────────────► │  本アプリ（単一 exe）     │      │
│  └──────────────┘ PPCR/CMND/PRNT  │                          │      │
│                                   │  ├ FxConsoleClient       │      │
│                                   │  ├ Kestrel               │      │
│                                   │  │  ├ 127.0.0.1:xxxxx 管理UI    │
│                                   │  │  └ 0.0.0.0:yyyyy   デッキUI  │
│                                   │  ├ NotifyIcon (トレイ)   │      │
│                                   │  └ CloudflaredKit ───────┼───┐  │
│                                   └──────────────────────────┘   │  │
└──────────────────────────────────────────────────────────────────┼──┘
                 LAN (HTTP)                 Cloudflare Tunnel (HTTPS)
                     ▲                              ▲
                     │                              │
              ┌──────┴──────┐                ┌──────┴────────┐
              │ スマホ (同一LAN) │            │ スマホ (別NW)  │
              └─────────────┘                └───────────────┘
```

### プロセス構成

- 1 プロセス。UI スレッドで `Application.Run(new TrayApplicationContext())`（WinForms、ウィンドウなし）、Kestrel は `WebApplication` をバックグラウンドで起動。
- 多重起動防止：名前付き Mutex。2 回目の起動は既存プロセスの管理 UI を開いて終了。

## 3. コンポーネント

### 3.1 FxConsoleClient（コンソールソケット）

FiveM クライアントが標準で開いている `127.0.0.1:29200` に接続する。**非公式プロトコル**（fxcommands の `connection-manager.ts` からの逆解析結果）なので、必ずこのクラスに閉じ込める。

| 項目 | 内容 |
|---|---|
| トランスポート | TCP |
| ハンドシェイク | 接続直後にクライアントから生の 4 バイト `PPCR`（`50 50 43 52`）を送信（フレームではない）。ゲーム側は `AINF` フレームで応答するが、内容は使わないので読み飛ばす |
| コマンド送信 | 12 バイトヘッダ + ペイロード。ヘッダ = `CMND`（4）+ プロトコル `00 D3`（2）+ 長さ big-endian（4）+ パディング `00 00`（2）。ペイロード = UTF-8 コマンド + `\n` + `\0`。**長さ = ペイロードのバイト数**（= コマンドの UTF-8 バイト数 + 2。ヘッダは含めない） |
| 受信 | ヘッダは送信と同じ 12 バイト構成（マジック 4 + プロトコル 2 + 長さ 4 + パディング 2）だが、**長さ = ヘッダ込みのフレーム全長**（送信と逆なので注意）。マジックは `PRNT`（コンソール出力）／`CHAN`／`CVAR`／`AINF`。`PRNT` のテキストはオフセット 40 以降（12 バイトヘッダ + 28 バイトのチャネルメタデータ）、末尾の NUL パディングを除去して trim。`CHAN`／`CVAR`／`AINF` は読み飛ばす |
| 受信の同期 | 先頭が既知のマジックでなければ次のマジック出現位置まで捨てて再同期する。長さが 12 未満または 1MiB 超なら壊れたフレームとみなしてマジック 4 バイトを捨てて再同期する（不完全なフレームを待ち続けない） |
| 流量制御 | 送信間隔 25ms 以上（詰めるとフレームが落ちる）。クライアントの送信キューで担保する |
| アイドル | 約 5 秒でサーバー側が切断するため、切断→再接続を前提に実装 |

設計方針:

- `IFxConsoleClient` インターフェースを切り、実装は `TcpFxConsoleClient`。
- 接続状態（未接続／接続中／接続済）をイベントで通知し、デッキ UI に表示。
- **常時接続 + 自動再接続**。`Start` 後はバックグラウンドで接続を維持し、切断されたら自動で繋ぎ直す。確立済みの接続が切れた場合（アイドル切断）は即時（100ms 後）に再接続し、接続失敗（ゲーム未起動）が続く間は 1 秒から始めて最大 5 秒まで間隔を延ばす。
- `Send` の挙動は接続状態で分ける。**未接続**（接続に失敗した＝ゲーム未起動）なら**待たずに失敗を返す**（デッキ側で「FiveM が起動していません」を出すため。UIUX §4.7）。**接続中**（起動直後、またはアイドル切断直後の再接続中）なら**最大 1 秒だけ接続を待ってから送る**。5 秒ごとに起きるアイドル切断の隙間で押下が失敗しないようにするため。待っている間に接続失敗が確定したら即座に失敗を返す。
- **エミュレータ** `FxConsoleEmulator`（テスト用、29200 で待ち受けて受信コマンドをログ＆`PRNT` を返す。`PPCR` には `AINF` で応答し、既定で 5 秒のアイドル切断も再現する）を同梱。ゲーム無しで開発・CI できるようにする。fxcommands の `scripts/fivem-emulator.cjs` が参考。エミュレータは本体のコーデックを共有せず独立に実装する（対称なバグを結合テストで見逃さないため）。
- リモートクライアント（`-devcon` 起動）向けに接続先 IP/Port は設定可能にする。既定は `127.0.0.1:29200`。

### 3.2 コマンド実行エンジン

fxcommands 互換の記法を解釈して `FxConsoleClient` に流す。

- `cmd1; cmd2; cmd3` — 順次実行。`;`（改行も同じ区切りとして扱う）で分割し、1 コマンドずつ別フレームで送る（FiveM のコンソール自体も `;` チェーンを解釈するが、フレームを分けることで 25ms ギャップと途中失敗の検出ができる）
- `{500ms}` — ディレイ。`ms` は大文字小文字を区別せず、括弧内の空白は許容（`{ 500 ms }`）。前後の `;` は省略可（`a;{500ms};b` と `a{500ms}b` は同じ）
- `;;` — 既定 500ms のディレイ（fxcommands 互換の省略記法）
- 各コマンドは前後の空白を trim し、空になったものは捨てる。ディレイに該当しない `{...}` はそのままコマンドの一部として送る
- ディレイの上限は 60 秒（超える指定は 60 秒に丸める。直列キューを長時間塞がないため）
- パーサは独立クラス `CommandMacroParser` にして単体テストを書く。
- 実行は `MacroExecutor` のキューで直列化（FIFO）。コマンド送信が失敗（未接続）したらそのマクロの残りは中止し、失敗理由を返す。25ms ギャップは `FxConsoleClient` 側で担保する。

#### 押す／離す（Press / Release）と ステージ

fxcommands の「On Press / On Release」「Staged buttons（最大 5）」に相当する機能。1 キーは **1〜5 個のステージ**を持ち、各ステージは **見た目（title / icon / background）+ 押したときのマクロ `command` + 離したときのマクロ `releaseCommand`（任意）** を持つ。ステージ 1 の見た目とマクロはキー自身のフィールド（`title` / `icon` / `background` / `action.command` / `action.releaseCommand`）、ステージ 2 以降は `action.stages[]` に**完全な形で**持つ（継承はしない。管理画面でステージを増やすときにステージ 1 の見た目をコピーする）。

| 用語 | 意味 |
|---|---|
| タップキー | `releaseCommand` が空のキー（従来どおり）。指を離したときに `command` を送る |
| ホールドキー | `releaseCommand` があるキー。**押した瞬間**に `command`、**離した瞬間**に `releaseCommand` を送る（例: 押している間 `e sit`、離すと `e c`） |
| ステージキー | `action.stages` が 1 つ以上あるキー。押すたびに次のステージへ進み、最後の次は 1 に戻る |

サーバー（`DeckHub`）の規則:

- キーごとの**現在ステージはサーバーがメモリ上に持つ**（`keyId → index`）。複数端末が同じデッキを見ても一致させるため。永続化はしない（fxcommands は永続化するが、ゲーム側の状態は FxDeck の再起動をまたいで残らないので、揃えて 1 に戻す）。設定変更でキーが消えた／ステージ数が減って index が範囲外になったときは 1 に戻す。
- `press` を受けたら現在ステージの `command` を実行する（空なら何もせず成功扱い）。同じキーの `command` 実行中に来た `press` は無視する（多重実行防止、従来どおり）。
- `release` を受けたら現在ステージの `releaseCommand` を実行する。`release` は実行中ガードの対象外（`press` のマクロの後ろに FIFO で並ぶだけ）。
- **ステージを進めるのは 1 サイクルの最後の段階が成功したとき**: タップキーは `press` の成功後、ホールドキーは `release` の成功後（`releaseCommand` が空なら `release` 受信時）。未接続で失敗したら進めない（押しても何も起きなかった、と利用者の認識を揃える）。進んだら `stage` メッセージを全端末に配る。
- ホールドキーは**セッションごとに押下中のキー**を覚えておき、WebSocket が閉じたら（スマホのスリープ、圏外、アプリ終了）押しっぱなしのキーの `releaseCommand` を実行する。キーボードのフォーカス喪失時のキーアップ相当で、`e sit` が掛かりっぱなしになるのを防ぐ。
- `result` は `press` / `release` それぞれに返す（失敗時のみ UI が赤点滅＋トースト）。

`--send` と管理画面の「テスト送信」は単発のマクロ文字列を送るだけなので、ステージやホールドの概念を持たない。

### 3.3 Web サーバー（Kestrel）

**リスナーを 2 つに分け、セキュリティ境界にする。**

| 用途 | バインド | 認証 | 提供物 |
|---|---|---|---|
| 管理 UI | `127.0.0.1:<port>` のみ | なし（ローカル前提） | 設定編集、インポート／エクスポート、QR 表示、トークン再発行、トンネル ON/OFF、ログ |
| デッキ UI | `0.0.0.0:<port>`（＋トンネル） | トークン必須 | ボタングリッド、コンソールログ、接続状態 |

- 1 つの Kestrel に 2 エンドポイントを設定し、ミドルウェアで「ローカル接続か（`LocalPort` が管理ポートか、かつ `RemoteIpAddress.IsLoopback`）」を見て管理 API を拒否する。
- API は Minimal API。ボタン押下・接続状態・コンソールログはデッキ用 WebSocket 1 本。
- 静的ファイル（SPA の `dist`）は EmbeddedResource（LogicalName `wwwroot/<相対パス>`）にして、マニフェストリソース名から引く自前の `IFileProvider` で配信する（`ManifestEmbeddedFileProvider` は npm build 後にターゲット内で動的に追加した EmbeddedResource を拾えないため使わない）。`/`（QR の着地点）・`/deck/*`・`/admin/*` は `index.html` を返す SPA フォールバック。ハッシュ付きアセット（`/assets/*`）は `Cache-Control: immutable`、`index.html` と `sw.js` は `no-cache`。
- 開発時: 環境変数 `FXDECK_WEBROOT` にディレクトリを指定すると埋め込みではなくそのディレクトリ（`vite build --watch` の出力）を配信する。Vite dev server（`npm run dev`）は `/api` をデッキポートへ proxy する。
- `--console` 付きで起動したときは、デッキ URL と QR（半角ブロック文字）を端末にも表示する（開発・トラブルシュート用。通常の利用者は管理 UI の「接続」画面を使う）。

#### デッキ WebSocket メッセージ（JSON、1 行 1 メッセージ）

| 方向 | メッセージ | 意味 |
|---|---|---|
| スマホ → PC | `{ "type": "press", "keyId": "guid" }` | キー押下。サーバーは現在ステージの `command` を `MacroExecutor` に投入する。同じキーが実行中なら無視する（UIUX §4.3）。タップキーは指を離したとき、ホールドキーは押した瞬間に送る（§3.2） |
| スマホ → PC | `{ "type": "release", "keyId": "guid" }` | ホールドキーで指を離した（またはジェスチャがキャンセルされた）。現在ステージの `releaseCommand` を投入する。タップキーは送らない |
| PC → スマホ | `{ "type": "hello", "profiles": [...], "settings": {...}, "game": "connected", "stages": { "keyId": 1 } }` | 接続直後に現在の状態をまとめて送る。`stages` はステージ 1 以外にいるキーの現在ステージ（0 始まりの index） |
| PC → スマホ | `{ "type": "stage", "keyId": "guid", "stage": 1 }` | ステージが進んだ／戻った（全端末に配信） |
| PC → スマホ | `{ "type": "status", "game": "disconnected" \| "connecting" \| "connected" }` | ゲーム接続状態の変化 |
| PC → スマホ | `{ "type": "result", "keyId": "guid", "phase": "press" \| "release", "success": true, "reason": "notConnected" \| ..., "message": "..." }` | 押下（または離す）結果。失敗時のみ UI が赤点滅＋トーストを出す。押した端末にだけ返す |
| PC → スマホ | `{ "type": "profiles", "profiles": [...] }` | プロファイルが変わった（全端末に配信） |
| PC → スマホ | `{ "type": "settings", "settings": { "theme": "dark", "deckStatusBar": true, "language": "auto" } }` | デッキに関係する設定が変わった |
| PC → スマホ | `{ "type": "console", "line": "..." }` | ゲームのコンソール出力（PRNT）1 行 |

トークン再発行時はサーバーが close code **4001** で全接続を閉じ、SPA は「アクセスが無効になりました」画面を出す。それ以外の切断は自動再接続する。

#### 主な API

```
# デッキ（トークン必須）
GET  /api/deck/profile            現在のプロファイル（ボタン定義）
GET  /api/deck/assets/{hash}      ユーザー画像（256×256 PNG、Cache-Control: immutable）。管理リスナーからの要求は Cookie なしでも通す（管理 UI のプレビュー用）
POST /api/deck/session            ?t= トークンを HttpOnly Cookie に交換
WS   /api/deck/ws                 push: {type:"press", keyId}
                                  recv: {type:"hello"|"status"|"result"|"console"|"profiles"|"settings"}

# 管理（localhost のみ）
GET  /api/admin/status            ゲーム接続状態、リスナー、LAN IP、デッキ URL、接続中デッキ数、トンネル
GET/PUT /api/admin/config         設定全体（管理 UI は編集のたびに全体を PUT する＝自動保存。PUT は検証して保存し、デッキへ即配信）
GET  /api/admin/qr?kind=lan|tunnel  QR 画像（PNG）
POST /api/admin/token/rotate      デッキトークン再発行
POST /api/admin/send              {command} をそのまま実行（テスト送信）
GET  /api/admin/export?profile=<id>  .fxdeck（zip）ダウンロード。profile 省略で全体
POST /api/admin/import            multipart で .fxdeck / .json をアップロード。?mode=profile|all
GET  /api/admin/firewall/status   FxDeck 受信ルールの有無
POST /api/admin/firewall/allow    netsh を UAC 付きで起動してルール追加（§3.5）
GET  /api/admin/network/adapters  QR に使える IPv4 アダプタ一覧
GET  /api/admin/assets            ユーザー画像の一覧 [{hash,size,modified,referenced}]
POST /api/admin/assets            multipart で画像を追加 → {hash}（サーバーで 256×256 PNG に正規化、同一内容は重複排除）
POST /api/admin/assets/prune      どのキーからも参照されない画像を削除 → {deleted}
POST /api/admin/game/test         現在の設定でゲームへの TCP 接続を試す
GET/PUT /api/admin/autostart      Windows 起動時の自動起動（HKCU\...\Run）
GET  /api/admin/about             バージョン、ライセンス、THIRD-PARTY-NOTICES 本文
POST /api/admin/restart           アプリを再起動（ポート変更の反映用。トレイ側が停止→同じ引数で再起動）
POST /api/admin/tunnel/start      トンネルを開始（設定の mode が off なら TryCloudflare）。準備が整うか失敗するまで待って {tunnel} を返す。失敗は 502 で {tunnel.error} に理由
POST /api/admin/tunnel/stop       トンネルを停止（cloudflared を kill）
```

- `/api/admin/status` の `tunnel` は `{ mode, autoStart, status: "stopped"|"starting"|"running"|"error", url, deckUrl, error: { phase: "download"|"start"|"exited", message } | null }`。`url` は公開 URL（TryCloudflare は cloudflared の出力から取得、固定 URL は設定の `namedUrl`）、`deckUrl` はそれにトークンを付けた QR の中身。管理 UI は 2 秒ごとの status ポーリングで追従する。

- インポートの意味: `mode=profile` はアップロードされたプロファイルを**末尾に追加**（id が既存と衝突したら振り直す）。`mode=all` は `settings`（トークン以外）と `profiles` を**丸ごと置き換える**（UI で確認ダイアログを出す）。JSON 単体のアップロードは中身が `{profiles:[...]}` か `{id,name,keys,...}` かで判定する。
- PUT `/api/admin/config` の検証: `version` が 1、ポートが 1〜65535、グリッドが 1〜12 列・1〜8 行、キーがグリッド内で重複なし、`action.type` が既知。検証エラーは 400 で理由を返し、保存しない。

### 3.4 認証（デッキ UI）

- ID/PW は使わず**ランダムトークン 1 本**（32 バイト以上、Base64url）。
- QR の中身は `http(s)://<host>:<port>/?t=<token>`。
- SPA は起動時に `t` を読み取り、`POST /api/deck/session` で HttpOnly Cookie に交換し、`history.replaceState` で URL から除去する。以後は Cookie で認証。
- トークン照合は固定時間比較（`CryptographicOperations.FixedTimeEquals`）。IP 単位のレート制限（`AddRateLimiter`。`/api/deck/session` は 1 分あたり 10 回）。
- Cookie（`fxdeck_session`、HttpOnly、SameSite=Lax、90 日スライディング）の値は**トークンから HMAC-SHA256 で導出**した固定値にする。サーバーにセッション状態を持たないので PC を再起動してもスマホは QR を読み直す必要がなく、トークンを再発行すれば導出値も変わって全端末が自動で無効になる。
- 管理 UI から再発行 → 既存セッションは全て無効化。
- デッキトークンは `%LOCALAPPDATA%\FxDeck\deck-token` に保存する（初回起動時に生成）。
- LAN 側は HTTP 平文（自己署名 HTTPS はスマホ側で弾かれるため採用しない）。家庭内 LAN 前提と明記する。トンネル側は Cloudflare で HTTPS 終端。

### 3.5 ネットワーク／到達性

- **LAN**: QR にはホスト名ではなく **IP アドレスを直接**埋める（mDNS `.local` は Android ブラウザで不安定）。NIC が複数ある場合は管理 UI でアダプタを選べるようにし、選択したものの IP を使う。
- **Windows Firewall**: `0.0.0.0` で Listen すると初回に Windows が「セキュリティの重要な警告」ダイアログを出す。キャンセルされると LAN から届かない（管理 UI は loopback なので影響を受けず、常に開ける）。復旧導線として:
  - 管理 API `POST /api/admin/firewall/allow`（localhost 限定）を受けた FxDeck プロセスが `Process.Start` で `netsh advfirewall firewall add rule name="FxDeck" dir=in action=allow protocol=TCP localport=<deckPort>` を `UseShellExecute=true, Verb="runas"` で起動し、UAC を出す。ブラウザは昇格に関与しない
  - ルールは exe パスではなく**ポート指定**にする（exe を移動しても有効）。ポート変更時は新ルールを追加
  - 引数は固定文字列＋整数検証済みポートのみ。ブラウザからの入力文字列を渡さない
  - 状態確認は Windows Firewall の COM API（`HNetCfg.FwPolicy2`。昇格不要、ローカライズされた netsh 出力を解析しなくて済む）で「名前が FxDeck、または本 exe に紐づく受信ルール」を列挙し、`GET /api/admin/firewall/status` で `{ruleExists, portAllowed, blocked}` を返す
  - 初回 Listen 時の Windows の警告で [許可] を押すと exe 単位の許可ルール（名前 FxDeck、ポート任意）が、[キャンセル] だと**ブロック**ルールが自動作成される。ブロックは許可より優先されるため、「許可する」は `netsh ... delete rule name="FxDeck" dir=in` で既存の FxDeck 受信ルールを消してからポート許可ルールを追加する（`cmd /c` で 1 回の UAC にまとめる）
  - 管理 UI の「接続」画面にファイアウォール状態と [許可する] ボタン、および同一ネットワーク（モバイル回線・ゲスト Wi-Fi・AP 分離）の注意を表示する
- **トンネル**: CloudflaredKit を使用。用途は「スマホと PC が別ネットワーク」の救済であり、既定は OFF。LAN で届かないときに管理 UI から ON にする導線（接続診断 → 「トンネルを使う」）を置く。
  - 既定は TryCloudflare（アカウント不要、起動ごとに URL が変わる → QR も再生成）
  - Zero Trust トークンによる固定 URL は「上級者向け」設定として分離。cloudflared は固定トンネルの公開 URL を出力しないので、Zero Trust ダッシュボードで設定した公開ホスト名を `tunnel.namedUrl` として利用者に入力してもらい、QR に使う
  - `cloudflared` バイナリは実行時に `%LOCALAPPDATA%\FxDeck\cloudflared\` へダウンロード（GitHub Releases の latest）されるため、配布物は単一のまま。自動更新はしない（更新したければ exe を消せば次回の開始時に取り直す）
  - 実装は `Services/TunnelService`。CloudflaredKit の `ICloudflaredService` を包み、状態 `stopped → starting → running | error` と公開 URL を持つ。`Changed` イベントをトレイが購読する。開始・停止は `SemaphoreSlim` で直列化する
  - `CloudflaredOptions` は起動時固定ではなく、開始のたびに設定（mode・namedToken）と実際のデッキポートから組み立てる（`IOptionsMonitor<CloudflaredOptions>` を差し替える）。転送先は `http://127.0.0.1:<deckPort>`（`localhost` だと IPv6 に解決されうる）
  - エラーは UIUX §7 の通り**ダウンロード失敗**（`phase: "download"`。GitHub に届かない・プロキシ等）と**起動失敗**（`phase: "start"`。URL が 30 秒以内に出ない、固定トークンが不正で即終了）を分けて返す。稼働中に cloudflared が落ちたら `phase: "exited"`（終了コード付き）。自動再起動はしない（利用者が「再試行」を押す）
  - `tunnel.autoStart` が true で mode が off でなければ、アプリ起動時（`ApplicationStarted`）にバックグラウンドで開始する。アプリ終了時は必ず停止して cloudflared を残さない
  - トンネル経由の要求はすべて `127.0.0.1` から届くので、`/api/deck/session` のレート制限の分割キーは、リモートが loopback かつ `CF-Connecting-IP` ヘッダーがあればそれを使う（管理 API はポートで判定しているので影響なし）
  - トンネルは HTTPS 終端が Cloudflare なので、デッキの Cookie は `Secure` を付けない（LAN は平文 HTTP）。トークンは URL クエリで渡るため TryCloudflare の URL が変わっても Cookie は残る

### 3.6 トレイ常駐

- WinForms `NotifyIcon` + `ApplicationContext`（TFM `net10.0-windows`、`UseWindowsForms=true`、`OutputType=WinExe`）。Kestrel は `WebApplication.StartAsync` でバックグラウンド起動し、UI スレッドは `Application.Run`。
- メニュー: 管理画面を開く／デッキ URL をコピー／データフォルダを開く（データディレクトリをエクスプローラーで開く。ログ採取や config.json の手編集の入口）／トンネルを開始・停止（稼働中は「トンネル URL をコピー」も）／ゲーム接続状態・トンネル状態（表示のみ）／終了。ダブルクリックで管理画面。
- アイコンは実行時に System.Drawing で描画（角丸タイル。ゲーム未接続＝グレー、接続済＝カラー）。画像アセットを持たない。
- 多重起動防止: 名前付き Mutex `Local\FxDeck`。起動時に `%LOCALAPPDATA%\FxDeck\admin-url` へ管理 UI の URL を書き、2 回目の起動はそれを読んでブラウザで開いて終了する。
- 初回起動（config.json を新規作成したとき）は既定ブラウザで管理画面の「接続」を自動で開く。2 回目以降はバルーンで「起動しました」。
- WinExe なのでコンソールは無い。`--send`／`--console` 指定時は `AttachConsole(ATTACH_PARENT_PROCESS)` で呼び出し元の端末に出力する（開発・スクリプト用）。ログは `%LOCALAPPDATA%\FxDeck\logs\fxdeck.log`（1MB でローテーション、直近 3 世代）。
- Windows 起動時に自動起動（`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の `FxDeck` 値に exe パス）をオプションで。

### 3.7 フロントエンド（SPA）

画面構成・操作・フローは [UIUX.ja.md](./UIUX.ja.md) を正とする。ここでは技術的な要点のみ。

- **Vite + React + TypeScript**。管理画面とデッキ画面を同一 SPA 内でルーティング分割（`/admin/*`、`/deck/*`）。
- デッキ画面は **PWA** 化（`manifest.json` + 最小限の Service Worker）。
  - ホーム画面追加でフルスクリーン
  - `Screen Wake Lock API` でスリープ抑止
  - `navigator.vibrate` で押下フィードバック
  - WebSocket 切断時は自動再接続、切断中はボタンを無効化して状態を明示
- デッキのレイアウト: プロファイルごとに固定グリッド（3×2 / 5×3 / 8×4 / カスタム、横向き基準・縦向きは転置）。キーは背景色・アイコン（組み込みアイコンセット or 画像。詳細は §3.8）・オーバーレイのタイトル。
- 状態管理は Zustand。UI ライブラリは使わず素の CSS（デッキは画面が単純で、管理 UI も③で必要になったら検討）。ルーティングはパス判定のみ（react-router は使わない）。
- 縦向きの「転置」は、キーを読み順（左→右、上→下）の通し番号で並べ直す方式（5×3 の 7 番目のキーは縦向き 3×5 では 3 行目の 1 列目）。

### 3.8 アイコン

要件: 組み込みアイコンセットを検索して選べる／ユーザー画像（PNG 等）も使える／config.json を肥大化させない。

#### 組み込みアイコンセット: MDI + Font Awesome Free + Unicode 絵文字

| セット | 点数 | ライセンス | 検索用メタ | 備考 |
|---|---|---|---|---|
| **Material Design Icons (MDI)** | 約 7,400 | Apache 2.0 | `@mdi/svg/meta.json`（name / aliases / tags） | 主力 |
| **Font Awesome Free** | 約 2,000 | アイコン: **CC BY 4.0**／フォント: SIL OFL 1.1／コード: MIT | `@fortawesome/fontawesome-free/metadata/icons.json`（search terms） | solid / regular / brands の 3 スタイル。CC BY のため帰属表示が必要（§5） |
| **Unicode 絵文字** | 約 3,800 | 端末の絵文字フォントを使うためライセンス考慮なし | `emojibase-data`（MIT）等の keywords | 描画は端末依存（iOS / Android / Windows で絵柄が変わる）。それが嫌なら将来 Twemoji（CC BY 4.0）等の同梱を検討 |

game-icons.net は絵柄が好みでないため不採用。

**配布形態について** — MDI も Font Awesome も「アイコンフォント」と「SVG」の両方で配布されている。

| 形態 | 中身 | 使い方 | 特徴 |
|---|---|---|---|
| アイコンフォント（webfont） | `.woff2`＋ CSS（MDI 約 400KB、FA は solid / regular / brands 合計 約 400KB） | `<span class="mdi mdi-hand-wave">`、`<i class="fa-solid fa-hand">` | 導入が最も簡単。全アイコンをフォントで持つので、使う数に関係なくサイズ一定 |
| SVG パス | アイコンごとの `<path d="…">` 文字列（`@mdi/js`、`@fortawesome/free-solid-svg-icons`） | `<svg><path d={…}/></svg>` | 使うアイコンだけ配れるが、実装の手間が増える |

**採用: アイコンフォント方式。** 理由は単純さ。フォントと CSS を SPA の `dist` に含め（exe に EmbeddedResource として同梱される。CDN 参照はしない＝LAN でインターネット不要）、管理 UI・デッキ UI 双方で同じ CSS クラスで描画する。デッキ側はフォントを初回に 1 度取得し、Service Worker がキャッシュする。絵文字はフォント同梱なしで文字としてそのまま描画する。

検索用メタ（MDI `meta.json` 約 1MB、FA `icons.json` 約 1MB、絵文字 keywords 数百 KB）は**管理 UI のピッカーを開いたときだけ遅延ロード**し、デッキ UI では読まない。ピッカーは 3 セット横断で name / aliases / tags / keywords の部分一致検索（クライアント側で完結）、セットごとにタブ or フィルタで絞れるようにする。

#### ユーザー画像

- **受け付ける形式: PNG / JPEG / WebP / GIF（静止画として扱う）**。SVG は `<script>` 混入のサニタイズが必要になるため初期は受け付けない（将来検討）。
- 画像は config.json に入れず、`%LOCALAPPDATA%\FxDeck\assets\<sha256>.png` に保存し、ボタンからはハッシュで参照する（同一画像は自動で重複排除）。「参照」にはキー本体の `icon` と `action.stages[].icon` の両方を含める（未使用画像の判定・エクスポートの同梱・インポートの解決すべて）。
- **256×256 に縮小して PNG に正規化**（1 枚あたり数十 KB）。元画像は保持しない。縦横比は保ち、余白は透明。変換は 2 段階:
  - 管理 UI（ブラウザ）が `createImageBitmap` + canvas で 256×256 PNG にしてからアップロードする。WebP や GIF（先頭フレーム）はここで吸収される
  - サーバー（`Config/AssetStore`、System.Drawing）が受け取った画像をデコードして検証し、**既に 256×256 の PNG（512KB 以下）ならそのまま**、それ以外は 256×256 PNG に描き直す（GDI+ は WebP を読めないので、サーバー単体では PNG / JPEG / GIF のみ）。ハッシュは保存する PNG の SHA-256。「そのまま」にするのは GDI+ の再エンコードが決定的でなく、描き直すとエクスポート→インポートでハッシュが変わって重複排除が効かなくなるため
- デッキ UI には `/api/deck/assets/{hash}` で配信（`Cache-Control: immutable`、Service Worker がキャッシュファーストで保持）。管理 UI のプレビューも同じ URL を使うので、管理リスナー（loopback）からの要求は Cookie なしで通す。
- どこからも参照されなくなった画像は、管理 UI の「未使用画像を削除」（`POST /api/admin/assets/prune`）で掃除（自動 GC はしない）。
- ハッシュは 64 桁の小文字 16 進。設定の検証でこの形式を要求する。

#### ボタンのアイコン表現

```jsonc
"icon": { "type": "mdi",   "name": "hand-wave" }                     // MDI
"icon": { "type": "fa",    "style": "solid", "name": "hand" }        // Font Awesome（style: solid | regular | brands）
"icon": { "type": "emoji", "value": "👋" }                           // Unicode 絵文字
"icon": { "type": "image", "hash": "3f9a…" }                         // ユーザー画像
"icon": null                                                         // ラベルのみ
```

#### エクスポート／インポート

- 粒度は **プロファイル単位** と **全体** の 2 種類（§4）。どちらを選んでも **zip で出力**し、その範囲から参照されている画像だけを `assets/` に同梱する。
  - プロファイル単位: `profile.json` + `assets/`
  - 全体: `config.json`（トークンは除く）+ `assets/`
- 拡張子は `.fxdeck`（実体は zip）にして、エクスプローラで見分けやすくする。
- **インポートは `.fxdeck` (zip) と素の JSON の両方を受け付ける**。JSON で画像参照が解決できないボタンはラベル表示にフォールバックし、警告を出す。
- 画像は全プロファイルで共有のストア（ハッシュ参照）なので、プロファイル単位の zip をインポートしても同じ画像は重複保存されない。
- インポート時の画像解決: キーが参照するハッシュがストアにあればそのまま。無ければ zip の `assets/<hash>.png` を取り込む（改めて正規化してハッシュを計算し直し、変わればキーの参照も付け替える）。どちらにも無ければアイコンを外してラベル表示にし、件数を警告する（UIUX §7）。

### 3.9 多言語対応（i18n）

利用者の言語で UI を出す（ロードマップ⑥で実装済み）。

- **対応言語**: 日本語（正）と英語。他言語は辞書ファイルの追加だけで足せる構造にする（翻訳は募集ベース）。
- **言語の決め方**: 設定 `settings.language` = `"auto" | "ja" | "en"`（既定 `auto`）。`auto` はブラウザの `navigator.languages` を見て `ja*` なら日本語、それ以外は英語。テーマと同じく管理画面とデッキで共通の設定にし、デッキには WebSocket の `settings` メッセージで配る（利用者は 1 人なので端末ごとの切り替えは持たない）。
- **フロント**: 依存を増やさず、型付きキーの辞書（`shared/locales/ja.ts` が正。`as const` からキー型 `MessageKey` を作り、`en.ts` は `satisfies Record<MessageKey, string>` で欠落をコンパイルエラーにする）と `useT()` フック／非フックの `t(key, params)`（`shared/i18n.ts`、zustand ストア）で実装。`{name}` を補間する。日本語は常に同梱（フォールバック）、英語は初回使用時に遅延ロード。複数形・日付は最小限（現状ほぼ不要）。絵文字の検索インデックスは既に日英両方のラベルを持つ。`document.documentElement.lang` も合わせる。
  - 管理 UI は設定の読み込み後に `settings.language` を適用し、それまではブラウザ言語。デッキは `hello` / `settings` メッセージの `language` を適用する。
- **サーバー側の文言**（検証エラー、インポートのエラー、API のメッセージ、トンネルのエラー、トレイメニュー、`--console` の表示、MessageBox、`--help`）: `Localization/Strings`（言語ごとに `Strings.ja.cs` / `Strings.en.cs` の辞書を持つ partial クラス。`Lang → 辞書` のレジストリで引き、無い訳は日本語→キーにフォールバック。`string.Format` 形式）と `Localizer`（設定値を毎回読む。`auto` は OS の UI カルチャ）で引く。設定の読み込み前に出る文言（引数エラー、usage）は OS カルチャ。`PUT /api/admin/config` の検証だけは**保存しようとしている文書の `language`** で出す（UI の切り替えと同時に揃うように）。トレイは `ConfigStore.Changed` で言語変更を拾ってメニュー文言を書き直す。
- **API のエラー**: 単発エラー（`{error}`）には機械可読な `code`（`importModeInvalid`、`fileTooLarge`、`invalidPackage`、`importInvalid`、`imageTooLarge`、`notImage`、`portInvalid` など）を添える。検証エラーの配列 `errors` は翻訳済み文字列のみ（そのまま表示する用途）。
- **変えないもの**: ドキュメント・コミットメッセージは日本語、コードの識別子・コメントは英語、ログは英語（開発者向け）。
- 文言の正は日本語。英語は日本語から起こし、キーが増えたら両方を同時に更新する（片方に無いキーはビルド時の型チェックで検出）。

## 4. データモデル（案）

```jsonc
{
  "version": 1,
  "settings": {
    "game": { "host": "127.0.0.1", "port": 29200 },
    "adminPort": 0,            // 0 = 自動
    "deckPort": 20200,         // 既定
    "lanAdapter": null,        // null = 自動選択
    "tunnel": {
      "mode": "off|try|named",   // off = 使わない（「接続」画面の開始ボタンは TryCloudflare で一時的に開始できる）
      "namedToken": null,        // Zero Trust のトンネルトークン（エクスポートに含めない）
      "namedUrl": null,          // 固定トンネルの公開 URL（https://deck.example.com）。QR に使う
      "autoStart": false         // アプリ起動時に自動開始（mode が off のときは無視）
    },
    "autoStart": false,
    "theme": "dark",           // dark | light | system（管理 UI・デッキ共通）
    "language": "auto",        // auto | ja | en（§3.9）
    "deckStatusBar": true
  },
  "profiles": [
    {
      "id": "guid",
      "name": "Default",
      "order": 0,
      "columns": 5, "rows": 3,                       // 固定グリッド（横向き基準）
      "keys": [
        {
          "id": "guid",
          "row": 0, "col": 0,
          "title": { "text": "Wave", "position": "bottom", "visible": true },
          "background": "#2a2a2a",
          "icon": { "type": "mdi", "name": "hand-wave" },
          "action": {
            "type": "command",                                   // 将来: folder / switchProfile
            "command": "e wave",                                 // 押したとき（ステージ 1）
            "releaseCommand": null,                              // 離したとき（任意。あると「ホールドキー」になる。§3.2）
            "stages": [                                          // ステージ 2〜5（任意、最大 4 要素。§3.2）
              {
                "title": { "text": "Stand", "position": "bottom", "visible": true },
                "background": "#2a2a2a",
                "icon": { "type": "mdi", "name": "human-handsup" },
                "command": "e c",
                "releaseCommand": null
              }
            ]
          },
          "holdToConfirm": false
        }
      ]
    }
  ]
}
```

- `action.stages` の各要素は見た目とマクロを**すべて**持つ（キー本体から継承しない）。`icon` は `null` 可。
- 検証: `stages` は 0〜4 要素、各要素の `title` / `background` は必須、`icon` はキーと同じ規則、`command` は空でもよい（`releaseCommand` だけのステージも可）。`command` と `releaseCommand` の両方が空のキー／ステージは「コマンドがありません」として押下時に失敗を返す（保存は許す。作りかけのキーを置けるように）。
- `holdToConfirm` とホールドキーは併用できる: 600ms 押し続けた時点で `command`、離した時点で `releaseCommand`。

UI 上の理由（[UIUX.ja.md](./UIUX.ja.md) §0, §4, §8）: Stream Deck Mobile に倣い、固定グリッド・タイトルはオーバーレイ・キーの動作は拡張可能な `action` にしている。

- 保存先: `%LOCALAPPDATA%\FxDeck\config.json`。デッキトークンは別ファイル（エクスポートに含めない）。ユーザー画像は `%LOCALAPPDATA%\FxDeck\assets\`（§3.8）。
- 初回起動時はサンプルキー入り（`e wave` など）の `Default` プロファイル 1 つで生成する。
- `config.json` はファイル監視してホットリロードする（手で編集しても即デッキに反映。管理 UI ができるまでの編集手段であり、以降も残す）。壊れた JSON は無視して直前の設定を保ち、ログに警告を出す。
- 環境変数 `FXDECK_DATA_DIR` でデータディレクトリを差し替えられる（テストと複数インスタンス検証用）。
- インポート／エクスポートは `profiles` 単位と全体の 2 種類。形式は §3.8 参照（zip 基本、JSON も可）。

## 5. 配布・ビルド

- 単一 exe（`PublishSingleFile`）を 2 種類配る（手順は [DevelopmentNote.ja.md](./DevelopmentNote.ja.md) §3）
  - **自己完結（推奨）**: `--self-contained true` + `EnableCompressionInSingleFile`。約 60MB（圧縮なしだと約 130MB）。.NET ランタイム未導入の PC でも動くことを優先
  - **slim（フレームワーク依存）**: `--self-contained false`。約 3MB だが .NET 10 Desktop Runtime と ASP.NET Core Runtime の 2 つのインストールが要る。ランタイム導入済みの人向けの副次的な選択肢
  - `PublishTrimmed` は ASP.NET Core + WinForms の組み合わせで壊れやすいので **OFF**
  - Native AOT は WinForms 非対応のため対象外
- フロントは MSBuild ターゲット（`BeforeBuild` / `BeforePublish`）で `npm ci && npm run build` → `dist` を `EmbeddedResource` に含める。
- 配布は GitHub Actions（`.github/workflows/release.yml`）。`v*` タグの push でテスト → 2 種の publish → GitHub Release に exe と `SHA256SUMS.txt` を添付する。タグと csproj の `<Version>` が一致しないと失敗する。

### 5.1 ライセンス表記・帰属表示

本アプリは MIT。同梱する第三者成果物の義務は次の通りで、**どのライセンスでも「ライセンス文の同梱」は必要**、CC BY はさらに**利用者の目に見えるクレジット**が必要。

| 対象 | ライセンス | 求められること |
|---|---|---|
| MDI | Apache 2.0 | LICENSE 文と著作権表記を配布物に同梱 |
| Font Awesome Free（アイコン） | CC BY 4.0 | **目に見える場所**に (1) 作者名 Fonticons, Inc. (2) ライセンス名とリンク (3) 元の作品へのリンク https://fontawesome.com (4) 改変した場合はその旨。公式は「Free のファイルに帰属コメントが埋め込み済みなので通常利用では追加作業不要」としているが、exe 同梱では利用者がファイルを見られないため明示する |
| Font Awesome Free（フォント／コード） | SIL OFL 1.1／MIT | ライセンス文の同梱 |
| React、その他 npm / NuGet 依存、CloudflaredKit | MIT 等 | ライセンス文の同梱 |
| fxcommands（プロトコル部分を参考にした場合） | MIT | 参考にしたコードを含めるならライセンス文の同梱 |
| Unicode 絵文字 | 端末フォントを使うため対象外 | — |

実装:

- リポジトリ直下に `THIRD-PARTY-NOTICES.md` を置き、名称／作者／ライセンス／リンクを列挙する。npm 側は `license-checker` 系、NuGet 側は `dotnet-project-licenses` 等で生成し、アイコンセットの項は手書きで追加。
- 管理 UI に **About 画面**を設け、バージョン・本アプリのライセンス・`THIRD-PARTY-NOTICES.md` の内容を表示する（exe 配布なので利用者がリポジトリを見に来ない前提）。Font Awesome の CC BY クレジットはここに載せることで要件を満たす。
- `THIRD-PARTY-NOTICES.md` は EmbeddedResource にして About 画面がそれを読む形にし、二重管理を避ける。
- 今後 CC BY のセット（Twemoji 等）を追加する場合も同じ About 画面に追記するだけで対応できる。

## 6. ロードマップ（MVP の順序）

①〜⑥はすべて実装済み。残っている課題は [DevelopmentNote.ja.md](./DevelopmentNote.ja.md) §6。

1. **FxConsoleClient + エミュレータ + パーサ**（コンソールアプリで `e wave` が飛ぶところまで）
2. **Kestrel + デッキ UI**（LAN、トークン、QR、WebSocket、PWA）
3. **トレイ常駐 + 管理 UI**（プロファイル編集、インポート／エクスポート、ファイアウォール導線）
4. **Cloudflare トンネル**（TryCloudflare → 固定 URL）
5. コンソールログ表示、アイコン画像、自動起動などの磨き込み
6. **多言語対応**（§3.9。ブラウザ言語で自動判定＋設定で指定。日本語・英語）

順番は目安。⑤と⑥は独立しているので、必要に応じて入れ替えてよい。

## 7. リスクと対策

| リスク | 影響 | 対策 |
|---|---|---|
| コンソールプロトコルが FiveM 更新で変わる | 全機能停止 | `FxConsoleClient` に隔離、エミュレータでテスト、fxcommands の追従を監視 |
| Windows Firewall で LAN から届かない | 主要ユースケース不成立 | ルール追加導線、接続診断 |
| トークン漏洩（URL 共有、スクショ） | 第三者がゲームにコマンド送信 | URL からの即時除去、再発行、レート制限、管理 UI の localhost 限定 |
| TryCloudflare の URL が毎回変わる | 別ネットワーク利用時に毎回 QR を読み直す手間 | 起動時に QR 自動更新、固定 URL はオプションで |
| 単一 exe のサイズ | 配布の重さ | 許容。将来的にフレームワーク依存版も並行配布可 |

## 8. 決定事項・未決事項

決定済み:

- アプリ名: **FxDeck**（リポジトリ／フォルダ名は当面据え置き）
- デッキ UI の既定ポート: **20200**
- ライセンス: **MIT**
- トンネルの位置づけ: 「スマホと PC が別ネットワーク」の救済。既定 OFF
- アイコン: MDI + Font Awesome Free をアイコンフォント形式で同梱、Unicode 絵文字は端末フォントで描画。ユーザー画像は PNG / JPEG / WebP / GIF（SVG は初期非対応）。エクスポートはプロファイル単位／全体を選んで zip（`.fxdeck`）
- ライセンス表記: `THIRD-PARTY-NOTICES.md` + 管理 UI の About 画面で一元表示（CC BY の帰属表示もここで満たす）
- 押す／離す・ステージ（§3.2）: fxcommands の On Press / On Release と Staged buttons に合わせる。fxcommands と意図的に変える点は (1) ステージはサーバー側で持ち全端末で共有、永続化しない (2) 失敗したらステージを進めない (3) WebSocket 切断時に押しっぱなしのキーの `releaseCommand` を送る、の 3 つ

未決:

- （なし）
