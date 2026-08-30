import { useEffect, useState } from "react";
import { useT } from "../shared/i18n";
import { api, type AboutInfo } from "./api";

export function AboutPage() {
  const t = useT();
  const [about, setAbout] = useState<AboutInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .about()
      .then(setAbout)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  return (
    <div className="page about-page">
      <h2>{t("nav.about")}</h2>
      {error && <p className="error">{error}</p>}
      {about && (
        <>
          <p>
            <strong>{about.name}</strong> {t("about.version", { version: about.version })}
          </p>
          <p>
            <a href={`${about.repository}/releases`} target="_blank" rel="noreferrer">
              {t("about.releases")}
            </a>
          </p>
          <section>
            <h3>{t("about.license")}</h3>
            <p>
              {t("about.licenseText", { license: about.license })}
              <a href={about.repository} target="_blank" rel="noreferrer">
                {about.repository}
              </a>
            </p>
          </section>
          <section>
            <h3>{t("about.thirdParty")}</h3>
            <pre className="notices">{about.thirdPartyNotices}</pre>
          </section>
        </>
      )}
    </div>
  );
}
