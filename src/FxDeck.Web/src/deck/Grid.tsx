import { useMemo, useRef, type CSSProperties, type PointerEvent } from "react";
import type { DeckKey, DeckProfile } from "../shared/types";
import { useElementSize } from "./hooks";
import { KeyTile } from "./KeyTile";

const SWIPE_MIN_X = 70;
const SWIPE_MAX_Y = 60;

interface Props {
  profile: DeckProfile;
  running: Record<string, true>;
  stages: Record<string, number>;
  flash: Record<string, number>;
  onPress(keyId: string): boolean;
  onRelease(keyId: string): void;
  onSwipe(delta: number): void;
}

/**
 * Fixed grid (UIUX §4.1). Landscape shows columns×rows; portrait reflows the same reading order
 * into rows×columns (design memo §3.7).
 */
export function Grid({ profile, running, stages, flash, onPress, onRelease, onSwipe }: Props) {
  const areaRef = useRef<HTMLDivElement>(null);
  const size = useElementSize(areaRef);
  const swipeStart = useRef<{ x: number; y: number; id: number } | null>(null);

  const portrait = size.width > 0 && size.height > size.width;
  const displayCols = portrait ? profile.rows : profile.columns;
  const displayRows = portrait ? profile.columns : profile.rows;
  const cellCount = profile.columns * profile.rows;

  const keyByIndex = useMemo(() => {
    const map = new Map<number, DeckKey>();
    for (const key of profile.keys) {
      if (key.row < 0 || key.row >= profile.rows || key.col < 0 || key.col >= profile.columns) continue;
      map.set(key.row * profile.columns + key.col, key);
    }
    return map;
  }, [profile]);

  const gap = Math.max(6, Math.round(Math.min(size.width, size.height) * 0.02));
  const keySize = Math.max(
    0,
    Math.floor(Math.min((size.width - gap * (displayCols + 1)) / displayCols, (size.height - gap * (displayRows + 1)) / displayRows)),
  );

  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    swipeStart.current = { x: event.clientX, y: event.clientY, id: event.pointerId };
  };
  const onPointerUp = (event: PointerEvent<HTMLDivElement>) => {
    const start = swipeStart.current;
    swipeStart.current = null;
    if (!start || start.id !== event.pointerId) return;
    const dx = event.clientX - start.x;
    const dy = event.clientY - start.y;
    if (Math.abs(dx) >= SWIPE_MIN_X && Math.abs(dy) <= SWIPE_MAX_Y) onSwipe(dx < 0 ? 1 : -1);
  };

  const style = {
    "--key": `${keySize}px`,
    "--gap": `${gap}px`,
    gridTemplateColumns: `repeat(${displayCols}, ${keySize}px)`,
    gridTemplateRows: `repeat(${displayRows}, ${keySize}px)`,
    gap: `${gap}px`,
  } as CSSProperties;

  return (
    <div className="grid-area" ref={areaRef} onPointerDown={onPointerDown} onPointerUp={onPointerUp} onPointerCancel={() => (swipeStart.current = null)}>
      {keySize > 0 && (
        <div className="grid" style={style}>
          {Array.from({ length: cellCount }, (_, index) => {
            const key = keyByIndex.get(index);
            return key ? (
              <KeyTile key={key.id} deckKey={key} stage={stages[key.id] ?? 0} running={Boolean(running[key.id])} flashAt={flash[key.id]} onPress={onPress} onRelease={onRelease} />
            ) : (
              <div key={`empty-${index}`} className="key empty" aria-hidden="true" />
            );
          })}
        </div>
      )}
    </div>
  );
}
