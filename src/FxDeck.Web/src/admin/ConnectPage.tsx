import { useEffect, useState, type ReactNode } from "react";
import { useT } from "../shared/i18n";
import { api, type FirewallStatus, type TunnelStatus } from "./api";
import { useAdminStore } from "./store";

const DECK_SEEN_KEY = "fxdeck.admin.deckSeen";
const CHECKLIST_OPEN_KEY = "fxdeck.admin.checklistOpen";

/** Home screen: QR codes, troubleshooting and the first-run checklist (UIUX §5.2). */
export function ConnectPage() {
  const t = useT();
  const status = useAdminStore((s) => s.status);
  const [qrVersion, setQrVersion] = useState(() => Date.now());
  const [firewall, setFirewall] = useState<FirewallStatus | null>(null);
  const [firewallBusy, setFirewallBusy] = useState(false);
  const [firewallMessage, setFirewallMessage] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [deckSeen, setDeckSeen] = useState(() => read(DECK_SEEN_KEY) === "1");
  const [checklistOpen] = useState(() => read(CHECKLIST_OPEN_KEY) !== "0");

  const refreshFirewall = () => api.firewallStatus().then(setFirewall).catch(() => setFirewall(null));
  useEffect(() => {
    void refreshFirewall();
  }, []);

  useEffect(() => {
    if (status && status.connectedDecks > 0 && !deckSeen) {
      setDeckSeen(true);
      write(DECK_SEEN_KEY, "1");
    }
  }, [status, deckSeen]);

  const allow = async () => {
    setFirewallBusy(true);
    setFirewallMessage(null);
    try {
      const result = await api.firewallAllow();
      setFirewallMessage(
        result.outcome === "added"
          ? t("connect.firewall.added", { port: result.port })
          : result.outcome === "cancelled"
            ? t("connect.firewall.cancelled")
            : t("connect.firewall.failed", { message: result.message ?? t("connect.firewall.unknownError") }),
      );
      await refreshFirewall();
    } catch (error) {
      setFirewallMessage(t("common.failed", { message: error instanceof Error ? error.message : String(error) }));
    } finally {
      setFirewallBusy(false);
    }
  };

  const copy = async () => {
    if (!status?.deckUrl) return;
    try {
      await navigator.clipboard.writeText(status.deckUrl);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      prompt(t("common.copyPrompt"), status.deckUrl);
    }
  };

  const gameDone = status?.game === "connected";
  const firewallDone = firewall?.portAllowed ?? false;
  const allDone = gameDone && firewallDone && deckSeen;

  return (
    <div className="page connect-page">
      <h2>{t("connect.title")}</h2>
      <div className="qr-row">
        <div className="qr-card">
          <h3>{t("connect.lan.title")}</h3>
          {status?.deckUrl ? (
            <>
              <img className="qr" src={api.qrUrl(qrVersion)} alt={t("connect.lan.qrAlt")} width={220} height={220} onError={() => setTimeout(() => setQrVersion(Date.now()), 2000)} />
              <div className="url">{status.deckUrlWithoutToken}</div>
              <div className="row">
                <button type="button" onClick={() => void copy()}>
                  {copied ? t("common.copied") : t("common.copyUrl")}
                </button>
                <button type="button" className="ghost" onClick={() => setQrVersion(Date.now())}>
                  {t("connect.refreshQr")}
                </button>
              </div>
              <p className="muted small">{t("connect.tokenWarning")}</p>
            </>
          ) : (
            <p className="warning">{t("connect.noLan")}</p>
          )}
        </div>
        <TunnelCard />
      </div>

      <details className="troubleshoot" open>
        <summary>{t("connect.trouble.title")}</summary>
        <ul>
          <li>
            {t("connect.trouble.firewall")}{" "}
            {firewall === null ? (
              <span className="muted">{t("connect.firewall.unknown")}</span>
            ) : firewall.portAllowed ? (
              <span className="ok">{t("connect.firewall.allowed", { port: firewall.port })}</span>
            ) : firewall.blocked ? (
              <span className="warning">{t("connect.firewall.blocked")}</span>
            ) : (
              <span className="warning">{t("connect.firewall.notAllowed", { port: firewall.port })}</span>
            )}{" "}
            <button type="button" onClick={() => void allow()} disabled={firewallBusy}>
              {t("connect.firewall.allow")}
            </button>
            {firewallMessage && <span className="hint"> {firewallMessage}</span>}
          </li>
          <li>{t("connect.trouble.network")}</li>
          <li>{t("connect.trouble.tunnel")}</li>
        </ul>
      </details>

      <details className="checklist" open={checklistOpen || !allDone} onToggle={(e) => write(CHECKLIST_OPEN_KEY, (e.target as HTMLDetailsElement).open ? "1" : "0")}>
        <summary>
          {t("connect.setup.title")}
          {allDone && <span className="ok">{t("connect.setup.done")}</span>}
        </summary>
        <ul className="checks">
          <li className={gameDone ? "done" : ""}>
            <span className="check" aria-hidden="true">
              {gameDone ? "☑" : "☐"}
            </span>
            {t("connect.setup.game")} <span className="muted small">{t("connect.setup.gameHint", { endpoint: status?.gameEndpoint ?? "" })}</span>
          </li>
          <li className={firewallDone ? "done" : ""}>
            <span className="check" aria-hidden="true">
              {firewallDone ? "☑" : "☐"}
            </span>
            {t("connect.setup.firewall")}
            <button type="button" onClick={() => void allow()} disabled={firewallBusy || firewallDone}>
              {t("connect.firewall.allow")}
            </button>
          </li>
          <li className={deckSeen ? "done" : ""}>
            <span className="check" aria-hidden="true">
              {deckSeen ? "☑" : "☐"}
            </span>
            {t("connect.setup.scan")} <span className="muted small">{t("connect.setup.scanHint", { count: status?.connectedDecks ?? 0 })}</span>
          </li>
        </ul>
      </details>
    </div>
  );
}

