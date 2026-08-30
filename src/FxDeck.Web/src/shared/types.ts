// Mirrors the C# models (FxDeck.Config / FxDeck.Web.DeckMessages). Property names are camelCase on the wire.

export type GameState = "disconnected" | "connecting" | "connected";

export type KeyIcon =
  | { type: "mdi"; name: string }
  | { type: "fa"; style: "solid" | "regular" | "brands"; name: string }
  | { type: "emoji"; value: string }
  | { type: "image"; hash: string };

export interface KeyTitle {
  text: string;
  position: "top" | "middle" | "bottom";
  visible: boolean;
}

/** One of the extra stages of a key (design memo §3.2): a full look plus its own macros. */
export interface KeyStage {
  title: KeyTitle;
  background: string;
  icon?: KeyIcon | null;
  command?: string | null;
  releaseCommand?: string | null;
}

export interface KeyAction {
  type: "command" | string;
  /** Sent on tap; with `releaseCommand` set it is sent on pointer-down instead. */
  command?: string | null;
  /** Sent when the finger lifts; makes the key a "hold key". */
  releaseCommand?: string | null;
  /** Stages 2..5. Stage 1 is the key itself. */
  stages?: KeyStage[] | null;
}

/** Maximum number of stages a key can have, including the key itself. */
export const MAX_STAGES = 5;

/** The look and macros of stage `stage` (0 = the key itself). */
export function stageOf(key: DeckKey, stage: number): KeyStage {
  const extra = key.action.stages ?? [];
  if (stage > 0 && stage <= extra.length) return extra[stage - 1];
  return { title: key.title, background: key.background, icon: key.icon, command: key.action.command, releaseCommand: key.action.releaseCommand };
}

export function stageCount(key: DeckKey): number {
  return 1 + (key.action.stages?.length ?? 0);
}

export interface DeckKey {
  id: string;
  row: number;
  col: number;
  title: KeyTitle;
  background: string;
  icon?: KeyIcon | null;
  action: KeyAction;
  holdToConfirm: boolean;
}

export interface DeckProfile {
  id: string;
  name: string;
  order: number;
  columns: number;
  rows: number;
  keys: DeckKey[];
}

export interface DeckSettings {
  theme: "dark" | "light" | "system";
  deckStatusBar: boolean;
  /** auto | ja | en (design memo §3.9). Older servers may omit it. */
  language?: "auto" | "ja" | "en";
}

/** Whole config.json (design memo §4). */
export interface AppSettings {
  game: { host: string; port: number };
  adminPort: number;
  deckPort: number;
  lanAdapter?: string | null;
  tunnel: { mode: "off" | "try" | "named"; namedToken?: string | null; namedUrl?: string | null; autoStart: boolean };
  autoStart: boolean;
  theme: DeckSettings["theme"];
  language: "auto" | "ja" | "en";
  deckStatusBar: boolean;
}

export interface AppConfig {
  version: number;
  settings: AppSettings;
  profiles: DeckProfile[];
}

export interface HelloMessage {
  type: "hello";
  profiles: DeckProfile[];
  settings: DeckSettings;
  game: GameState;
  /** Keys currently on a stage other than the first (0-based). Older servers omit it. */
  stages?: Record<string, number>;
}

export type ServerMessage =
  | HelloMessage
  | { type: "status"; game: GameState }
  | { type: "result"; keyId: string; phase?: "press" | "release"; success: boolean; reason: string; message?: string }
  | { type: "profiles"; profiles: DeckProfile[] }
  | { type: "settings"; settings: DeckSettings }
  | { type: "stage"; keyId: string; stage: number }
  | { type: "console"; line: string };

export type ClientMessage = { type: "press" | "release"; keyId: string };

/** Close code the server uses when the token was rotated. */
export const TOKEN_REVOKED_CLOSE_CODE = 4001;
