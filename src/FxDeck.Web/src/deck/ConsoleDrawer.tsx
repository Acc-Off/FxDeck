import { useEffect, useRef, useState } from "react";
import { useT } from "../shared/i18n";

interface Props {
  lines: string[];
  onClose(): void;
}

/**
 * Bottom drawer with the last console lines (UIUX §4.8). Follows new output until the user touches or scrolls
 * the list; scrolling back to the bottom (or "Latest") resumes following.
 */
export function ConsoleDrawer({ lines, onClose }: Props) {
  const t = useT();
  const listRef = useRef<HTMLDivElement>(null);
  const [follow, setFollow] = useState(true);

  useEffect(() => {
    const list = listRef.current;
    if (follow && list) list.scrollTop = list.scrollHeight;
  }, [lines, follow]);

  const onScroll = () => {
    const list = listRef.current;
    if (!list) return;
    const atBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 8;
    if (atBottom !== follow) setFollow(atBottom);
  };

  return (
    <div className="console" role="log" aria-label={t("deck.console")} onPointerDown={(e) => e.stopPropagation()}>
      <div className="console-bar">
        <span className="console-title">{t("deck.console")}</span>
        {!follow && (
          <button type="button" onClick={() => setFollow(true)}>
            {t("deck.console.latest")}
          </button>
        )}
        <button type="button" className="close" onClick={onClose} aria-label={t("common.close")}>
          ✕
        </button>
      </div>
      <div className="console-lines" ref={listRef} onScroll={onScroll} onPointerDown={() => setFollow(false)}>
        {lines.length === 0 ? (
          <div className="console-empty">{t("deck.console.empty")}</div>
        ) : (
          lines.map((line, index) => (
            <div key={index} className="console-line">
              {line}
            </div>
          ))
        )}
      </div>
    </div>
  );
}
