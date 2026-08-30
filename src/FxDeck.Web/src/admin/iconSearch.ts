import { t } from "../shared/i18n";
import type { KeyIcon } from "../shared/types";

/** "image" is handled by the picker itself (uploaded images are listed, not searched). */
export type IconTab = "all" | "mdi" | "fa" | "emoji" | "image";

export interface IconHit {
  icon: KeyIcon;
  /** Stable key for React lists. */
  key: string;
  label: string;
}

interface MdiEntry {
  n: string;
  a: string[];
  t: string[];
}

interface FaEntry {
  n: string;
  l: string;
  s: ("solid" | "regular" | "brands")[];
  a: string[];
  t: string[];
}

interface EmojiEntry {
  u: string;
  g: number;
  l: string;
  le: string;
  t: string[];
}

interface Indexed {
  hit: IconHit;
  haystack: string;
}

let loaded: Promise<Indexed[]> | null = null;

/** Loads the generated indexes (≈1 MB total) the first time the picker opens. */
export function loadIconIndex(): Promise<Indexed[]> {
  loaded ??= Promise.all([
    import("../generated/icons-mdi.json"),
    import("../generated/icons-fa.json"),
    import("../generated/icons-emoji.json"),
  ]).then(([mdi, fa, emoji]) => {
    const items: Indexed[] = [];
    for (const e of mdi.default as MdiEntry[]) {
      items.push({ hit: { icon: { type: "mdi", name: e.n }, key: `mdi:${e.n}`, label: e.n }, haystack: [e.n, ...e.a, ...e.t].join(" ").toLowerCase() });
    }
    for (const e of fa.default as FaEntry[]) {
      for (const style of e.s) {
        items.push({
          hit: { icon: { type: "fa", style, name: e.n }, key: `fa:${style}:${e.n}`, label: `${e.l} (${style})` },
          haystack: [e.n, e.l, ...e.a, ...e.t].join(" ").toLowerCase(),
        });
      }
    }
    for (const e of emoji.default as EmojiEntry[]) {
      items.push({ hit: { icon: { type: "emoji", value: e.u }, key: `emoji:${e.u}`, label: e.l }, haystack: [e.l, e.le, ...e.t].join(" ").toLowerCase() });
    }
    return items;
  });
  return loaded;
}

export function searchIcons(index: Indexed[], query: string, tab: IconTab, limit = 400): IconHit[] {
  const terms = query
    .toLowerCase()
    .split(/\s+/)
    .map((t) => t.trim())
    .filter(Boolean);
  const results: IconHit[] = [];
  for (const item of index) {
    if (tab !== "all" && item.hit.icon.type !== tab) continue;
    if (terms.length > 0 && !terms.every((t) => item.haystack.includes(t))) continue;
    results.push(item.hit);
    if (results.length >= limit) break;
  }
  return results;
}

const RECENT_KEY = "fxdeck.admin.recentIcons";
const RECENT_MAX = 12;

export function loadRecentIcons(): KeyIcon[] {
  try {
    const raw = localStorage.getItem(RECENT_KEY);
    return raw ? (JSON.parse(raw) as KeyIcon[]) : [];
  } catch {
    return [];
  }
}

/** Drops recent image icons whose file no longer exists (after "Delete unused images"); returns the kept list. */
export function pruneRecentImages(existingHashes: Set<string>): KeyIcon[] {
  const kept = loadRecentIcons().filter((icon) => icon.type !== "image" || existingHashes.has(icon.hash));
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(kept));
  } catch {
    /* ignore */
  }
  return kept;
}

export function pushRecentIcon(icon: KeyIcon) {
  const key = iconKey(icon);
  const next = [icon, ...loadRecentIcons().filter((i) => iconKey(i) !== key)].slice(0, RECENT_MAX);
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(next));
  } catch {
    /* ignore */
  }
}

export function iconKey(icon: KeyIcon | null | undefined): string {
  if (!icon) return "";
  switch (icon.type) {
    case "mdi":
      return `mdi:${icon.name}`;
    case "fa":
      return `fa:${icon.style}:${icon.name}`;
    case "emoji":
      return `emoji:${icon.value}`;
    case "image":
      return `image:${icon.hash}`;
  }
}

export function describeIcon(icon: KeyIcon | null | undefined): string {
  if (!icon) return t("icon.none");
  switch (icon.type) {
    case "mdi":
      return `MDI: ${icon.name}`;
    case "fa":
      return `Font Awesome: ${icon.name} (${icon.style})`;
    case "emoji":
      return t("icon.emoji", { value: icon.value });
    case "image":
      return t("icon.image");
  }
}
