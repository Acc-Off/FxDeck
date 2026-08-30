import { useEffect, useState } from "react";
import { MAX_STAGES, stageCount, stageOf, type DeckKey, type DeckProfile, type KeyIcon, type KeyStage } from "../shared/types";
import { Icon } from "../deck/Icon";
import { t as translate, useT } from "../shared/i18n";
import { api } from "./api";
import { IconPicker } from "./IconPicker";
import { describeIcon } from "./iconSearch";
import { newKey, useAdminStore } from "./store";

const PRESET_COLOURS = ["#2a2a2a", "#4a4a4a", "#2f6fdb", "#1f8a8a", "#3c8d5a", "#5a6b2f", "#d08a2a", "#c2408f", "#6b3fd6", "#8a2f2f", "#b3261e", "#ffffff"];
const DANGEROUS = ["quit", "disconnect", "exit"];

interface Props {
  profile: DeckProfile;
  cell: { row: number; col: number };
}

/**
 * Right-hand property panel (UIUX §5.3): stage tabs, then appearance, then behaviour.
 * Stage 1 edits the key itself; stages 2..5 edit `action.stages[i-1]` (design memo §3.2).
 */
export function KeyEditor({ profile, cell }: Props) {
  const t = useT();
  const update = useAdminStore((s) => s.update);
  const existing = profile.keys.find((k) => k.row === cell.row && k.col === cell.col) ?? null;
  const key: DeckKey = existing ?? newKey(cell.row, cell.col);
  const [stage, setStage] = useState(0);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [testResult, setTestResult] = useState<string | null>(null);
  const [testing, setTesting] = useState(false);

  useEffect(() => {
    setTestResult(null);
    setStage(0);
  }, [cell.row, cell.col, profile.id]);

  const stages = stageCount(key);
  const current = stage < stages ? stage : 0;
  const view = stageOf(key, current);

  /** Creates the key on first edit (empty cells hold a draft until then). */
  const edit = (mutate: (key: DeckKey) => void) => {
    update((config) => {
      const p = config.profiles.find((x) => x.id === profile.id);
      if (!p) return;
      let target = p.keys.find((k) => k.row === cell.row && k.col === cell.col);
      if (!target) {
        target = { ...key, id: key.id };
        p.keys.push(target);
      }
      mutate(target);
    });
  };

  /** Edits the look/macros of the stage being shown, wherever they live. */
  const editStage = (mutate: (s: StageFields) => void) =>
    edit((k) => {
      if (current === 0) {
        mutate(keyFields(k));
      } else {
        const s = k.action.stages?.[current - 1];
        if (s) mutate(stageFields(s));
      }
    });

  const addStage = () => {
    if (stages >= MAX_STAGES) return;
    edit((k) => {
      const base = stageOf(k, 0);
      k.action.stages = [
        ...(k.action.stages ?? []),
        { title: { ...base.title }, background: base.background, icon: base.icon ? { ...base.icon } : null, command: "", releaseCommand: null },
      ];
    });
    setStage(stages);
  };

  const removeStage = () => {
    if (current === 0) return;
    edit((k) => {
      const list = k.action.stages ?? [];
      list.splice(current - 1, 1);
      k.action.stages = list.length > 0 ? list : null;
    });
    setStage(current - 1);
  };

  const clear = () => {
    if (!existing) return;
    if (!confirm(t("key.clearConfirm", { name: existing.title.text || existing.action.command || t("key.untitled") }))) return;
    update((config) => {
      const p = config.profiles.find((x) => x.id === profile.id);
      if (p) p.keys = p.keys.filter((k) => k.id !== existing.id);
    });
  };

  const test = async (macro: string | null | undefined) => {
    const command = macro?.trim();
    if (!command) return;
    setTesting(true);
    setTestResult(null);
    try {
      const result = await api.send(command);
      setTestResult(result.success ? t("key.sent", { steps: result.stepsCompleted }) : t("key.failed", { message: describeReason(result.reason) }));
    } catch (error) {
      setTestResult(t("key.failed", { message: error instanceof Error ? error.message : String(error) }));
    } finally {
      setTesting(false);
    }
  };

  const command = view.command ?? "";
  const releaseCommand = view.releaseCommand ?? "";
  const suggestHold = !key.holdToConfirm && DANGEROUS.some((w) => command.toLowerCase().split(/[\s;]+/).includes(w));

  return (
    <div className="key-editor">
      <h3>
        {t("key.title", { row: cell.row + 1, col: cell.col + 1 })}
        {!existing && <span className="muted">{t("key.unset")}</span>}
      </h3>

      <div className="field">
        <span>{t("key.stages")}</span>
        <div className="stage-tabs" role="tablist">
          {Array.from({ length: stages }, (_, i) => (
            <button key={i} type="button" role="tab" aria-selected={i === current} className={i === current ? "active" : ""} onClick={() => setStage(i)}>
              {i + 1}
            </button>
          ))}
          {stages < MAX_STAGES && (
            <button type="button" className="ghost" onClick={addStage}>
              {t("key.addStage")}
            </button>
          )}
          {current > 0 && (
            <button type="button" className="ghost danger" onClick={removeStage}>
              {t("key.removeStage")}
            </button>
          )}
        </div>
        {stages > 1 && <span className="hint">{t("key.stagesHint")}</span>}
      </div>

      <label>
        {t("key.label")}
        <input type="text" value={view.title.text} placeholder={t("key.labelPlaceholder")} onChange={(e) => editStage((s) => (s.title.text = e.target.value))} />
      </label>
      <div className="row">
        <label>
          {t("key.position")}
          <select value={view.title.position} onChange={(e) => editStage((s) => (s.title.position = e.target.value as DeckKey["title"]["position"]))}>
            <option value="top">{t("key.position.top")}</option>
            <option value="middle">{t("key.position.middle")}</option>
            <option value="bottom">{t("key.position.bottom")}</option>
          </select>
        </label>
        <label className="checkbox">
          <input type="checkbox" checked={view.title.visible} onChange={(e) => editStage((s) => (s.title.visible = e.target.checked))} />
          {t("key.visible")}
        </label>
      </div>

      <div className="field">
        <span>{t("key.icon")}</span>
        <div className="icon-field">
          <span className="icon-preview" style={{ background: view.background }}>
            <Icon icon={view.icon} />
          </span>
          <span className="muted">{describeIcon(view.icon)}</span>
          <button type="button" onClick={() => setPickerOpen(true)}>
            {t("key.change")}
          </button>
        </div>
      </div>

      <div className="field">
        <span>{t("key.background")}</span>
        <div className="swatches">
          {PRESET_COLOURS.map((c) => (
            <button key={c} type="button" className={`swatch ${view.background.toLowerCase() === c ? "selected" : ""}`} style={{ background: c }} title={c} onClick={() => editStage((s) => s.setBackground(c))} />
          ))}
          <input type="color" value={toHex(view.background)} onChange={(e) => editStage((s) => s.setBackground(e.target.value))} title={t("key.custom")} />
        </div>
      </div>

      <label>
        {t("key.command")}
        <textarea rows={3} value={command} placeholder={t("key.commandPlaceholder")} onChange={(e) => editStage((s) => s.setCommand(e.target.value))} spellCheck={false} />
        <span className="hint">
          <code>;</code>
          {t("key.hint.chain")}
          <code>{"{500ms}"}</code>
          {t("key.hint.wait")}
          <code>;;</code>
          {t("key.hint.shortWait")}
        </span>
      </label>

      <label>
        {t("key.release")}
        <textarea rows={2} value={releaseCommand} placeholder={t("key.releasePlaceholder")} onChange={(e) => editStage((s) => s.setReleaseCommand(e.target.value))} spellCheck={false} />
        {releaseCommand.trim() && <span className="hint">{t("key.hint.release")}</span>}
      </label>

      <label className="checkbox">
        <input type="checkbox" checked={key.holdToConfirm} onChange={(e) => edit((k) => (k.holdToConfirm = e.target.checked))} />
        {t("key.hold")}
      </label>
      {suggestHold && <p className="hint warning">{t("key.dangerous")}</p>}

      <div className="row actions">
        <button type="button" onClick={() => void test(command)} disabled={testing || !command.trim()}>
          {t("key.test")}
        </button>
        {releaseCommand.trim() && (
          <button type="button" onClick={() => void test(releaseCommand)} disabled={testing}>
            {t("key.testRelease")}
          </button>
        )}
        <button type="button" className="danger ghost" onClick={clear} disabled={!existing}>
          {t("key.clear")}
        </button>
      </div>
      {testResult && <p className="hint">{testResult}</p>}

      {pickerOpen && <IconPicker current={view.icon} onPick={(icon: KeyIcon | null) => editStage((s) => s.setIcon(icon))} onClose={() => setPickerOpen(false)} />}
    </div>
  );
}

