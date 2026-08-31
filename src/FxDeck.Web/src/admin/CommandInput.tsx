import { forwardRef, useImperativeHandle, useRef, useState, type KeyboardEvent } from "react";
import type { CachedCommand } from "../shared/types";
import { suggestCommands, tokenAtCaret, usageHint } from "./commandAssist";
import { useAdminStore } from "./store";

export interface CommandInputHandle {
  /** Inserts text at the caret (used by the command picker) and puts the focus back. */
  insert(text: string): void;
}

interface Props {
  value: string;
  rows: number;
  placeholder?: string;
  onChange(value: string): void;
}

interface Popup {
  items: CachedCommand[];
  start: number;
  end: number;
}

/**
 * Macro textarea with the typeahead of UIUX §5.3: while typing, the command-name token at the caret
 * offers matches from the extracted cache; picking one replaces the token and appends a space so the
 * arguments can follow. Best-effort — manual input is never blocked.
 */
export const CommandInput = forwardRef<CommandInputHandle, Props>(function CommandInput({ value, rows, placeholder, onChange }, handle) {
  const commands = useAdminStore((s) => s.commandCache?.commands);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const [popup, setPopup] = useState<Popup | null>(null);
  const [active, setActive] = useState(0);

  const moveCaret = (position: number) => {
    requestAnimationFrame(() => {
      const el = textarea.current;
      if (!el) return;
      el.focus();
      el.setSelectionRange(position, position);
    });
  };

  useImperativeHandle(handle, () => ({
    insert(text: string) {
      const el = textarea.current;
      const start = el?.selectionStart ?? value.length;
      const end = el?.selectionEnd ?? start;
      onChange(value.slice(0, start) + text + value.slice(end));
      moveCaret(start + text.length);
    },
  }));

  /** Recomputes the dropdown from the live textarea state (value not yet in props during onChange). */
  const refresh = (el: HTMLTextAreaElement) => {
    if (!commands || commands.length === 0 || el.selectionStart !== el.selectionEnd) {
      setPopup(null);
      return;
    }
    const token = tokenAtCaret(el.value, el.selectionStart);
    const items = token && token.prefix ? suggestCommands(commands, token.prefix) : [];
    setPopup(items.length > 0 && token ? { items, start: token.start, end: token.end } : null);
    setActive(0);
  };

  const accept = (command: CachedCommand) => {
    if (!popup) return;
    const inserted = command.name + " ";
    onChange(value.slice(0, popup.start) + inserted + value.slice(popup.end));
    setPopup(null);
    moveCaret(popup.start + inserted.length);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (!popup) return;
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const delta = event.key === "ArrowDown" ? 1 : -1;
      setActive((i) => (i + delta + popup.items.length) % popup.items.length);
    } else if (event.key === "Enter" || event.key === "Tab") {
      event.preventDefault();
      accept(popup.items[active]);
    } else if (event.key === "Escape") {
      setPopup(null);
    }
  };

  return (
    <div className="command-input">
      <textarea
        ref={textarea}
        rows={rows}
        value={value}
        placeholder={placeholder}
        spellCheck={false}
        onChange={(e) => {
          onChange(e.target.value);
          refresh(e.target);
        }}
        onSelect={(e) => refresh(e.currentTarget)}
        onKeyDown={onKeyDown}
        onBlur={() => setPopup(null)}
      />
      {popup && (
        <ul className="typeahead" role="listbox">
          {popup.items.map((command, i) => (
            <li
              key={command.name}
              role="option"
              aria-selected={i === active}
              className={i === active ? "active" : ""}
              onMouseDown={(e) => {
                e.preventDefault(); // keep the textarea focused
                accept(command);
              }}
              onMouseEnter={() => setActive(i)}
            >
              <code className="name">{command.name}</code>
              {command.help && <span className="help">{command.help}</span>}
              {usageHint(command) && <code className="usage">{usageHint(command)}</code>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
});
