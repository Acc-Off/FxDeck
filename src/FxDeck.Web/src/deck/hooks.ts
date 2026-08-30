import { useEffect, useState, type RefObject } from "react";
import type { DeckSettings } from "../shared/types";

/** Applies `data-theme` to <html>; "system" follows prefers-color-scheme. */
export function useTheme(theme: DeckSettings["theme"]) {
  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const apply = () => {
      const resolved = theme === "system" ? (media.matches ? "dark" : "light") : theme;
      document.documentElement.dataset.theme = resolved;
      const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
      if (meta) meta.content = resolved === "dark" ? "#000000" : "#e6e6e6";
    };
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, [theme]);
}

/** Keeps the screen on while the deck is visible (Screen Wake Lock API, best effort). */
export function useWakeLock() {
  useEffect(() => {
    let lock: WakeLockSentinel | null = null;
    const request = async () => {
      if (!("wakeLock" in navigator) || document.visibilityState !== "visible") return;
      try {
        lock = await navigator.wakeLock.request("screen");
      } catch {
        /* not allowed (low battery, unsupported) */
      }
    };
    const onVisibility = () => {
      if (document.visibilityState === "visible") void request();
    };
    void request();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      void lock?.release();
    };
  }, []);
}

export interface Size {
  width: number;
  height: number;
}

/** Content box size of an element, updated by ResizeObserver. */
export function useElementSize(ref: RefObject<HTMLElement | null>): Size {
  const [size, setSize] = useState<Size>({ width: 0, height: 0 });
  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const update = () => setSize({ width: element.clientWidth, height: element.clientHeight });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, [ref]);
  return size;
}

/** True when running as an installed PWA (home screen). */
export function isStandalone(): boolean {
  return window.matchMedia("(display-mode: standalone)").matches || window.matchMedia("(display-mode: fullscreen)").matches || (navigator as { standalone?: boolean }).standalone === true;
}
