namespace FxDeck.Localization;

/// <summary>Japanese — the source of truth: every key exists here. Adding a language = copy this file, translate, register it in <see cref="Strings"/>.</summary>
public static partial class Strings
{
    private static readonly Dictionary<string, string> Ja = new(StringComparer.Ordinal)
    {
        // --- configuration validation (ConfigValidator) ---
        ["validator.emptyConfig"] = "設定が空です。",
        ["validator.unsupportedVersion"] = "未対応の設定バージョンです: {0}",
        ["validator.noSettings"] = "settings がありません。",
        ["validator.gameHostEmpty"] = "ゲームのホストが空です。",
        ["validator.gamePortInvalid"] = "ゲームのポートが不正です: {0}",
        ["validator.deckPortInvalid"] = "デッキのポートが不正です: {0}",
        ["validator.adminPortInvalid"] = "管理ポートが不正です: {0}",
        ["validator.themeInvalid"] = "テーマが不正です: {0}",
        ["validator.languageInvalid"] = "言語が不正です: {0}",
        ["validator.tunnelModeInvalid"] = "トンネルモードが不正です: {0}",
        ["validator.tunnelUrlInvalid"] = "トンネルの固定 URL は https://… の形式で入力してください: {0}",
        ["validator.profileByIndex"] = "プロファイル #{0}",
        ["validator.profileByName"] = "プロファイル「{0}」",
        ["validator.profileIdEmpty"] = "{0}: id が空です。",
        ["validator.profileIdDuplicate"] = "{0}: id が重複しています。",
        ["validator.profileNameEmpty"] = "{0}: 名前が空です。",
        ["validator.columnsRange"] = "{0}: 列数は 1〜{1} です。",
        ["validator.rowsRange"] = "{0}: 行数は 1〜{1} です。",
        ["validator.keyLabel"] = "{0} のキー「{1}」",
        ["validator.keyIdEmpty"] = "{0}: id が空です。",
        ["validator.keyIdDuplicate"] = "{0}: id が重複しています。",
        ["validator.keyOutside"] = "{0}: グリッドの外にあります ({1},{2})。",
        ["validator.keyOverlap"] = "{0}: 同じマスに複数のキーがあります ({1},{2})。",
        ["validator.keyNoTitle"] = "{0}: title がありません。",
        ["validator.titlePositionInvalid"] = "{0}: タイトル位置が不正です: {1}",
        ["validator.backgroundEmpty"] = "{0}: 背景色が空です。",
        ["validator.actionUnsupported"] = "{0}: 未対応の動作です: {1}",
        ["validator.iconTypeUnsupported"] = "{0}: 未対応のアイコン種別です: {1}",
        ["validator.iconNameEmpty"] = "{0}: アイコン名が空です。",
        ["validator.faStyleInvalid"] = "{0}: Font Awesome のスタイルが不正です: {1}",
        ["validator.emojiEmpty"] = "{0}: 絵文字が空です。",
        ["validator.imageRefInvalid"] = "{0}: 画像の参照が不正です。",
        ["validator.stageLabel"] = "{0} のステージ {1}",
        ["validator.stageEmpty"] = "{0}: 内容がありません。",
        ["validator.tooManyStages"] = "{0}: ステージは最大 {1} つです。",

        // --- .fxdeck import (ConfigPackage) ---
        ["package.needConfigForAll"] = "全体のインポートには config.json（settings と profiles を含む JSON）が必要です。プロファイル単体の場合は「プロファイルを追加」を選んでください。",
        ["package.unsupportedVersion"] = "未対応の設定バージョンです: {0}",
        ["package.noProfiles"] = "プロファイルが見つかりませんでした。",
        ["package.zipMissingEntries"] = "zip に {0} も {1} も含まれていません。",
        ["package.jsonTooLarge"] = "JSON が大きすぎます。",
        ["package.notJson"] = "{0} を JSON として読めません: {1}",
        ["package.notObject"] = "{0} の形式が不正です（オブジェクトではありません）。",
        ["package.emptyConfig"] = "空の設定です。",
        ["package.emptyProfile"] = "空のプロファイルです。",
        ["package.invalidContent"] = "{0} の内容が不正です: {1}",
        ["package.notFxDeck"] = "{0} は FxDeck のプロファイルでも設定でもありません（profiles か keys が必要です）。",
        ["package.missingImages"] = "{0} 個のボタンの画像が見つからずラベル表示になります。",

        // --- images (AssetStore) ---
        ["asset.notImage"] = "画像として読み込めません（PNG / JPEG / GIF に対応しています）。",

        // --- admin API ---
        ["api.importModeInvalid"] = "mode は profile か all です。",
        ["api.fileTooLarge"] = "ファイルが大きすぎます（32MB まで）。",
        ["api.importInvalid"] = "インポートした内容に問題があります。",
        ["api.imageTooLarge"] = "画像が大きすぎます（16MB まで）。",
        ["api.portInvalid"] = "ポートが不正です。",
        ["api.gameTestOk"] = "{0}:{1} に接続できました。",
        ["api.gameTestFailed"] = "{0}:{1} に接続できませんでした。FiveM が起動しているか確認してください。",
        ["api.commands.gameNotRunning"] = "FiveM が見つかりません。",
        ["api.commands.notInSession"] = "サーバーに接続してから実行してください。",
        ["api.commands.chatUnavailable"] = "このサーバーのチャットからは取得できませんでした。",

        // --- tunnel (TunnelService) ---
        ["tunnel.tokenMissing"] = "固定 URL のトンネルトークンが設定されていません。「設定 → トンネル」で入力してください。",
        ["tunnel.unsupported"] = "この環境では cloudflared を利用できません: {0}",
        ["tunnel.downloadFailed"] = "cloudflared をダウンロードできませんでした。インターネット接続（GitHub への到達性）を確認してください。({0})",
        ["tunnel.downloadFailedGeneric"] = "cloudflared をダウンロードできませんでした: {0}",
        ["tunnel.timeout"] = "cloudflared が時間内に公開 URL を報告しませんでした。ネットワークやセキュリティソフトが cloudflared をブロックしていないか確認してください。",
        ["tunnel.exitedNamed"] = "cloudflared が起動直後に終了しました。トンネルトークンが正しいか確認してください。({0})",
        ["tunnel.exitedOnStart"] = "cloudflared が起動直後に終了しました。({0})",
        ["tunnel.startFailed"] = "cloudflared を起動できませんでした: {0}",
        ["tunnel.exitedUnexpectedly"] = "cloudflared が予期せず終了しました（終了コード {0}）。",

        // --- tray ---
        ["tray.openAdmin"] = "管理画面を開く(&O)",
        ["tray.copyDeckUrl"] = "デッキ URL をコピー(&C)",
        ["tray.openDataDir"] = "データフォルダを開く(&D)",
        ["tray.tunnelStart"] = "トンネルを開始(&T)",
        ["tray.tunnelStop"] = "トンネルを停止(&T)",
        ["tray.tunnelCopyUrl"] = "トンネル URL をコピー(&U)",
        ["tray.exit"] = "終了(&X)",
        ["tray.game.connected"] = "FiveM: 接続中",
        ["tray.game.connecting"] = "FiveM: 接続を試行中…",
        ["tray.game.disconnected"] = "FiveM: 未接続",
        ["tray.tunnel.starting"] = "トンネル: 開始しています…",
        ["tray.tunnel.running"] = "トンネル: 稼働中 {0}",
        ["tray.tunnel.noUrl"] = "（公開 URL 未設定）",
        ["tray.tunnel.error"] = "トンネル: 失敗（管理画面で確認）",
        ["tray.tunnel.stopped"] = "トンネル: 停止中",
        ["tray.started"] = "起動しました。ダブルクリックで管理画面を開きます。",
        ["tray.browserFailed"] = "ブラウザを開けませんでした。手動で {0} を開いてください。",
        ["tray.noLan"] = "LAN の IP アドレスが見つかりません。",
        ["tray.openDataDirFailed"] = "フォルダを開けませんでした。手動で {0} を開いてください。",
        ["tray.deckUrlCopied"] = "デッキ URL をコピーしました（トークンを含みます。取り扱いに注意）。",
        ["tray.tunnelStarting"] = "トンネルを開始しています。初回は cloudflared のダウンロードのため時間がかかります。",
        ["tray.tunnelRunning"] = "トンネルが稼働しています: {0}",
        ["tray.tunnelFailed"] = "トンネルを開始できませんでした: {0}",
        ["tray.tunnelUrlCopied"] = "トンネル URL をコピーしました（トークンを含みます。取り扱いに注意）。",

        // --- program / console ---
        ["program.unknownArg"] = "不明な引数です: {0}",
        ["program.needsValue"] = "{0} には値が必要です。",
        ["program.alreadyRunning"] = "FxDeck は既に起動しています。タスクトレイのアイコンから管理画面を開いてください。",
        ["program.portInUse"] = "ポートが使用中のため起動できませんでした。\n{0}\n\nconfig.json の deckPort を変えるか、--deck-port で別のポートを指定してください。",
        ["program.startFailed"] = "起動に失敗しました。\n{0}",
        ["program.banner.config"] = "設定ファイル : {0}",
        ["program.banner.admin"] = "管理画面     : {0}",
        ["program.banner.noLan"] = "LAN の IPv4 アドレスが見つかりませんでした。Wi-Fi／有線の接続を確認してください。",
        ["program.banner.deckUrl"] = "デッキ URL   : {0}",
        ["program.banner.exit"] = "タスクトレイのアイコンから終了できます。",
        ["program.send.connecting"] = "FxDeck — {0}:{1} に接続します",
        ["program.send.timeout"] = "ゲームに接続できませんでした（{0}:{1}、{2} 秒待機）。FiveM またはエミュレータが起動しているか確認してください。",
        ["program.send.ok"] = "[ok] {0} ステップを送信しました",
        ["program.send.failed"] = "[failed] {0}（{1}/{2} ステップ完了）{3}",
        ["program.state.connected"] = "接続済み",
        ["program.state.connecting"] = "接続中…",
        ["program.state.disconnected"] = "未接続",
        ["program.usage"] = """
            使い方: FxDeck [オプション]

              --console          呼び出し元の端末にデッキ URL・QR・ログを表示する
              --host <ip>        ゲームの接続先（既定 config.json の値、初期値 127.0.0.1）
              --port <port>      ゲームの接続先ポート（初期値 29200）
              --deck-port <port> デッキ UI のポート（初期値 20200）
              --admin-port <port> 管理 UI のポート（初期値 自動）
              --data-dir <dir>   設定の保存先（既定 %LOCALAPPDATA%\FxDeck、環境変数 FXDECK_DATA_DIR でも指定可）
              --send "<macro>"   Web サーバーを立てずにマクロを 1 回送って終了する
              --timeout <ms>     --send 時に接続を待つ時間（既定 10000）
              -v                 詳細ログ

            引数なしで起動するとタスクトレイに常駐します。
            """,
    };
}
