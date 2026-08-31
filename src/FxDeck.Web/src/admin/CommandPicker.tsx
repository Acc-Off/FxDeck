import { useEffect, useMemo, useRef, useState } from "react";
import { useT } from "../shared/i18n";
import { formatExtractedAt, isAuxiliaryCommand, usageHint } from "./commandAssist";
import { useAdminStore } from "./store";

interface Props {
  onPick(name: string): void;
  onClose(): void;
}

/**
 * Modal to find a command in the extracted cache when the name will not come to mind (UIUX §5.6).
 * Auxiliary commands (keybind halves, internals) are hidden unless toggled on; the cache keeps them all.
 */
export function CommandPicker({ onPick, onClose }: Props) {
  const t = useT();
  const cache = useAdminStore((s) => s.commandCache);
  const extract = useAdminStore((s) => s.extractCommands);
  const [query, setQuery] = useState("");
  const [showAux, setShowAux] = useState(false);
  const [extracting, setExtracting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const commands = useMemo(() => cache?.commands ?? [], [cache]);
  const results = useMemo(() => {
    const q = query.trim().toLowerCase();
    return commands.filter((c) => {
      if (!showAux && isAuxiliaryCommand(c.name)) return false;
      return !q || c.name.toLowerCase().includes(q) || (c.help ?? "").toLowerCase().includes(q);
    });
  }, [commands, query, showAux]);

  const extracted = Boolean(cache?.extractedAt);
  const hiddenAux = !showAux && commands.some((c) => isAuxiliaryCommand(c.name));

  const reextract = async () => {
    setExtracting(true);
    setError(null);
    try {
      await extract();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setExtracting(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal command-picker" role="dialog" aria-label={t("cmdpicker.aria")} onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <input ref={inputRef} type="search" placeholder={t("cmdpicker.search")} value={query} onChange={(e) => setQuery(e.target.value)} />
          {extracted && cache?.extractedAt && (
            <span className="muted small cache-status">{t("assist.status", { count: cache.count ?? commands.length, time: formatExtractedAt(cache.extractedAt) })}</span>
          )}
          <button type="button" onClick={() => void reextract()} disabled={extracting}>
            {extracting ? t("assist.extracting") : t("cmdpicker.reextract")}
          </button>
          <button type="button" className="ghost" onClick={onClose} aria-label={t("common.close")}>
            ✕
          </button>
        </div>
        <label className="checkbox aux-toggle">
          <input type="checkbox" checked={showAux} onChange={(e) => setShowAux(e.target.checked)} />
          {t("cmdpicker.showAux")}
        </label>
        {error && <p className="error picker-error">{error}</p>}
        <div className="command-results">
          {!extracted && !extracting && <p className="muted picker-empty">{t("cmdpicker.empty")}</p>}
          {extracted && results.length === 0 && <p className="muted picker-empty">{t("cmdpicker.none")}</p>}
          {results.map((command) => (
            <button key={command.name} type="button" className="command-row" onClick={() => onPick(command.name)}>
              <code className="name">{command.name}</code>
              <span className="help">{command.help}</span>
              {usageHint(command) && <code className="usage">{usageHint(command)}</code>}
            </button>
          ))}
          {extracted && hiddenAux && <p className="muted small picker-note">{t("cmdpicker.auxHidden")}</p>}
        </div>
      </div>
    </div>
  );
}
