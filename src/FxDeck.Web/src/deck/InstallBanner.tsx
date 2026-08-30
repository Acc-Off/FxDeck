import { useEffect, useState } from "react";
import { useT } from "../shared/i18n";
import { isStandalone } from "./hooks";

const DISMISSED_KEY = "fxdeck.installBannerDismissed";

interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

/** "Add to home screen" hint, shown once (UIUX §4.9). */
export function InstallBanner() {
  const t = useT();
  const [visible, setVisible] = useState(false);
  const [prompt, setPrompt] = useState<BeforeInstallPromptEvent | null>(null);

  useEffect(() => {
    let dismissed = false;
    try {
      dismissed = localStorage.getItem(DISMISSED_KEY) === "1";
    } catch {
      /* storage unavailable */
    }
    if (dismissed || isStandalone()) return;
    setVisible(true);
    const onPrompt = (event: Event) => {
      event.preventDefault();
      setPrompt(event as BeforeInstallPromptEvent);
    };
    window.addEventListener("beforeinstallprompt", onPrompt);
    return () => window.removeEventListener("beforeinstallprompt", onPrompt);
  }, []);

  if (!visible) return null;

  const dismiss = () => {
    setVisible(false);
    try {
      localStorage.setItem(DISMISSED_KEY, "1");
    } catch {
      /* storage unavailable */
    }
  };

  const install = async () => {
    if (!prompt) return;
    await prompt.prompt();
    const choice = await prompt.userChoice;
    if (choice.outcome === "accepted") dismiss();
  };

  const ios = /iPhone|iPad|iPod/.test(navigator.userAgent);

  return (
    <div className="banner" role="dialog" aria-label={t("deck.install.aria")}>
      <div className="banner-text">
        <strong>{t("deck.install.title")}</strong>
        <span>{ios ? t("deck.install.ios") : prompt ? t("deck.install.prompt") : t("deck.install.generic")}</span>
      </div>
      <div className="banner-actions">
        {prompt && (
          <button type="button" className="primary" onClick={() => void install()}>
            {t("deck.install.button")}
          </button>
        )}
        <button type="button" onClick={dismiss}>
          {t("common.close")}
        </button>
      </div>
    </div>
  );
}
