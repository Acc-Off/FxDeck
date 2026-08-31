import type { AppConfig, CommandCache, GameState } from "../shared/types";

export interface AdminStatus {
  game: GameState;
  gameEndpoint: string;
  adminPort: number;
  deckPort: number;
  lanAddress: string | null;
  deckUrl: string | null;
  deckUrlWithoutToken: string | null;
  connectedDecks: number;
  tunnel: TunnelStatus;
  dataDirectory: string;
  configPath: string;
  restartRequired: boolean;
}

/** `status.tunnel` (design memo §3.3). `mode`/`autoStart` are the settings; the rest is the live state. */
export interface TunnelStatus {
  mode: "off" | "try" | "named";
  autoStart: boolean;
  status: "stopped" | "starting" | "running" | "error";
  /** Mode the running/starting/failed tunnel was started with ("try" when the setting is "off"). */
  activeMode: "try" | "named" | null;
  /** Public origin without a trailing slash; null unless running (or when the named URL is not configured). */
  url: string | null;
  deckUrl: string | null;
  error: { phase: "download" | "start" | "exited"; message: string; exitCode?: number | null } | null;
}

export interface FirewallStatus {
  ruleExists: boolean;
  portAllowed: boolean;
  /** A block rule (created when the first-run prompt was cancelled) overrides any allow rule. */
  blocked: boolean;
  port: number;
  ruleName: string;
}

export interface Adapters {
  selected: string | null;
  automatic: string | null;
  adapters: { id: string; name: string; address: string; hasGateway: boolean }[];
}

/** One stored user image (design memo §3.8). */
export interface AssetInfo {
  hash: string;
  size: number;
  modified: string;
  /** Some key currently uses it. */
  referenced: boolean;
}

export interface AboutInfo {
  name: string;
  version: string;
  license: string;
  repository: string;
  thirdPartyNotices: string;
}

export interface SendResult {
  success: boolean;
  reason: string;
  stepsCompleted: number;
  stepCount: number;
  message?: string;
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public errors: string[] = [],
    /** Machine-readable reason (e.g. the extraction's gameNotRunning / notInSession / chatUnavailable). */
    public code?: string,
  ) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, { credentials: "same-origin", cache: "no-store", ...init });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    let errors: string[] = [];
    let code: string | undefined;
    try {
      const body = (await response.json()) as { error?: string; errors?: string[]; code?: string };
      if (body.error) message = body.error;
      if (body.errors) {
        errors = body.errors;
        if (!body.error) message = body.errors.join("\n");
      }
      code = body.code;
    } catch {
      /* not json */
    }
    throw new ApiError(response.status, message, errors, code);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

export const api = {
  status: () => request<AdminStatus>("/api/admin/status"),
  config: () => request<AppConfig>("/api/admin/config"),
  saveConfig: (config: AppConfig) => request<{ ok: boolean; restartRequired: boolean }>("/api/admin/config", json("PUT", config)),
  rotateToken: () => request<{ ok: boolean }>("/api/admin/token/rotate", { method: "POST" }),
  send: (command: string) => request<SendResult>("/api/admin/send", json("POST", { command })),
  exportUrl: (profileId?: string) => (profileId ? `/api/admin/export?profile=${encodeURIComponent(profileId)}` : "/api/admin/export"),
  import: (file: File, mode: "profile" | "all") => {
    const form = new FormData();
    form.append("file", file);
    return request<{ ok: boolean; profilesAdded: number; warnings: string[] }>(`/api/admin/import?mode=${mode}`, { method: "POST", body: form });
  },
  assets: () => request<{ assets: AssetInfo[] }>("/api/admin/assets"),
  uploadAsset: (image: Blob, name: string) => {
    const form = new FormData();
    form.append("file", image, name);
    return request<{ hash: string }>("/api/admin/assets", { method: "POST", body: form });
  },
  pruneAssets: () => request<{ deleted: number }>("/api/admin/assets/prune", { method: "POST" }),
  firewallStatus: () => request<FirewallStatus>("/api/admin/firewall/status"),
  firewallAllow: () => request<{ outcome: "added" | "cancelled" | "failed"; message: string | null; port: number }>("/api/admin/firewall/allow", { method: "POST" }),
  adapters: () => request<Adapters>("/api/admin/network/adapters"),
  gameTest: (host: string, port: number) => request<{ ok: boolean; message: string }>("/api/admin/game/test", json("POST", { host, port })),
  autoStart: () => request<{ enabled: boolean; command: string }>("/api/admin/autostart"),
  setAutoStart: (enabled: boolean) => request<{ enabled: boolean }>("/api/admin/autostart", json("PUT", { enabled })),
  restart: () => request<{ ok: boolean }>("/api/admin/restart", { method: "POST" }),
  about: () => request<AboutInfo>("/api/admin/about"),
  tunnelStart: () => request<{ tunnel: TunnelStatus }>("/api/admin/tunnel/start", { method: "POST" }),
  tunnelStop: () => request<{ tunnel: TunnelStatus }>("/api/admin/tunnel/stop", { method: "POST" }),
  commands: () => request<CommandCache>("/api/admin/commands"),
  extractCommands: () => request<CommandCache>("/api/admin/commands/extract", { method: "POST" }),
  clearCommands: () => request<{ ok: boolean }>("/api/admin/commands", { method: "DELETE" }),
  qrUrl: (cacheBust: number, kind: "lan" | "tunnel" = "lan") => `/api/admin/qr?kind=${kind}&v=${cacheBust}`,
};
