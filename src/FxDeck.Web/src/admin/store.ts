import { create } from "zustand";
import type { AppConfig, CommandCache, DeckKey, DeckProfile } from "../shared/types";
import { api, ApiError, type AdminStatus } from "./api";

export type SaveState = "idle" | "dirty" | "saving" | "saved" | "error";

const SAVE_DEBOUNCE_MS = 500;
const SELECTED_PROFILE_KEY = "fxdeck.admin.profile";

export const uuid = () => crypto.randomUUID();

export function newProfile(name = "Profile", order = 0): DeckProfile {
  return { id: uuid(), name, order, columns: 5, rows: 3, keys: [] };
}

export function newKey(row: number, col: number): DeckKey {
  return {
    id: uuid(),
    row,
    col,
    title: { text: "", position: "bottom", visible: true },
    background: "#2a2a2a",
    icon: null,
    action: { type: "command", command: "" },
    holdToConfirm: false,
  };
}

export interface AdminState {
  config: AppConfig | null;
  status: AdminStatus | null;
  loadError: string | null;
  save: SaveState;
  saveErrors: string[];
  restartRequired: boolean;
  selectedProfileId: string | null;
  selectedCell: { row: number; col: number } | null;
  /** Extracted command cache for the input assist (design memo §3.10); null until loaded. */
  commandCache: CommandCache | null;

  load(): Promise<void>;
  refreshStatus(): Promise<void>;
  /** Applies a mutation to a copy of the config, then auto-saves. */
  update(mutate: (config: AppConfig) => void): void;
  flush(): Promise<void>;
  selectProfile(id: string | null): void;
  selectCell(cell: { row: number; col: number } | null): void;
  /** Runs an extraction and stores the result; throws the localized ApiError when it fails. */
  extractCommands(): Promise<CommandCache>;
  clearCommands(): Promise<void>;
}

let saveTimer: number | null = null;
let saveChain: Promise<void> = Promise.resolve();

export const useAdminStore = create<AdminState>((set, get) => ({
  config: null,
  status: null,
  loadError: null,
  save: "idle",
  saveErrors: [],
  restartRequired: false,
  selectedProfileId: null,
  selectedCell: null,
  commandCache: null,

  async load() {
    // Non-fatal side load: the input assist works without it, so its failure must not block the app.
    api
      .commands()
      .then((cache) => set({ commandCache: cache }))
      .catch(() => undefined);
    try {
      const [config, status] = await Promise.all([api.config(), api.status()]);
      const sorted = { ...config, profiles: [...config.profiles].sort((a, b) => a.order - b.order) };
      let stored: string | null = null;
      try {
        stored = localStorage.getItem(SELECTED_PROFILE_KEY);
      } catch {
        /* ignore */
      }
      const selected = sorted.profiles.find((p) => p.id === stored)?.id ?? sorted.profiles[0]?.id ?? null;
      set({ config: sorted, status, loadError: null, restartRequired: status.restartRequired, selectedProfileId: selected, save: "idle", saveErrors: [] });
    } catch (error) {
      set({ loadError: error instanceof Error ? error.message : String(error) });
    }
  },

  async refreshStatus() {
    try {
      const status = await api.status();
      set((s) => ({ status, restartRequired: s.restartRequired || status.restartRequired }));
    } catch {
      set({ status: null });
    }
  },

  update(mutate) {
    const current = get().config;
    if (!current) return;
    const copy = structuredClone(current);
    mutate(copy);
    copy.profiles.forEach((p, i) => (p.order = i));
    set({ config: copy, save: "dirty" });
    if (saveTimer !== null) window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => void get().flush(), SAVE_DEBOUNCE_MS);
  },

  flush() {
    if (saveTimer !== null) {
      window.clearTimeout(saveTimer);
      saveTimer = null;
    }
    saveChain = saveChain.then(async () => {
      const { config, save } = get();
      if (!config || (save !== "dirty" && save !== "error")) return;
      set({ save: "saving" });
      try {
        const result = await api.saveConfig(config);
        set((s) => ({ save: s.save === "dirty" ? "dirty" : "saved", saveErrors: [], restartRequired: s.restartRequired || result.restartRequired }));
      } catch (error) {
        const errors = error instanceof ApiError && error.errors.length > 0 ? error.errors : [error instanceof Error ? error.message : String(error)];
        set({ save: "error", saveErrors: errors });
      }
    });
    return saveChain;
  },

  selectProfile(id) {
    try {
      if (id) localStorage.setItem(SELECTED_PROFILE_KEY, id);
    } catch {
      /* ignore */
    }
    set({ selectedProfileId: id, selectedCell: null });
  },

  selectCell(cell) {
    set({ selectedCell: cell });
  },

  async extractCommands() {
    const cache = await api.extractCommands();
    set({ commandCache: cache });
    return cache;
  },

  async clearCommands() {
    await api.clearCommands();
    set({ commandCache: { commands: [] } });
  },
}));

window.addEventListener("beforeunload", () => {
  if (useAdminStore.getState().save === "dirty") void useAdminStore.getState().flush();
});
