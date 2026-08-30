import { useCallback } from "react";
import { create } from "zustand";
import { ja, type MessageKey } from "./locales/ja";

/** Concrete UI languages; `settings.language` may also be "auto" (design memo §3.9). */
export type Lang = "ja" | "en";
export type LanguageSetting = "auto" | Lang;
export type Params = Record<string, string | number>;

/** "auto" follows the browser: any ja* language → Japanese, otherwise English. */
export function detectLanguage(setting: LanguageSetting | undefined, languages: readonly string[] = navigator.languages ?? [navigator.language]): Lang {
  if (setting === "ja" || setting === "en") return setting;
  return languages.some((l) => l.toLowerCase().startsWith("ja")) ? "ja" : "en";
}

type Dictionary = Record<MessageKey, string>;
const dictionaries: Partial<Record<Lang, Dictionary>> = { ja };

async function loadDictionary(lang: Lang): Promise<Dictionary> {
  const cached = dictionaries[lang];
  if (cached) return cached;
  const loaded = (await import("./locales/en")).en; // only English is lazy; Japanese is the built-in fallback
  dictionaries.en = loaded;
  return loaded;
}

export function format(template: string, params?: Params): string {
  return params ? template.replace(/\{(\w+)\}/g, (match, name: string) => (name in params ? String(params[name]) : match)) : template;
}

interface I18nState {
  lang: Lang;
  dict: Dictionary;
  /** Applies a language setting; the English dictionary is fetched on first use. */
  setLanguage(setting: LanguageSetting | undefined): void;
}

let generation = 0;

export const useI18nStore = create<I18nState>((set) => ({
  lang: "ja",
  dict: ja,
  setLanguage(setting) {
    const lang = detectLanguage(setting);
    const current = ++generation;
    const apply = (dict: Dictionary) => {
      if (current !== generation) return; // a newer setting won
      document.documentElement.lang = lang;
      set({ lang, dict });
    };
    const ready = dictionaries[lang];
    if (ready) apply(ready);
    else void loadDictionary(lang).then(apply).catch(() => apply(ja));
  },
}));

/** Translate outside React (socket handlers, helpers). */
export function t(key: MessageKey, params?: Params): string {
  const { dict } = useI18nStore.getState();
  return format(dict[key] ?? ja[key] ?? key, params);
}

/** Translate inside a component; re-renders when the language changes. */
export function useT(): (key: MessageKey, params?: Params) => string {
  const dict = useI18nStore((s) => s.dict);
  return useCallback((key: MessageKey, params?: Params) => format(dict[key] ?? ja[key] ?? key, params), [dict]);
}

export type { MessageKey };
