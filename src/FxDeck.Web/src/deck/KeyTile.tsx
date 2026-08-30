import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent } from "react";
import { stageCount, stageOf, type DeckKey } from "../shared/types";
import { Icon } from "./Icon";

const HOLD_MS = 600;
/** Movement beyond this cancels the tap; the Grid then decides whether it was a swipe (UIUX §4.3). */
export const TAP_SLOP_PX = 10;

interface Props {
  deckKey: DeckKey;
  /** Current stage (0-based) of a staged key. */
  stage: number;
  running: boolean;
  flashAt: number | undefined;
  /** Returns whether the press was actually sent. */
  onPress(keyId: string): boolean;
  onRelease(keyId: string): void;
}

/**
 * One key. Tap keys fire when the finger lifts without having moved, like Stream Deck Mobile;
 * with holdToConfirm they fire after a 600 ms hold instead (UIUX §4.4).
 * Hold keys (a stage with a release command) send the press on pointer-down and always send
 * a release when the finger lifts, the gesture is cancelled or the key disappears (UIUX §4.3).
 */
export function KeyTile({ deckKey, stage, running, flashAt, onPress, onRelease }: Props) {
  const [pressed, setPressed] = useState(false);
  const [holdProgress, setHoldProgress] = useState(0);
  const [flashing, setFlashing] = useState(false);
  const gesture = useRef<{ x: number; y: number; cancelled: boolean } | null>(null);
  const holdStart = useRef<number | null>(null);
  const holdFrame = useRef(0);
  /** Set while a hold key's press has gone out and its release has not. */
  const held = useRef(false);
  const releaseRef = useRef(onRelease);
  releaseRef.current = onRelease;

  const current = stageOf(deckKey, stage);
  const isHold = Boolean(current.releaseCommand?.trim());
  const stages = stageCount(deckKey);

  useEffect(() => {
    if (!flashAt) return;
    setFlashing(true);
    const timer = window.setTimeout(() => setFlashing(false), 400);
    return () => window.clearTimeout(timer);
  }, [flashAt]);

  const cancelHold = useCallback(() => {
    holdStart.current = null;
    cancelAnimationFrame(holdFrame.current);
    setHoldProgress(0);
  }, []);

  const releaseNow = useCallback(() => {
    if (!held.current) return;
    held.current = false;
    releaseRef.current(deckKey.id);
  }, [deckKey.id]);

  const fire = useCallback(
    (strength: number) => {
      if (running) return;
      navigator.vibrate?.(strength);
      if (onPress(deckKey.id) && isHold) held.current = true;
    },
    [deckKey.id, onPress, running, isHold],
  );

  const tick = useCallback(() => {
    if (holdStart.current === null) return;
    const progress = Math.min(1, (performance.now() - holdStart.current) / HOLD_MS);
    setHoldProgress(progress);
    if (progress >= 1) {
      cancelHold();
      if (gesture.current) gesture.current.cancelled = true; // the release must not fire again
      fire(30);
      return;
    }
    holdFrame.current = requestAnimationFrame(tick);
  }, [cancelHold, fire]);

  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (event.pointerType === "mouse" && event.button !== 0) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    gesture.current = { x: event.clientX, y: event.clientY, cancelled: running };
    setPressed(true);
    navigator.vibrate?.(5);
    if (deckKey.holdToConfirm && !running) {
      holdStart.current = performance.now();
      holdFrame.current = requestAnimationFrame(tick);
    } else if (isHold) {
      gesture.current.cancelled = true; // the press goes out now; lifting only releases
      fire(10);
    }
  };

  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    const current = gesture.current;
    if (!current || current.cancelled) return;
    if (Math.hypot(event.clientX - current.x, event.clientY - current.y) > TAP_SLOP_PX) {
      current.cancelled = true; // moved: a swipe or a change of mind, not a press
      setPressed(false);
      cancelHold();
    }
  };

  const onPointerUp = () => {
    const current = gesture.current;
    gesture.current = null;
    setPressed(false);
    cancelHold();
    if (held.current) {
      releaseNow();
      return;
    }
    if (!current || current.cancelled || deckKey.holdToConfirm) return;
    fire(10);
  };

  const onPointerCancel = () => {
    gesture.current = null;
    setPressed(false);
    cancelHold();
    releaseNow();
  };

  useEffect(() => cancelHold, [cancelHold]);
  // The key left the screen (profile swipe, config change) while held: never leave the game "pressed".
  useEffect(() => releaseNow, [releaseNow]);

  const title = current.title;
  const showTitle = title.visible && title.text.length > 0;
  const hasIcon = Boolean(current.icon);
  const className = ["key", showTitle && hasIcon ? `title-${title.position}` : "", pressed ? "pressed" : "", running ? "running" : "", flashing ? "flash" : ""]
    .filter(Boolean)
    .join(" ");

  return (
    <div
      className={className}
      style={{ background: current.background }}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerCancel}
      onContextMenu={(event) => event.preventDefault()}
      role="button"
      aria-label={title.text || current.command || "key"}
    >
      <Icon icon={current.icon} />
      {showTitle && <div className={`title ${hasIcon ? title.position : "middle solo"}`}>{title.text}</div>}
      {stages > 1 && <StageDots count={stages} current={stage} />}
      {deckKey.holdToConfirm && <div className="hold-mark" aria-hidden="true" />}
      {holdProgress > 0 && <div className="hold-ring" style={{ "--p": `${holdProgress * 100}%` } as CSSProperties} aria-hidden="true" />}
      {running && <div className="progress" aria-hidden="true" />}
    </div>
  );
}

/** Top-left row of dots showing which stage a staged key is on (UIUX §4.11). */
export function StageDots({ count, current }: { count: number; current: number }) {
  return (
    <div className="stage-dots" aria-hidden="true">
      {Array.from({ length: count }, (_, i) => (
        <span key={i} className={i === current ? "on" : ""} />
      ))}
    </div>
  );
}
