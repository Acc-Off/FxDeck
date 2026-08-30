import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import { Icon } from "../deck/Icon";
import { useT, type MessageKey } from "../shared/i18n";
import type { KeyIcon } from "../shared/types";
import { api, type AssetInfo } from "./api";
import { iconKey, loadIconIndex, loadRecentIcons, pruneRecentImages, pushRecentIcon, searchIcons, type IconHit, type IconTab } from "./iconSearch";
import { toKeyImage } from "./imageUpload";

interface Props {
  current: KeyIcon | null | undefined;
  onPick(icon: KeyIcon | null): void;
  onClose(): void;
}

const TABS: { id: IconTab; label: MessageKey }[] = [
  { id: "all", label: "picker.tab.all" },
  { id: "mdi", label: "picker.tab.mdi" },
  { id: "fa", label: "picker.tab.fa" },
  { id: "emoji", label: "picker.tab.emoji" },
  { id: "image", label: "picker.tab.image" },
];

/** Modal icon picker searching MDI + Font Awesome + emoji, plus uploaded images (UIUX §5.4). */
export function IconPicker({ current, onPick, onClose }: Props) {
  const t = useT();
  const [query, setQuery] = useState("");
  const [tab, setTab] = useState<IconTab>(() => (current?.type === "image" ? "image" : "all"));
  const [index, setIndex] = useState<Awaited<ReturnType<typeof loadIconIndex>> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [recent, setRecent] = useState(loadRecentIcons);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    loadIconIndex()
      .then(setIndex)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
    // Deleted images would otherwise linger in "recent" (the browser still has them cached).
    if (loadRecentIcons().some((icon) => icon.type === "image")) {
      api
        .assets()
        .then((r) => setRecent(pruneRecentImages(new Set(r.assets.map((a) => a.hash)))))
        .catch(() => undefined);
    }
    inputRef.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const results = useMemo<IconHit[]>(() => (index && tab !== "image" ? searchIcons(index, query, tab) : []), [index, query, tab]);
  const currentKey = iconKey(current);

  const pick = (icon: KeyIcon | null) => {
    if (icon) pushRecentIcon(icon);
    onPick(icon);
    onClose();
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal icon-picker" role="dialog" aria-label={t("picker.aria")} onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <input ref={inputRef} type="search" placeholder={tab === "image" ? t("picker.searchImages") : t("picker.search")} value={query} disabled={tab === "image"} onChange={(e) => setQuery(e.target.value)} />
          <button type="button" className="ghost" onClick={onClose} aria-label={t("common.close")}>
            ✕
          </button>
        </div>
        <div className="tabs">
          {TABS.map((tabDef) => (
            <button key={tabDef.id} type="button" className={tab === tabDef.id ? "active" : ""} onClick={() => setTab(tabDef.id)}>
              {t(tabDef.label)}
            </button>
          ))}
        </div>
        {tab === "image" ? (
          <ImageTab currentKey={currentKey} onPick={pick} />
        ) : (
          <>
            {!query && recent.length > 0 && (
              <div className="icon-section">
                <div className="section-label">{t("picker.recent")}</div>
                <div className="icon-grid">
                  {recent.map((icon) => (
                    <IconCell key={iconKey(icon)} icon={icon} label={iconKey(icon)} selected={iconKey(icon) === currentKey} onClick={() => pick(icon)} />
                  ))}
                </div>
              </div>
            )}
            <div className="icon-results">
              {error && <p className="error">{t("picker.loadError", { message: error })}</p>}
              {!index && !error && <p className="muted">{t("picker.loading")}</p>}
              {index && results.length === 0 && <p className="muted">{t("picker.none")}</p>}
              <div className="icon-grid">
                {results.map((hit) => (
                  <IconCell key={hit.key} icon={hit.icon} label={hit.label} selected={hit.key === currentKey} onClick={() => pick(hit.icon)} />
                ))}
              </div>
              {index && results.length >= 400 && <p className="muted">{t("picker.truncated")}</p>}
            </div>
          </>
        )}
        <div className="modal-footer">
          <button type="button" onClick={() => pick(null)}>
            {t("picker.noIcon")}
          </button>
        </div>
      </div>
    </div>
  );
}

/** Upload (drop / file picker) and the list of stored images. Files are shrunk to 256×256 PNG in the browser first. */
function ImageTab({ currentKey, onPick }: { currentKey: string; onPick(icon: KeyIcon): void }) {
  const t = useT();
  const [images, setImages] = useState<AssetInfo[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(0);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  const refresh = useCallback(
    () =>
      api
        .assets()
        .then((r) => {
          setImages(r.assets);
          setLoadError(null);
        })
        .catch((e: unknown) => setLoadError(e instanceof Error ? e.message : String(e))),
    [],
  );

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const upload = async (files: FileList | File[]) => {
    const list = Array.from(files).filter((f) => f.type.startsWith("image/") || /\.(png|jpe?g|webp|gif)$/i.test(f.name));
    if (list.length === 0) {
      setUploadError(t("picker.image.notImage"));
      return;
    }
    setUploadError(null);
    setUploading(list.length);
    let lastHash: string | null = null;
    const failures: string[] = [];
    for (const file of list) {
      try {
        const png = await toKeyImage(file);
        const result = await api.uploadAsset(png, file.name.replace(/\.[^.]+$/, "") + ".png");
        lastHash = result.hash;
      } catch (error) {
        failures.push(`${file.name}: ${error instanceof Error ? error.message : String(error)}`);
      } finally {
        setUploading((n) => n - 1);
      }
    }
    await refresh();
    if (failures.length > 0) setUploadError(failures.join(" / "));
    // A single successful upload is what the user wants on the key.
    if (list.length === 1 && lastHash) onPick({ type: "image", hash: lastHash });
  };

  const onDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    void upload(event.dataTransfer.files);
  };

  return (
    <div className="icon-results image-tab">
      <div
        className={`dropzone ${dragging ? "over" : ""}`}
        onClick={() => fileRef.current?.click()}
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => e.key === "Enter" && fileRef.current?.click()}
      >
        {uploading > 0 ? t("picker.image.uploading", { count: uploading }) : t("picker.image.drop")}
        <span className="muted small">{t("picker.image.formats")}</span>
        <input
          ref={fileRef}
          type="file"
          accept="image/png,image/jpeg,image/webp,image/gif"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files) void upload(e.target.files);
            e.target.value = "";
          }}
        />
      </div>
      {uploadError && <p className="error">{uploadError}</p>}
      {loadError && <p className="error">{t("picker.image.listError", { message: loadError })}</p>}
      {images === null && !loadError && <p className="muted">{t("picker.image.loading")}</p>}
      {images && images.length === 0 && <p className="muted">{t("picker.image.none")}</p>}
      {images && images.length > 0 && (
        <>
          <div className="section-label">{t("picker.image.uploaded", { count: images.length })}</div>
          <div className="icon-grid images">
            {images.map((asset) => (
              <IconCell
                key={asset.hash}
                icon={{ type: "image", hash: asset.hash }}
                label={asset.referenced ? t("picker.image.inUse") : t("picker.image.unused")}
                selected={`image:${asset.hash}` === currentKey}
                onClick={() => onPick({ type: "image", hash: asset.hash })}
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
}

function IconCell({ icon, label, selected, onClick }: { icon: KeyIcon; label: string; selected: boolean; onClick(): void }) {
  return (
    <button type="button" className={`icon-cell ${selected ? "selected" : ""}`} title={label} onClick={onClick}>
      <Icon icon={icon} />
    </button>
  );
}
