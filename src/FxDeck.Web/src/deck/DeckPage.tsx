import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useI18nStore, useT } from "../shared/i18n";
import { ConsoleDrawer } from "./ConsoleDrawer";
import { Grid } from "./Grid";
import { InstallBanner } from "./InstallBanner";
import { useTheme, useWakeLock } from "./hooks";
import { DeckSocket } from "./socket";
import { useDeckStore } from "./store";

export function DeckPage() {
  const socketRef = useRef<DeckSocket | null>(null);
  const socket = useDeckStore((s) => s.socket);
  const closedSince = useDeckStore((s) => s.closedSince);
  const game = useDeckStore((s) => s.game);
  const profiles = useDeckStore((s) => s.profiles);
  const settings = useDeckStore((s) => s.settings);
  const currentProfileId = useDeckStore((s) => s.currentProfileId);
  const running = useDeckStore((s) => s.running);
  const stages = useDeckStore((s) => s.stages);
  const flash = useDeckStore((s) => s.flash);
  const toast = useDeckStore((s) => s.toast);
  const selectProfile = useDeckStore((s) => s.selectProfile);
  const stepProfile = useDeckStore((s) => s.stepProfile);
  const showToast = useDeckStore((s) => s.showToast);
  const consoleLines = useDeckStore((s) => s.consoleLines);
  const consoleOpen = useDeckStore((s) => s.consoleOpen);
  const setConsoleOpen = useDeckStore((s) => s.setConsoleOpen);
  const setLanguage = useI18nStore((s) => s.setLanguage);
  const t = useT();

  useTheme(settings.theme);
  useWakeLock();

  // The language is a shared setting pushed by the PC (design memo §3.9); "auto" follows this phone's browser.
  useEffect(() => setLanguage(settings.language ?? "auto"), [settings.language, setLanguage]);

  useEffect(() => {
    const deckSocket = new DeckSocket();
    socketRef.current = deckSocket;
    deckSocket.start();
    const onVisibility = () => {
      if (document.visibilityState === "visible") deckSocket.poke();
    };
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("online", onVisibility);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("online", onVisibility);
      deckSocket.stop();
      socketRef.current = null;
    };
  }, []);

  const profile = useMemo(() => profiles.find((p) => p.id === currentProfileId) ?? null, [profiles, currentProfileId]);

  const onPress = useCallback(
    (keyId: string) => {
      const sent = socketRef.current?.press(keyId) ?? false;
      if (!sent && useDeckStore.getState().socket !== "open") showToast(t("deck.notConnectedToPc"));
      return sent;
    },
    [showToast, t],
  );
  const onRelease = useCallback((keyId: string) => {
    socketRef.current?.release(keyId);
  }, []);

  const gameClass = game === "connected" ? "game-on" : game === "connecting" ? "game-connecting" : "game-off";

  return (
    <div className={`deck ${gameClass}${consoleOpen ? " console-open" : ""}`}>
      {settings.deckStatusBar && <StatusBar game={game} profileName={profile?.name ?? ""} consoleOpen={consoleOpen} onToggleConsole={() => setConsoleOpen(!consoleOpen)} />}
      {profiles.length === 0 ? (
        <div className="empty-state">
          <p>{socket === "open" ? t("deck.noProfiles") : ""}</p>
        </div>
      ) : profile ? (
        profile.keys.length === 0 ? (
          <div className="empty-state">
            <h2>{profile.name}</h2>
            <p>{t("deck.noKeys")}</p>
          </div>
        ) : (
          <Grid profile={profile} running={running} stages={stages} flash={flash} onPress={onPress} onRelease={onRelease} onSwipe={stepProfile} />
        )
      ) : null}
      {profiles.length > 1 && (
        <div className="dots" role="tablist">
          {profiles.map((p) => (
            <button
              key={p.id}
              type="button"
              role="tab"
              aria-selected={p.id === currentProfileId}
              aria-label={p.name}
              className={`dot ${p.id === currentProfileId ? "active" : ""}`}
              onClick={() => selectProfile(p.id)}
            />
          ))}
        </div>
      )}
      {!settings.deckStatusBar && !consoleOpen && (
        <button type="button" className="console-handle" onClick={() => setConsoleOpen(true)} aria-label={t("deck.console.open")}>
          &gt;_
        </button>
      )}
      {consoleOpen && <ConsoleDrawer lines={consoleLines} onClose={() => setConsoleOpen(false)} />}
      {toast && <div className="toast">{toast.text}</div>}
      {socket === "invalid" && (
        <div className="overlay">
          <h2>{t("deck.invalid.title")}</h2>
          <p>{t("deck.invalid.body")}</p>
        </div>
      )}
      {(socket === "closed" || socket === "connecting") && profiles.length === 0 && <ConnectingOverlay closedSince={closedSince} />}
      {socket === "closed" && profiles.length > 0 && <ConnectingOverlay closedSince={closedSince} />}
      <InstallBanner />
    </div>
  );
}

function StatusBar({ game, profileName, consoleOpen, onToggleConsole }: { game: string; profileName: string; consoleOpen: boolean; onToggleConsole(): void }) {
  const t = useT();
  const label = game === "connected" ? t("deck.status.connected") : game === "connecting" ? t("deck.status.connecting") : t("deck.status.disconnected");
  return (
    <div className={`statusbar ${game}`}>
      <span className="status-dot" aria-hidden="true" />
      <span className="status-text">{label}</span>
      <span className="status-profile">{profileName}</span>
      <button type="button" className={`status-console ${consoleOpen ? "active" : ""}`} onClick={onToggleConsole} aria-pressed={consoleOpen} aria-label={t("deck.console")}>
        &gt;_
      </button>
    </div>
  );
}

function ConnectingOverlay({ closedSince }: { closedSince: number | null }) {
  const t = useT();
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    const update = () => setSeconds(closedSince === null ? 0 : Math.floor((performance.now() - closedSince) / 1000));
    update();
    const timer = window.setInterval(update, 1000);
    return () => window.clearInterval(timer);
  }, [closedSince]);
  return (
    <div className="overlay">
      <h2>{t("deck.reconnecting")}</h2>
      <p>{seconds > 0 ? t("deck.reconnectingFor", { seconds }) : t("deck.connectingToPc")}</p>
      <p className="hint">{t("app.unreachable.body")}</p>
    </div>
  );
}