/** "From another network" card: the tunnel QR with start/stop and phase-specific errors (UIUX §5.2, §7). */
function TunnelCard() {
  const t = useT();
  const tunnel = useAdminStore((s) => s.status?.tunnel ?? null);
  const refreshStatus = useAdminStore((s) => s.refreshStatus);
  const [busy, setBusy] = useState<"start" | "stop" | null>(null);
  const [qrVersion, setQrVersion] = useState(() => Date.now());
  const [copied, setCopied] = useState(false);

  // The QR is fetched by URL; refresh it whenever the tunnel URL changes.
  useEffect(() => {
    setQrVersion(Date.now());
  }, [tunnel?.url]);

  const start = async () => {
    setBusy("start");
    try {
      await api.tunnelStart();
    } catch {
      // A failed start answers 502 with the state; the status refresh below shows it.
    } finally {
      await refreshStatus();
      setBusy(null);
    }
  };

  const stop = async () => {
    setBusy("stop");
    try {
      await api.tunnelStop();
    } finally {
      await refreshStatus();
      setBusy(null);
    }
  };

  const copy = async () => {
    if (!tunnel?.deckUrl) return;
    try {
      await navigator.clipboard.writeText(tunnel.deckUrl);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      prompt(t("common.copyPrompt"), tunnel.deckUrl);
    }
  };

  const status = tunnel?.status ?? "stopped";
  const starting = status === "starting" || busy === "start";
  const activeMode = tunnel?.activeMode ?? (tunnel?.mode === "named" ? "named" : "try");
  const phase = (tunnel?.error?.phase ?? "start") as NonNullable<TunnelStatus["error"]>["phase"];

  let body: ReactNode;
  if (status === "running" && tunnel?.url) {
    body = (
      <>
        <img className="qr" src={api.qrUrl(qrVersion, "tunnel")} alt={t("connect.tunnel.qrAlt")} width={220} height={220} onError={() => setTimeout(() => setQrVersion(Date.now()), 2000)} />
        <div className="url">{tunnel.url}/</div>
        <div className="row">
          <button type="button" onClick={() => void copy()}>
            {copied ? t("common.copied") : t("common.copyUrl")}
          </button>
          <button type="button" className="ghost" onClick={() => void stop()} disabled={busy !== null}>
            {t("connect.tunnel.stop")}
          </button>
        </div>
        <p className="muted small">
          {t("connect.tunnel.running", { mode: t(`connect.tunnel.mode.${activeMode}`) })}
          {activeMode === "try" ? t("connect.tunnel.urlChanges") : ""}
          {t("connect.tunnel.tokenWarning")}
        </p>
      </>
    );
  } else if (status === "running") {
    body = (
      <>
        <div className="qr placeholder">{t("connect.tunnel.noUrl")}</div>
        <button type="button" className="ghost" onClick={() => void stop()} disabled={busy !== null}>
          {t("connect.tunnel.stop")}
        </button>
        <p className="warning small">{t("connect.tunnel.noUrlHint")}</p>
      </>
    );
  } else if (starting) {
    body = (
      <>
        <div className="qr placeholder">{t("connect.tunnel.starting")}</div>
        <button type="button" disabled>
          {t("connect.tunnel.start")}
        </button>
        <p className="muted small">{t("connect.tunnel.startingHint")}</p>
      </>
    );
  } else if (status === "error" && tunnel?.error) {
    body = (
      <>
        <div className="qr placeholder failed">{t(`connect.tunnel.error.${phase}`)}</div>
        <div className="row">
          <button type="button" onClick={() => void start()} disabled={busy !== null}>
            {t("common.retry")}
          </button>
          <button type="button" className="ghost" onClick={() => void stop()} disabled={busy !== null}>
            {t("common.close")}
          </button>
        </div>
        <p className="warning small">{tunnel.error.message}</p>
      </>
    );
  } else {
    body = (
      <>
        <div className="qr placeholder">{t("connect.tunnel.stopped")}</div>
        <button type="button" onClick={() => void start()} disabled={busy !== null || tunnel === null}>
          {t("connect.tunnel.start")}
        </button>
        <p className="muted small">
          {t("connect.tunnel.intro")}
          {tunnel?.mode === "named" ? t("connect.tunnel.startsNamed") : t("connect.tunnel.startsTry")}
        </p>
      </>
    );
  }

  return (
    <div className={`qr-card tunnel-card${status === "running" || status === "error" ? "" : " disabled"}`}>
      <h3>{t("connect.tunnel.title")}</h3>
      {body}
    </div>
  );
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* ignore */
  }
}
