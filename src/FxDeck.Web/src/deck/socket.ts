import { fetchProfile, webSocketUrl } from "../shared/api";
import { TOKEN_REVOKED_CLOSE_CODE, type ClientMessage, type ServerMessage } from "../shared/types";
import { t, type MessageKey } from "../shared/i18n";
import { useDeckStore } from "./store";

const RUNNING_SAFETY_MS = 70_000; // a macro can wait up to 60 s; never leave a key stuck

/** One WebSocket to the PC with automatic reconnection. */
export class DeckSocket {
  private socket: WebSocket | null = null;
  private retryTimer: number | null = null;
  private attempt = 0;
  private stopped = false;
  private runningTimers = new Map<string, number>();

  start() {
    this.stopped = false;
    void this.connect();
  }

  stop() {
    this.stopped = true;
    if (this.retryTimer !== null) window.clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.socket?.close();
    this.socket = null;
  }

  /** Reconnect right away (after the tab comes back to the foreground). */
  poke() {
    if (this.stopped) return;
    if (this.socket && this.socket.readyState === WebSocket.OPEN) return;
    if (this.retryTimer !== null) {
      window.clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
    this.attempt = 0;
    void this.connect();
  }

  press(keyId: string): boolean {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return false;
    const store = useDeckStore.getState();
    if (store.running[keyId]) return false;
    store.markRunning(keyId, true);
    this.runningTimers.set(
      keyId,
      window.setTimeout(() => this.finishKey(keyId), RUNNING_SAFETY_MS),
    );
    const message: ClientMessage = { type: "press", keyId };
    this.socket.send(JSON.stringify(message));
    return true;
  }

  /** Hold keys only (design memo §3.2): the finger lifted or the gesture was cancelled. Never blocked by the running guard. */
  release(keyId: string): boolean {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return false;
    const message: ClientMessage = { type: "release", keyId };
    this.socket.send(JSON.stringify(message));
    return true;
  }

  private async connect() {
    if (this.stopped || this.socket) return;
    const store = useDeckStore.getState();
    store.setSocket("connecting");

    // Probe first: the WebSocket API cannot tell a 401 from a dead server.
    const probe = await fetchProfile();
    if (this.stopped) return;
    if (probe.kind === "unauthorized") {
      store.setSocket("invalid");
      return;
    }
    if (probe.kind === "offline") {
      this.scheduleRetry();
      return;
    }
    store.applyHello(probe.hello);

    const socket = new WebSocket(webSocketUrl());
    this.socket = socket;
    socket.onopen = () => {
      this.attempt = 0;
      useDeckStore.getState().setSocket("open");
    };
    socket.onmessage = (event) => this.handle(event.data as string);
    socket.onerror = () => {
      /* onclose follows */
    };
    socket.onclose = (event) => {
      if (this.socket !== socket) return;
      this.socket = null;
      this.clearRunning();
      if (event.code === TOKEN_REVOKED_CLOSE_CODE) {
        useDeckStore.getState().setSocket("invalid");
        return;
      }
      useDeckStore.getState().setSocket("closed");
      this.scheduleRetry();
    };
  }

  private scheduleRetry() {
    if (this.stopped || this.retryTimer !== null) return;
    const delay = Math.min(1000 * 2 ** this.attempt, 5000);
    this.attempt++;
    useDeckStore.getState().setSocket("closed");
    this.retryTimer = window.setTimeout(() => {
      this.retryTimer = null;
      void this.connect();
    }, delay);
  }

  private handle(raw: string) {
    let message: ServerMessage;
    try {
      message = JSON.parse(raw) as ServerMessage;
    } catch {
      return;
    }
    const store = useDeckStore.getState();
    switch (message.type) {
      case "hello":
        store.applyHello(message);
        break;
      case "status":
        store.setGame(message.game);
        break;
      case "profiles":
        store.setProfiles(message.profiles);
        break;
      case "settings":
        store.setSettings(message.settings);
        break;
      case "stage":
        store.setStage(message.keyId, message.stage);
        break;
      case "result":
        if (message.phase !== "release") this.finishKey(message.keyId);
        if (!message.success) {
          store.flashKey(message.keyId);
          store.showToast(describeFailure(message.reason));
        }
        break;
      case "console":
        store.appendConsole(message.line);
        break;
    }
  }

  private finishKey(keyId: string) {
    const timer = this.runningTimers.get(keyId);
    if (timer !== undefined) window.clearTimeout(timer);
    this.runningTimers.delete(keyId);
    useDeckStore.getState().markRunning(keyId, false);
  }

  private clearRunning() {
    for (const timer of this.runningTimers.values()) window.clearTimeout(timer);
    this.runningTimers.clear();
  }
}

const FAILURE_KEYS: Partial<Record<string, MessageKey>> = {
  notConnected: "deck.failure.notConnected",
  invalidCommand: "deck.failure.invalidCommand",
  unknownKey: "deck.failure.unknownKey",
  noCommand: "deck.failure.noCommand",
};

export function describeFailure(reason: string): string {
  return t(FAILURE_KEYS[reason] ?? "deck.failure.generic");
}
