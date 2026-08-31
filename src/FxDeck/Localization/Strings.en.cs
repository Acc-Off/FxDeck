namespace FxDeck.Localization;

/// <summary>English.</summary>
public static partial class Strings
{
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // --- configuration validation (ConfigValidator) ---
        ["validator.emptyConfig"] = "The configuration is empty.",
        ["validator.unsupportedVersion"] = "Unsupported configuration version: {0}",
        ["validator.noSettings"] = "settings is missing.",
        ["validator.gameHostEmpty"] = "The game host is empty.",
        ["validator.gamePortInvalid"] = "Invalid game port: {0}",
        ["validator.deckPortInvalid"] = "Invalid deck port: {0}",
        ["validator.adminPortInvalid"] = "Invalid admin port: {0}",
        ["validator.themeInvalid"] = "Invalid theme: {0}",
        ["validator.languageInvalid"] = "Invalid language: {0}",
        ["validator.tunnelModeInvalid"] = "Invalid tunnel mode: {0}",
        ["validator.tunnelUrlInvalid"] = "The tunnel's fixed URL must look like https://…: {0}",
        ["validator.profileByIndex"] = "Profile #{0}",
        ["validator.profileByName"] = "Profile \"{0}\"",
        ["validator.profileIdEmpty"] = "{0}: the id is empty.",
        ["validator.profileIdDuplicate"] = "{0}: duplicate id.",
        ["validator.profileNameEmpty"] = "{0}: the name is empty.",
        ["validator.columnsRange"] = "{0}: columns must be 1–{1}.",
        ["validator.rowsRange"] = "{0}: rows must be 1–{1}.",
        ["validator.keyLabel"] = "{0}, key \"{1}\"",
        ["validator.keyIdEmpty"] = "{0}: the id is empty.",
        ["validator.keyIdDuplicate"] = "{0}: duplicate id.",
        ["validator.keyOutside"] = "{0}: outside the grid ({1},{2}).",
        ["validator.keyOverlap"] = "{0}: more than one key in the same cell ({1},{2}).",
        ["validator.keyNoTitle"] = "{0}: title is missing.",
        ["validator.titlePositionInvalid"] = "{0}: invalid title position: {1}",
        ["validator.backgroundEmpty"] = "{0}: the background colour is empty.",
        ["validator.actionUnsupported"] = "{0}: unsupported action: {1}",
        ["validator.iconTypeUnsupported"] = "{0}: unsupported icon type: {1}",
        ["validator.iconNameEmpty"] = "{0}: the icon name is empty.",
        ["validator.faStyleInvalid"] = "{0}: invalid Font Awesome style: {1}",
        ["validator.emojiEmpty"] = "{0}: the emoji is empty.",
        ["validator.imageRefInvalid"] = "{0}: invalid image reference.",
        ["validator.stageLabel"] = "stage {1} of {0}",
        ["validator.stageEmpty"] = "{0}: is empty.",
        ["validator.tooManyStages"] = "{0}: at most {1} stages are allowed.",

        // --- .fxdeck import (ConfigPackage) ---
        ["package.needConfigForAll"] = "Importing everything needs a config.json (JSON with settings and profiles). For a single profile choose \"Import a profile\".",
        ["package.unsupportedVersion"] = "Unsupported configuration version: {0}",
        ["package.noProfiles"] = "No profiles were found.",
        ["package.zipMissingEntries"] = "The zip contains neither {0} nor {1}.",
        ["package.jsonTooLarge"] = "The JSON is too large.",
        ["package.notJson"] = "{0} is not readable as JSON: {1}",
        ["package.notObject"] = "{0} has the wrong shape (not an object).",
        ["package.emptyConfig"] = "The configuration is empty.",
        ["package.emptyProfile"] = "The profile is empty.",
        ["package.invalidContent"] = "{0} has invalid content: {1}",
        ["package.notFxDeck"] = "{0} is neither an FxDeck profile nor a configuration (profiles or keys required).",
        ["package.missingImages"] = "{0} button(s) lost their image and will show their label instead.",

        // --- images (AssetStore) ---
        ["asset.notImage"] = "Not a readable image (PNG / JPEG / GIF are supported).",

        // --- admin API ---
        ["api.importModeInvalid"] = "mode must be profile or all.",
        ["api.fileTooLarge"] = "The file is too large (32 MB max).",
        ["api.importInvalid"] = "The imported content has problems.",
        ["api.imageTooLarge"] = "The image is too large (16 MB max).",
        ["api.portInvalid"] = "Invalid port.",
        ["api.gameTestOk"] = "Connected to {0}:{1}.",
        ["api.gameTestFailed"] = "Could not connect to {0}:{1}. Check that FiveM is running.",
        ["api.commands.gameNotRunning"] = "FiveM was not found.",
        ["api.commands.notInSession"] = "Join a server first, then try again.",
        ["api.commands.chatUnavailable"] = "Could not read the commands from this server's chat.",

