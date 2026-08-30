import { create } from "zustand";
import type { DeckProfile, DeckSettings, GameState, HelloMessage } from "../shared/types";

export type SocketState = "connecting" | "open" | "closed" | "invalid";

const LAST_PROFILE_KEY = "fxdeck.lastProfile";

export interface Toast {
  id: number;
  text: string;
}

export interface DeckState {
  socket: SocketState;
  /** performance.now() when the socket was lost, for the "Reconnecting… (N s)" overlay. */
  closedSince: number | null;
  game: GameState;
  profiles: DeckProfile[];
  settings: DeckSettings;
  currentProfileId: string | null;
  /** keys whose macro is running (press → result). */
  running: Record<string, true>;
  /** current stage (0-based) of staged keys; absent = first stage (design memo §3.2). */
  stages: Record<string, number>;
  /** keys to flash red, keyed by id → timestamp. */
  flash: Record<string, number>;
  toast: Toast | null;
  /** Last CONSOLE_MAX lines of FiveM console output (UIUX §4.8). */
  consoleLines: string[];
  consoleOpen: boolean;

  applyHello(hello: HelloMessage): void;
  appendConsole(line: string): void;
  setConsoleOpen(open: boolean): void;
  setSocket(state: SocketState): void;
  setGame(game: GameState): void;
  setProfiles(profiles: DeckProfile[]): void;
  setSettings(settings: DeckSettings): void;
  selectProfile(id: string): void;
  stepProfile(delta: number): void;
  markRunning(keyId: string, running: boolean): void;
  setStage(keyId: string, stage: number): void;
  flashKey(keyId: string): void;
  showToast(text: string): void;
  clearToast(id: number): void;
}

let toastSeq = 0;

export const CONSOLE_MAX = 200;
const CONSOLE_OPEN_KEY = "fxdeck.consoleOpen";

function readConsoleOpen(): boolean {
  try {
    return localStorage.getItem(CONSOLE_OPEN_KEY) === "1";
  } catch {
    return false;
  }
}

function pickProfile(profiles: DeckProfile[], preferred: string | null): string | null {
  if (profiles.length === 0) return null;
  if (preferred && profiles.some((p) => p.id === preferred)) return preferred;
  let stored: string | null = null;
  try {
    stored = localStorage.getItem(LAST_PROFILE_KEY);
  } catch {
    /* storage unavailable */
  }
  if (stored && profiles.some((p) => p.id === stored)) return stored;
  return profiles[0].id;
}

function remember(id: string) {
  try {
    localStorage.setItem(LAST_PROFILE_KEY, id);
  } catch {
    /* storage unavailable */
  }
}

export const useDeckStore = create<DeckState>((set, get) => ({
  socket: "connecting",
  closedSince: null,
  game: "disconnected",
  profiles: [],
  settings: { theme: "dark", deckStatusBar: true, language: "auto" },
  currentProfileId: null,
  running: {},
  stages: {},
  flash: {},
  toast: null,
  consoleLines: [],
  consoleOpen: readConsoleOpen(),

  appendConsole(line) {
    set((s) => {
      const next = s.consoleLines.length >= CONSOLE_MAX ? s.consoleLines.slice(s.consoleLines.length - CONSOLE_MAX + 1) : s.consoleLines.slice();
      next.push(line);
      return { consoleLines: next };
    });
  },
  setConsoleOpen(open) {
    try {
      localStorage.setItem(CONSOLE_OPEN_KEY, open ? "1" : "0");
    } catch {
      /* storage unavailable */
    }
    set({ consoleOpen: open });
  },

  applyHello(hello) {
    const profiles = [...hello.profiles].sort((a, b) => a.order - b.order);
    set({
      profiles,
      settings: hello.settings,
      game: hello.game,
      currentProfileId: pickProfile(profiles, get().currentProfileId),
      running: {},
      stages: hello.stages ?? {},
    });
  },
  setSocket(socket) {
    set((s) => ({
      socket,
      // Count seconds only once a working connection was lost (not during the very first connect).
      closedSince: socket === "closed" ? (s.closedSince ?? performance.now()) : socket === "connecting" ? s.closedSince : null,
      running: socket === "open" ? s.running : {},
    }));
  },
  setGame(game) {
    set({ game });
  },
  setProfiles(list) {
    const profiles = [...list].sort((a, b) => a.order - b.order);
    set((s) => ({ profiles, currentProfileId: pickProfile(profiles, s.currentProfileId) }));
  },
  setSettings(settings) {
    set({ settings });
  },
  selectProfile(id) {
    if (get().profiles.some((p) => p.id === id)) {
      remember(id);
      set({ currentProfileId: id });
    }
  },
  stepProfile(delta) {
    const { profiles, currentProfileId } = get();
    if (profiles.length < 2) return;
    const index = Math.max(0, profiles.findIndex((p) => p.id === currentProfileId));
    const next = profiles[(index + delta + profiles.length) % profiles.length];
    remember(next.id);
    set({ currentProfileId: next.id });
  },
  markRunning(keyId, running) {
    set((s) => {
      const copy = { ...s.running };
      if (running) copy[keyId] = true;
      else delete copy[keyId];
      return { running: copy };
    });
  },
  setStage(keyId, stage) {
    set((s) => {
      const copy = { ...s.stages };
      if (stage > 0) copy[keyId] = stage;
      else delete copy[keyId];
      return { stages: copy };
    });
  },
  flashKey(keyId) {
    set((s) => ({ flash: { ...s.flash, [keyId]: Date.now() } }));
  },
  showToast(text) {
    const id = ++toastSeq;
    set({ toast: { id, text } });
    setTimeout(() => get().clearToast(id), 2500);
  },
  clearToast(id) {
    set((s) => (s.toast?.id === id ? { toast: null } : {}));
  },
}));
