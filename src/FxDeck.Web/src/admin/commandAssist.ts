import type { CachedCommand } from "../shared/types";

/**
 * Helpers behind the command input assist (UIUX §5.3 / §5.6): the token-at-caret rules for the macro
 * notation (`;` / `;;` / `{500ms}`), suggestion ranking, and the default-hidden "auxiliary" heuristic.
 */

/**
 * Commands stored in the cache but hidden by default (design memo §3.10): `+x`/`-x` keybind halves,
 * `txAdmin:menu:*`-style internals, and convar-ish `txAdmin-*` entries. Display-side only — the cache keeps all.
 */
export function isAuxiliaryCommand(name: string): boolean {
  return /^[+-]/.test(name) || name.includes(":") || /^txadmin-/i.test(name);
}

/** `jail <id> [reason]` — usage hint from the params (`<required>` / `[optional]`). */
export function usageHint(command: CachedCommand): string | null {
  if (!command.params || command.params.length === 0) return null;
  const parts = command.params.map((p) => (p.optional ? `[${p.name}]` : `<${p.name}>`));
  return `${command.name} ${parts.join(" ")}`;
}

export interface CaretToken {
  /** Replace [start, end) with the picked name when accepting a suggestion. */
  start: number;
  end: number;
  /** What was typed so far (the filter). */
  prefix: string;
}

/**
 * The command-name token the caret sits in, or null when the caret is elsewhere (in arguments, or
 * inside a `{500ms}` delay). A macro segment starts after `;`, a line break or a `}`; its first
 * whitespace-delimited word is the command name — only that word is completed.
 */
export function tokenAtCaret(value: string, caret: number): CaretToken | null {
  let segmentStart = 0;
  for (let i = caret - 1; i >= 0; i--) {
    const c = value[i];
    if (c === ";" || c === "\n" || c === "\r" || c === "}") {
      segmentStart = i + 1;
      break;
    }
    if (c === "{") return null; // inside a {NNNms} delay
  }
  let start = segmentStart;
  while (start < caret && /\s/.test(value[start])) start++;
  const prefix = value.slice(start, caret);
  if (/\s/.test(prefix)) return null; // past the command word, into the arguments
  let end = caret;
  while (end < value.length && !/[\s;{]/.test(value[end])) end++;
  return { start, end, prefix };
}

/**
 * Candidates for a typed prefix: prefix matches first (auxiliary ones only surface this way — typing
 * `+` is explicit enough), then non-auxiliary substring matches.
 */
export function suggestCommands(commands: CachedCommand[], prefix: string, limit = 8): CachedCommand[] {
  const query = prefix.toLowerCase();
  if (!query) return [];
  const starts: CachedCommand[] = [];
  const startsAux: CachedCommand[] = [];
  const contains: CachedCommand[] = [];
  for (const command of commands) {
    const name = command.name.toLowerCase();
    if (name.startsWith(query)) {
      (isAuxiliaryCommand(command.name) ? startsAux : starts).push(command);
    } else if (name.includes(query) && !isAuxiliaryCommand(command.name)) {
      contains.push(command);
    }
  }
  return [...starts, ...startsAux, ...contains].slice(0, limit);
}

/** Short date+time for the "N commands, extracted at …" status lines. */
export function formatExtractedAt(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(undefined, { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
}