        // --- tunnel (TunnelService) ---
        ["tunnel.tokenMissing"] = "No tunnel token is set for the fixed URL. Enter it under Settings → Tunnel.",
        ["tunnel.unsupported"] = "cloudflared cannot run on this machine: {0}",
        ["tunnel.downloadFailed"] = "Could not download cloudflared. Check the internet connection (GitHub must be reachable). ({0})",
        ["tunnel.downloadFailedGeneric"] = "Could not download cloudflared: {0}",
        ["tunnel.timeout"] = "cloudflared did not report a public URL in time. Check that the network or security software is not blocking it.",
        ["tunnel.exitedNamed"] = "cloudflared exited right after starting. Check that the tunnel token is correct. ({0})",
        ["tunnel.exitedOnStart"] = "cloudflared exited right after starting. ({0})",
        ["tunnel.startFailed"] = "Could not start cloudflared: {0}",
        ["tunnel.exitedUnexpectedly"] = "cloudflared exited unexpectedly (exit code {0}).",

        // --- tray ---
        ["tray.openAdmin"] = "&Open admin page",
        ["tray.copyDeckUrl"] = "&Copy deck URL",
        ["tray.openDataDir"] = "Open &data folder",
        ["tray.tunnelStart"] = "Start &tunnel",
        ["tray.tunnelStop"] = "Stop &tunnel",
        ["tray.tunnelCopyUrl"] = "Copy tunnel &URL",
        ["tray.exit"] = "E&xit",
        ["tray.game.connected"] = "FiveM: connected",
        ["tray.game.connecting"] = "FiveM: connecting…",
        ["tray.game.disconnected"] = "FiveM: not connected",
        ["tray.tunnel.starting"] = "Tunnel: starting…",
        ["tray.tunnel.running"] = "Tunnel: running {0}",
        ["tray.tunnel.noUrl"] = "(no public URL set)",
        ["tray.tunnel.error"] = "Tunnel: failed (see the admin page)",
        ["tray.tunnel.stopped"] = "Tunnel: stopped",
        ["tray.started"] = "FxDeck is running. Double-click to open the admin page.",
        ["tray.browserFailed"] = "Could not open the browser. Open {0} manually.",
        ["tray.noLan"] = "No LAN IP address found.",
        ["tray.openDataDirFailed"] = "Could not open the folder. Open {0} manually.",
        ["tray.deckUrlCopied"] = "Deck URL copied (it contains the access token — handle with care).",
        ["tray.tunnelStarting"] = "Starting the tunnel. The first start downloads cloudflared and takes a while.",
        ["tray.tunnelRunning"] = "The tunnel is running: {0}",
        ["tray.tunnelFailed"] = "Could not start the tunnel: {0}",
        ["tray.tunnelUrlCopied"] = "Tunnel URL copied (it contains the access token — handle with care).",

        // --- program / console ---
        ["program.unknownArg"] = "Unknown argument: {0}",
        ["program.needsValue"] = "{0} needs a value.",
        ["program.alreadyRunning"] = "FxDeck is already running. Open the admin page from the tray icon.",
        ["program.portInUse"] = "Could not start because the port is in use.\n{0}\n\nChange deckPort in config.json or pass --deck-port.",
        ["program.startFailed"] = "Start-up failed.\n{0}",
        ["program.banner.config"] = "Config file : {0}",
        ["program.banner.admin"] = "Admin page  : {0}",
        ["program.banner.noLan"] = "No LAN IPv4 address found. Check the Wi-Fi / Ethernet connection.",
        ["program.banner.deckUrl"] = "Deck URL    : {0}",
        ["program.banner.exit"] = "Exit from the tray icon.",
        ["program.send.connecting"] = "FxDeck — connecting to {0}:{1}",
        ["program.send.timeout"] = "Could not connect to the game ({0}:{1}, waited {2} s). Check that FiveM or the emulator is running.",
        ["program.send.ok"] = "[ok] sent {0} step(s)",
        ["program.send.failed"] = "[failed] {0} ({1}/{2} steps completed){3}",
        ["program.state.connected"] = "connected",
        ["program.state.connecting"] = "connecting…",
        ["program.state.disconnected"] = "not connected",
        ["program.usage"] = """
            Usage: FxDeck [options]

              --console          Print the deck URL, QR code and log to the calling terminal
              --host <ip>        Game console host (default: config.json, initially 127.0.0.1)
              --port <port>      Game console port (initially 29200)
              --deck-port <port> Deck UI port (initially 20200)
              --admin-port <port> Admin UI port (initially automatic)
              --data-dir <dir>   Data directory (default %LOCALAPPDATA%\FxDeck; FXDECK_DATA_DIR works too)
              --send "<macro>"   Send one macro without starting the web server, then exit
              --timeout <ms>     How long --send waits for the game (default 10000)
              -v                 Verbose log

            Without arguments FxDeck stays in the system tray.
            """,
    };
}
