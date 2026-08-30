import { useEffect, useState } from "react";
import { useTheme } from "../deck/hooks";
import { useI18nStore, useT, type MessageKey } from "../shared/i18n";
import { AboutPage } from "./AboutPage";
import { ConnectPage } from "./ConnectPage";
import { ProfilesPage } from "./ProfilesPage";
import { SettingsPage } from "./SettingsPage";
import { useAdminStore } from "./store";
import "./admin.css";

type Page = "connect" | "profiles" | "settings" | "about";

const PAGES: { id: Page; label: MessageKey }[] = [
  { id: "connect", label: "nav.connect" },
  { id: "profiles", label: "nav.profiles" },
  { id: "settings", label: "nav.settings" },
  { id: "about", label: "nav.about" },
];

function pageFromPath(): Page {
  const segment = location.pathname.replace(/^\/admin\/?/, "").split("/")[0];
  return PAGES.some((p) => p.id === segment) ? (segment as Page) : "connect";
}

/** Left nav + page (UIUX §5.1). Status is polled every 2 s. */
export function AdminApp() {
  const [page, setPage] = useState<Page>(pageFromPath);
  const config = useAdminStore((s) => s.config);
  const status = useAdminStore((s) => s.status);
  const loadError = useAdminStore((s) => s.loadError);
  const save = useAdminStore((s) => s.save);
  const saveErrors = useAdminStore((s) => s.saveErrors);
  const load = useAdminStore((s) => s.load);
  const refreshStatus = useAdminStore((s) => s.refreshStatus);
  const flush = useAdminStore((s) => s.flush);
  const setLanguage = useI18nStore((s) => s.setLanguage);
  const t = useT();

  useTheme(config?.settings.theme ?? "dark");
  const language = config?.settings.language;
  useEffect(() => {
    if (config) setLanguage(language ?? "auto");
  }, [config, language, setLanguage]);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void refreshStatus(), 2000);
    const onPop = () => setPage(pageFromPath());
    window.addEventListener("popstate", onPop);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener("popstate", onPop);
    };
  }, [load, refreshStatus]);

  const navigate = (target: Page) => {
    history.pushState(null, "", `/admin/${target}`);
    setPage(target);
  };

  const gameLabel = status?.game === "connected" ? t("admin.game.connected") : status?.game === "connecting" ? t("admin.game.connecting") : status ? t("admin.game.disconnected") : t("admin.game.offline");

  return (
    <div className="admin">
      <nav className="sidebar">
        <div className="brand">FxDeck</div>
        {PAGES.map((p) => (
          <a key={p.id} href={`/admin/${p.id}`} className={page === p.id ? "active" : ""} onClick={(e) => (e.preventDefault(), navigate(p.id))}>
            {t(p.label)}
          </a>
        ))}
        <div className="sidebar-footer">
          <span className={`game-dot ${status?.game ?? "offline"}`} aria-hidden="true" />
          <span>{gameLabel}</span>
          {status && status.connectedDecks > 0 && <span className="muted small">{t("admin.decks", { count: status.connectedDecks })}</span>}
        </div>
      </nav>
      <main className="content">
        {loadError && (
          <div className="error-box">
            <p>{t("admin.loadError", { message: loadError })}</p>
            <button type="button" onClick={() => void load()}>
              {t("common.retry")}
            </button>
          </div>
        )}
        {config && page === "connect" && <ConnectPage />}
        {config && page === "profiles" && <ProfilesPage />}
        {config && page === "settings" && <SettingsPage />}
        {page === "about" && <AboutPage />}
        <div className={`save-indicator ${save}`} role="status">
          {save === "saving" && t("admin.save.saving")}
          {save === "dirty" && t("admin.save.dirty")}
          {save === "saved" && t("admin.save.saved")}
          {save === "error" && (
            <span>
              {t("admin.save.error", { errors: saveErrors.join(" / ") })}{" "}
              <button type="button" onClick={() => void flush()}>
                {t("common.retry")}
              </button>
            </span>
          )}
        </div>
      </main>
    </div>
  );
}
