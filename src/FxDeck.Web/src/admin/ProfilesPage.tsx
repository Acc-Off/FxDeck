import { useRef, useState } from "react";
import { useElementSize } from "../deck/hooks";
import { useT, type MessageKey } from "../shared/i18n";
import type { DeckProfile } from "../shared/types";
import { api } from "./api";
import { DeckPreview } from "./DeckPreview";
import { KeyEditor } from "./KeyEditor";
import { newProfile, useAdminStore, uuid } from "./store";

const SIZE_PRESETS: { label: MessageKey; columns: number; rows: number }[] = [
  { label: "profiles.size.mini", columns: 3, rows: 2 },
  { label: "profiles.size.standard", columns: 5, rows: 3 },
  { label: "profiles.size.xl", columns: 8, rows: 4 },
];

export function ProfilesPage() {
  const t = useT();
  const config = useAdminStore((s) => s.config);
  const selectedProfileId = useAdminStore((s) => s.selectedProfileId);
  const selectedCell = useAdminStore((s) => s.selectedCell);
  const selectProfile = useAdminStore((s) => s.selectProfile);
  const selectCell = useAdminStore((s) => s.selectCell);
  const update = useAdminStore((s) => s.update);
  const load = useAdminStore((s) => s.load);
  const [menuOpen, setMenuOpen] = useState(false);
  /** Profile id for which the user chose "custom" even though the size still matches a preset. */
  const [customFor, setCustomFor] = useState<string | null>(null);
  const [importMessage, setImportMessage] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const previewRef = useRef<HTMLDivElement>(null);
  const previewSize = useElementSize(previewRef);

  if (!config) return null;
  const profiles = config.profiles;
  const profile = profiles.find((p) => p.id === selectedProfileId) ?? profiles[0] ?? null;
  const index = profile ? profiles.indexOf(profile) : -1;
  // Fill the preview column like the Stream Deck software does: 5×3 gets big keys, 8×4 shrinks to fit.
  // Gap = 10 % of a key plus padding on both sides; also keep the whole grid inside the window height.
  const keySize = (() => {
    if (!profile || previewSize.width === 0) return 88;
    const byWidth = previewSize.width / (profile.columns * 1.1 + 0.2);
    const byHeight = (window.innerHeight - 220) / (profile.rows * 1.1 + 0.2);
    return Math.max(36, Math.min(140, Math.floor(Math.min(byWidth, byHeight))));
  })();

  const addProfile = () => {
    const created = newProfile(t("profiles.newName", { n: profiles.length + 1 }), profiles.length);
    update((c) => c.profiles.push(created));
    selectProfile(created.id);
  };

  const rename = () => {
    if (!profile) return;
    const name = prompt(t("profiles.renamePrompt"), profile.name)?.trim();
    if (!name) return;
    update((c) => {
      const p = c.profiles.find((x) => x.id === profile.id);
      if (p) p.name = name;
    });
  };

  const duplicate = () => {
    if (!profile) return;
    const copy: DeckProfile = structuredClone(profile);
    copy.id = uuid();
    copy.name = t("profiles.copySuffix", { name: profile.name });
    copy.keys.forEach((k) => (k.id = uuid()));
    update((c) => c.profiles.splice(index + 1, 0, copy));
    selectProfile(copy.id);
  };

  const remove = () => {
    if (!profile) return;
    if (!confirm(t("profiles.deleteConfirm", { name: profile.name, count: profile.keys.length }))) return;
    const next = profiles[index + 1] ?? profiles[index - 1] ?? null;
    update((c) => (c.profiles = c.profiles.filter((p) => p.id !== profile.id)));
    selectProfile(next?.id ?? null);
  };

  const move = (delta: number) => {
    if (!profile) return;
    const target = index + delta;
    if (target < 0 || target >= profiles.length) return;
    update((c) => {
      const [item] = c.profiles.splice(index, 1);
      c.profiles.splice(target, 0, item);
    });
  };

  const resize = (columns: number, rows: number) => {
    if (!profile) return;
    const outside = profile.keys.filter((k) => k.row >= rows || k.col >= columns);
    if (outside.length > 0 && !confirm(t("profiles.resizeConfirm", { count: outside.length }))) return;
    update((c) => {
      const p = c.profiles.find((x) => x.id === profile.id);
      if (!p) return;
      p.columns = columns;
      p.rows = rows;
      p.keys = p.keys.filter((k) => k.row < rows && k.col < columns);
    });
    if (selectedCell && (selectedCell.row >= rows || selectedCell.col >= columns)) selectCell(null);
  };

  const moveKey = (keyId: string, target: { row: number; col: number }) => {
    if (!profile) return;
    update((c) => {
      const p = c.profiles.find((x) => x.id === profile.id);
      if (!p) return;
      const source = p.keys.find((k) => k.id === keyId);
      if (!source || (source.row === target.row && source.col === target.col)) return;
      const occupant = p.keys.find((k) => k.row === target.row && k.col === target.col);
      if (occupant) {
        occupant.row = source.row;
        occupant.col = source.col;
      }
      source.row = target.row;
      source.col = target.col;
    });
    selectCell(target);
  };

  const importProfile = async (file: File) => {
    setImportMessage(null);
    try {
      const result = await api.import(file, "profile");
      setImportMessage(t("profiles.imported", { count: result.profilesAdded, warnings: result.warnings.join(" ") }));
      await load();
    } catch (error) {
      setImportMessage(t("profiles.importFailed", { message: error instanceof Error ? error.message : String(error) }));
    }
  };

  const matchesPreset = profile ? SIZE_PRESETS.some((s) => s.columns === profile.columns && s.rows === profile.rows) : false;
  const presetValue = !profile ? "" : matchesPreset && customFor !== profile.id ? `${profile.columns}x${profile.rows}` : "custom";

  return (
    <div className="page profiles-page">
      <div className="toolbar">
        <select value={profile?.id ?? ""} onChange={(e) => selectProfile(e.target.value || null)} disabled={profiles.length === 0}>
          {profiles.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
        <button type="button" onClick={addProfile} title={t("profiles.new")}>
          ＋
        </button>
        <div className="menu-anchor">
          <button type="button" onClick={() => setMenuOpen((o) => !o)} disabled={!profile} aria-haspopup="menu">
            ⋯
          </button>
          {menuOpen && profile && (
            <div className="menu" role="menu" onMouseLeave={() => setMenuOpen(false)}>
              <button type="button" onClick={() => (setMenuOpen(false), rename())}>
                {t("profiles.menu.rename")}
              </button>
              <button type="button" onClick={() => (setMenuOpen(false), duplicate())}>
                {t("profiles.menu.duplicate")}
              </button>
              <button type="button" onClick={() => (setMenuOpen(false), move(-1))} disabled={index <= 0}>
                {t("profiles.menu.moveUp")}
              </button>
              <button type="button" onClick={() => (setMenuOpen(false), move(1))} disabled={index >= profiles.length - 1}>
                {t("profiles.menu.moveDown")}
              </button>
              <a href={api.exportUrl(profile.id)} onClick={() => setMenuOpen(false)}>
                {t("profiles.menu.export")}
              </a>
              <button type="button" onClick={() => (setMenuOpen(false), fileInput.current?.click())}>
                {t("profiles.menu.import")}
              </button>
              <button type="button" className="danger" onClick={() => (setMenuOpen(false), remove())}>
                {t("profiles.menu.delete")}
              </button>
            </div>
          )}
        </div>
        {profile && (
          <label className="inline">
            {t("profiles.size")}
            <select
              value={presetValue}
              onChange={(e) => {
                const preset = SIZE_PRESETS.find((s) => `${s.columns}x${s.rows}` === e.target.value);
                if (preset) {
                  setCustomFor(null);
                  resize(preset.columns, preset.rows);
                } else {
                  setCustomFor(profile.id); // show the column × row inputs, starting from the current size
                }
              }}
            >
              {SIZE_PRESETS.map((s) => (
                <option key={s.label} value={`${s.columns}x${s.rows}`}>
                  {t(s.label)}
                </option>
              ))}
              <option value="custom">{t("profiles.size.custom")}</option>
            </select>
          </label>
        )}
        {profile && presetValue === "custom" && (
          <span className="inline">
            <input type="number" min={1} max={12} value={profile.columns} onChange={(e) => resize(clamp(Number(e.target.value), 1, 12), profile.rows)} aria-label={t("profiles.columns")} /> {t("profiles.columnsUnit")}
            <input type="number" min={1} max={8} value={profile.rows} onChange={(e) => resize(profile.columns, clamp(Number(e.target.value), 1, 8))} aria-label={t("profiles.rows")} /> {t("profiles.rowsUnit")}
          </span>
        )}
        <input ref={fileInput} type="file" accept=".fxdeck,.json,.zip" hidden onChange={(e) => e.target.files?.[0] && void importProfile(e.target.files[0])} />
      </div>
      {importMessage && <p className="hint">{importMessage}</p>}

      {!profile ? (
        <div className="no-profiles">
          <p>{t("profiles.none")}</p>
        </div>
      ) : (
        <div className="editor-layout">
          <div className="preview-column" ref={previewRef}>
            {profile.keys.length === 0 && <p className="hint">{t("profiles.firstKeyHint")}</p>}
            <DeckPreview profile={profile} selected={selectedCell} onSelect={selectCell} onMove={moveKey} keySize={keySize} />
            <p className="muted small">{t("profiles.previewHint")}</p>
          </div>
          <div className="panel-column">
            {selectedCell ? <KeyEditor profile={profile} cell={selectedCell} /> : <p className="muted">{t("profiles.selectKey")}</p>}
          </div>
        </div>
      )}
    </div>
  );
}

function clamp(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : min;
}
