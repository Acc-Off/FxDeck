import type { HelloMessage } from "./types";

export type SessionExchange = "ok" | "invalid" | "rateLimited" | "error";

/** Exchanges the QR token (`?t=`) for the session cookie. */
export async function exchangeToken(token: string): Promise<SessionExchange> {
  try {
    const response = await fetch(`/api/deck/session?t=${encodeURIComponent(token)}`, { method: "POST", credentials: "same-origin" });
    if (response.ok) return "ok";
    if (response.status === 401) return "invalid";
    if (response.status === 429) return "rateLimited";
    return "error";
  } catch {
    return "error";
  }
}

export type ProfileFetch = { kind: "ok"; hello: HelloMessage } | { kind: "unauthorized" } | { kind: "offline" };

/** Also doubles as the cheap "is my cookie still valid?" probe before opening the WebSocket. */
export async function fetchProfile(): Promise<ProfileFetch> {
  try {
    const response = await fetch("/api/deck/profile", { credentials: "same-origin", cache: "no-store" });
    if (response.status === 401) return { kind: "unauthorized" };
    if (!response.ok) return { kind: "offline" };
    return { kind: "ok", hello: (await response.json()) as HelloMessage };
  } catch {
    return { kind: "offline" };
  }
}

export function webSocketUrl(): string {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${location.host}/api/deck/ws`;
}
