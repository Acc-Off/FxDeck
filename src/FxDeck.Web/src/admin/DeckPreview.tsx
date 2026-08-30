import { t } from "../shared/i18n";
import { useMemo, useState, type CSSProperties, type DragEvent } from "react";
import { Icon } from "../deck/Icon";
import { StageDots } from "../deck/KeyTile";
import { stageCount, type DeckKey, type DeckProfile } from "../shared/types";

interface Props {
  profile: DeckProfile;
  selected: { row: number; col: number } | null;
  onSelect(cell: { row: number; col: number }): void;
  /** Move (empty target) or swap (occupied target). */
  onMove(keyId: string, target: { row: number; col: number }): void;
  keySize?: number;
}

/** Same look as the phone (landscape), clickable and drag-and-droppable (UIUX §5.3). */
export function DeckPreview({ profile, selected, onSelect, onMove, keySize = 88 }: Props) {
  const [dragOver, setDragOver] = useState<number | null>(null);
  const gap = Math.round(keySize * 0.1);

  const keyByIndex = useMemo(() => {
    const map = new Map<number, DeckKey>();
    for (const key of profile.keys) {
      if (key.row >= 0 && key.row < profile.rows && key.col >= 0 && key.col < profile.columns) map.set(key.row * profile.columns + key.col, key);
    }
    return map;
  }, [profile]);

  const style = {
    "--key": `${keySize}px`,
    gridTemplateColumns: `repeat(${profile.columns}, ${keySize}px)`,
    gridTemplateRows: `repeat(${profile.rows}, ${keySize}px)`,
    gap: `${gap}px`,
    padding: `${gap}px`,
  } as CSSProperties;

  const onDrop = (event: DragEvent, index: number) => {
    event.preventDefault();
    setDragOver(null);
    const keyId = event.dataTransfer.getData("application/x-fxdeck-key");
    if (!keyId) return;
    onMove(keyId, { row: Math.floor(index / profile.columns), col: index % profile.columns });
  };

  return (
    <div className="deck-preview" style={style}>
      {Array.from({ length: profile.columns * profile.rows }, (_, index) => {
        const row = Math.floor(index / profile.columns);
        const col = index % profile.columns;
        const key = keyByIndex.get(index);
        const isSelected = selected?.row === row && selected?.col === col;
        const hasIcon = Boolean(key?.icon);
        const showTitle = key ? key.title.visible && key.title.text.length > 0 : false;
        return (
          <div
            key={index}
            className={["key", key ? "" : "empty", showTitle && hasIcon ? `title-${key!.title.position}` : "", isSelected ? "selected" : "", dragOver === index ? "drag-over" : ""]
              .filter(Boolean)
              .join(" ")}
            style={key ? { background: key.background } : undefined}
            draggable={Boolean(key)}
            onClick={() => onSelect({ row, col })}
            onDragStart={(event) => {
              if (!key) return;
              event.dataTransfer.setData("application/x-fxdeck-key", key.id);
              event.dataTransfer.effectAllowed = "move";
            }}
            onDragOver={(event) => {
              event.preventDefault();
              event.dataTransfer.dropEffect = "move";
              if (dragOver !== index) setDragOver(index);
            }}
            onDragLeave={() => setDragOver((current) => (current === index ? null : current))}
            onDrop={(event) => onDrop(event, index)}
            title={key ? key.action.command || key.title.text : t("profiles.emptyKey")}
          >
            {key && <Icon icon={key.icon} />}
            {key && showTitle && <div className={`title ${hasIcon ? key.title.position : "middle solo"}`}>{key.title.text}</div>}
            {key && stageCount(key) > 1 && <StageDots count={stageCount(key)} current={0} />}
            {key?.holdToConfirm && <div className="hold-mark" aria-hidden="true" />}
          </div>
        );
      })}
    </div>
  );
}
