import { useEffect, useRef, useState } from "react";
import { useT } from "../shared/i18n";
import { api, type Adapters } from "./api";
import { formatExtractedAt } from "./commandAssist";
import { useAdminStore } from "./store";

/** One long page grouped like UIUX §5.5. */
export function SettingsPage() {
  const t = useT();
  const config = useAdminStore((s) => s.config);
  const status = useAdminStore((s) => s.status);
  const update = useAdminStore((s) => s.update);
  const load = useAdminStore((s) => s.load);
  const restartRequired = useAdminStore((s) => s.restartRequired);
  const commandCache = useAdminStore((s) => s.commandCache);
  const extractCommands = useAdminStore((s) => s.extractCommands);
  const clearCommands = useAdminStore((s) => s.clearCommands);
  const [adapters, setAdapters] = useState<Adapters | null>(null);
  const [gameTest, setGameTest] = useState<string | null>(null);
  const [autoStart, setAutoStart] = useState<boolean | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [extracting, setExtracting] = useState(false);
  const [extractError, setExtractError] = useState<string | null>(null);
  const importAll = useRef<HTMLInputElement>(null);
  const importProfile = useRef<HTMLInputElement>(null);

  useEffect(() => {
    api.adapters().then(setAdapters).catch(() => setAdapters(null));
    api
      .autoStart()
      .then((r) => setAutoStart(r.enabled))
      .catch(() => setAutoStart(null));
  }, []);

  if (!config) return null;
  const s = config.settings;
  const errorMessage = (error: unknown) => (error instanceof Error ? error.message : String(error));

  const testGame = async () => {
    setGameTest(t("settings.game.testing"));
    try {
      const result = await api.gameTest(s.game.host, s.game.port);
      setGameTest(result.message);
    } catch (error) {
      setGameTest(errorMessage(error));
    }
  };

  const rotate = async () => {
    if (!confirm(t("settings.security.rotateConfirm"))) return;
    await api.rotateToken();
    setMessage(t("settings.security.rotated"));
  };

  const toggleAutoStart = async (enabled: boolean) => {
    try {
      const result = await api.setAutoStart(enabled);
      setAutoStart(result.enabled);
      await load();
    } catch (error) {
      setMessage(t("settings.app.autoStartFailed", { message: errorMessage(error) }));
    }
  };

  const doImport = async (file: File, mode: "all" | "profile") => {
    if (mode === "all" && !confirm(t("settings.data.importAllConfirm"))) return;
    try {
      const result = await api.import(file, mode);
      setMessage(t("settings.data.imported", { count: result.profilesAdded, warnings: result.warnings.join(" ") }));
      await load();
    } catch (error) {
      setMessage(t("settings.data.importFailed", { message: errorMessage(error) }));
    }
  };

  const prune = async () => {
    if (!confirm(t("settings.data.pruneConfirm"))) return;
    try {
      const result = await api.pruneAssets();
      setMessage(result.deleted === 0 ? t("settings.data.prunedNone") : t("settings.data.pruned", { count: result.deleted }));
    } catch (error) {
      setMessage(t("settings.data.pruneFailed", { message: errorMessage(error) }));
    }
  };

  const restart = async () => {
    if (!confirm(t("settings.restartConfirm"))) return;
    await api.restart();
    setMessage(t("settings.restarting"));
  };

  const extract = async () => {
    setExtracting(true);
    setExtractError(null);
    try {
      await extractCommands();
    } catch (error) {
      setExtractError(errorMessage(error));
    } finally {
      setExtracting(false);
    }
  };

  const clearExtracted = async () => {
    setExtractError(null);
    try {
      await clearCommands();
    } catch (error) {
      setExtractError(errorMessage(error));
    }
  };

  const gameState = status?.game === "connected" ? t("settings.game.state.connected") : status?.game === "connecting" ? t("settings.game.state.connecting") : t("settings.game.state.disconnected");

  return (
    <div className="page settings-page">
      <h2>{t("settings.title")}</h2>
      {restartRequired && (
        <div className="banner-restart">
          {t("settings.restartBanner")}
          <button type="button" onClick={() => void restart()}>
            {t("settings.restart")}
          </button>
        </div>
      )}
      {message && <p className="hint">{message}</p>}

      <section>
        <h3>{t("settings.game.title")}</h3>
        <div className="row">
          <label>
            {t("settings.game.host")}
            <input type="text" value={s.game.host} onChange={(e) => update((c) => (c.settings.game.host = e.target.value))} />
          </label>
          <label>
            {t("settings.game.port")}
            <input type="number" min={1} max={65535} value={s.game.port} onChange={(e) => update((c) => (c.settings.game.port = Number(e.target.value)))} />
          </label>
          <button type="button" onClick={() => void testGame()}>
            {t("settings.game.test")}
          </button>
        </div>
        {gameTest && <p className="hint">{gameTest}</p>}
        <p className="muted small">{t("settings.game.hint", { state: gameState })}</p>
      </section>

      <section>
        <h3>{t("settings.deck.title")}</h3>
        <div className="row">
          <label>
            {t("settings.deck.port")}
            <input type="number" min={1} max={65535} value={s.deckPort} onChange={(e) => update((c) => (c.settings.deckPort = Number(e.target.value)))} />
          </label>
          <label>
            {t("settings.deck.adapter")}
            <select value={s.lanAdapter ?? ""} onChange={(e) => update((c) => (c.settings.lanAdapter = e.target.value || null))}>
              <option value="">
                {t("settings.deck.auto")}
                {adapters?.automatic ? `（${adapters.automatic}）` : ""}
              </option>
              {adapters?.adapters.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name} — {a.address}
                  {a.hasGateway ? "" : t("settings.deck.noGateway")}
                </option>
              ))}
            </select>
          </label>
        </div>
        <p className="muted small">{t("settings.deck.hint")}</p>
      </section>

      <section>
        <h3>{t("settings.tunnel.title")}</h3>
        <div className="row">
          <label>
            {t("settings.tunnel.mode")}
            <select value={s.tunnel.mode} onChange={(e) => update((c) => (c.settings.tunnel.mode = e.target.value as typeof s.tunnel.mode))}>
              <option value="off">{t("settings.tunnel.mode.off")}</option>
              <option value="try">{t("settings.tunnel.mode.try")}</option>
              <option value="named">{t("settings.tunnel.mode.named")}</option>
            </select>
          </label>
          <label className="checkbox">
            <input type="checkbox" checked={s.tunnel.autoStart} disabled={s.tunnel.mode === "off"} onChange={(e) => update((c) => (c.settings.tunnel.autoStart = e.target.checked))} />
            {t("settings.tunnel.autoStart")}
          </label>
        </div>
        {s.tunnel.mode === "named" && (
          <div className="row wide-fields">
            <label>
              {t("settings.tunnel.token")}
              <input type="password" autoComplete="off" value={s.tunnel.namedToken ?? ""} onChange={(e) => update((c) => (c.settings.tunnel.namedToken = e.target.value))} placeholder="eyJh…" />
            </label>
            <label>
              {t("settings.tunnel.url")}
              <input type="url" value={s.tunnel.namedUrl ?? ""} onChange={(e) => update((c) => (c.settings.tunnel.namedUrl = e.target.value || null))} placeholder="https://deck.example.com" />
            </label>
          </div>
        )}
        <p className="muted small">
          {s.tunnel.mode === "off" ? t("settings.tunnel.hint.off") : s.tunnel.mode === "try" ? t("settings.tunnel.hint.try") : t("settings.tunnel.hint.named")}
          {status?.tunnel.status === "running" && t("settings.tunnel.hint.running")}
          {" "}
          {t("settings.tunnel.downloadBefore")}
          {status?.dataDirectory ? <code>{status.dataDirectory}\cloudflared</code> : t("settings.tunnel.dataFolder")}
          {t("settings.tunnel.downloadAfter")}
        </p>
      </section>

      <section>
        <h3>{t("settings.security.title")}</h3>
        <button type="button" className="danger" onClick={() => void rotate()}>
          {t("settings.security.rotate")}
        </button>
        <p className="muted small">{t("settings.security.hint")}</p>
      </section>

      <section>
        <h3>{t("settings.app.title")}</h3>
        <label className="checkbox">
          <input type="checkbox" checked={autoStart ?? s.autoStart} disabled={autoStart === null} onChange={(e) => void toggleAutoStart(e.target.checked)} />
          {t("settings.app.autoStart")}
        </label>
        <div className="row">
          <label>
            {t("settings.app.theme")}
            <select value={s.theme} onChange={(e) => update((c) => (c.settings.theme = e.target.value as typeof s.theme))}>
              <option value="dark">{t("settings.app.theme.dark")}</option>
              <option value="light">{t("settings.app.theme.light")}</option>
              <option value="system">{t("settings.app.theme.system")}</option>
            </select>
          </label>
          <label>
            {t("settings.app.language")}
            <select value={s.language ?? "auto"} onChange={(e) => update((c) => (c.settings.language = e.target.value as typeof s.language))}>
              <option value="auto">{t("settings.app.language.auto")}</option>
              <option value="ja">{t("settings.app.language.ja")}</option>
              <option value="en">{t("settings.app.language.en")}</option>
            </select>
          </label>
          <label className="checkbox">
            <input type="checkbox" checked={s.deckStatusBar} onChange={(e) => update((c) => (c.settings.deckStatusBar = e.target.checked))} />
            {t("settings.app.statusBar")}
          </label>
        </div>
      </section>

      <section>
        <h3>{t("settings.assist.title")}</h3>
        <div className="row">
          <button type="button" onClick={() => void extract()} disabled={extracting}>
            {extracting ? t("assist.extracting") : t("settings.assist.extract")}
          </button>
          <button type="button" onClick={() => void clearExtracted()} disabled={!commandCache?.extractedAt}>
            {t("settings.assist.clear")}
          </button>
          <span className={extractError ? "error small" : "muted small"}>
            {extractError ??
              (commandCache?.extractedAt
                ? t("assist.status", { count: commandCache.count ?? commandCache.commands.length, time: formatExtractedAt(commandCache.extractedAt) })
                : t("settings.assist.none"))}
          </span>
        </div>
        <p className="muted small">{t("settings.assist.hint")}</p>
      </section>

      <section>
        <h3>{t("settings.data.title")}</h3>
        <div className="row">
          <a className="button" href={api.exportUrl()} download>
            {t("settings.data.export")}
          </a>
          <button type="button" onClick={() => importAll.current?.click()}>
            {t("settings.data.importAll")}
          </button>
          <button type="button" onClick={() => importProfile.current?.click()}>
            {t("settings.data.importProfile")}
          </button>
          <button type="button" onClick={() => void prune()}>
            {t("settings.data.prune")}
          </button>
        </div>
        <input ref={importAll} type="file" accept=".fxdeck,.json,.zip" hidden onChange={(e) => e.target.files?.[0] && void doImport(e.target.files[0], "all")} />
        <input ref={importProfile} type="file" accept=".fxdeck,.json,.zip" hidden onChange={(e) => e.target.files?.[0] && void doImport(e.target.files[0], "profile")} />
        <p className="muted small">
          {t("settings.data.configPath")}
          <code>{status?.configPath}</code>
          {t("settings.data.configHint")}
        </p>
      </section>
    </div>
  );
}