/** Uniform write access to a key (stage 1) or an extra stage, which store the same fields in different places. */
interface StageFields {
  title: DeckKey["title"];
  setBackground(value: string): void;
  setIcon(icon: KeyIcon | null): void;
  setCommand(value: string): void;
  setReleaseCommand(value: string): void;
}

function keyFields(k: DeckKey): StageFields {
  return {
    title: k.title,
    setBackground: (v) => (k.background = v),
    setIcon: (icon) => (k.icon = icon),
    setCommand: (v) => (k.action.command = v),
    setReleaseCommand: (v) => (k.action.releaseCommand = v.trim() ? v : null),
  };
}

function stageFields(s: KeyStage): StageFields {
  return {
    title: s.title,
    setBackground: (v) => (s.background = v),
    setIcon: (icon) => (s.icon = icon),
    setCommand: (v) => (s.command = v),
    setReleaseCommand: (v) => (s.releaseCommand = v.trim() ? v : null),
  };
}

function toHex(colour: string): string {
  return /^#[0-9a-f]{6}$/i.test(colour) ? colour : "#2a2a2a";
}

export function describeReason(reason: string): string {
  switch (reason) {
    case "notConnected":
      return translate("deck.failure.notConnected");
    case "invalidCommand":
      return translate("deck.failure.invalidCommand");
    default:
      return reason;
  }
}
