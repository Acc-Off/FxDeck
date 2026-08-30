import { useEffect, useState } from "react";
import { AdminApp } from "./admin/AdminApp";
import { DeckPage } from "./deck/DeckPage";
import { exchangeToken, type SessionExchange } from "./shared/api";
import { useI18nStore, useT } from "./shared/i18n";

type Route = { kind: "loading" } | { kind: "deck" } | { kind: "admin" } | { kind: "exchangeFailed"; result: SessionExchange };

// Until the server's language setting arrives, follow the browser.
useI18nStore.getState().setLanguage("auto");

/**
 * Entry: `/?t=<token>` (from the QR code) exchanges the token for the cookie and lands on /deck/,
 * `/deck/*` is the deck, `/admin/*` the admin UI.
 */
export function App() {
  const [route, setRoute] = useState<Route>({ kind: "loading" });
  const t = useT();

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const token = params.get("t");
    const path = location.pathname;

    if (token) {
      void exchangeToken(token).then((result) => {
        if (result === "ok") {
          history.replaceState(null, "", "/deck/");
          setRoute({ kind: "deck" });
        } else {
          history.replaceState(null, "", location.pathname); // never keep the token in the address bar
          setRoute({ kind: "exchangeFailed", result });
        }
      });
      return;
    }

    if (path.startsWith("/admin")) {
      setRoute({ kind: "admin" });
      return;
    }
    if (!path.startsWith("/deck")) history.replaceState(null, "", "/deck/");
    setRoute({ kind: "deck" });
  }, []);

  switch (route.kind) {
    case "loading":
      return <div className="overlay">{t("app.connecting")}</div>;
    case "deck":
      return <DeckPage />;
    case "admin":
      return <AdminApp />;
    case "exchangeFailed": {
      const kind = route.result === "invalid" ? "qrInvalid" : route.result === "rateLimited" ? "rateLimited" : "unreachable";
      return (
        <div className="overlay">
          <h2>{t(`app.${kind}.title`)}</h2>
          <p>{t(`app.${kind}.body`)}</p>
        </div>
      );
    }
  }
}
