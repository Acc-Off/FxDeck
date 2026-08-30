// Builds compact search indexes for the admin icon picker from the icon packages' metadata
// (design memo §3.8). Output: src/generated/icons-{mdi,fa,emoji}.json, loaded lazily by the picker.
// Runs automatically before `npm run build` / `npm run dev` (see package.json).
import { createRequire } from "node:module";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const outDir = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "generated");
mkdirSync(outDir, { recursive: true });

// MDI: [name, aliases..., tags...] — everything is searchable text.
const mdi = require("@mdi/svg/meta.json")
  .filter((icon) => !icon.deprecated)
  .map((icon) => ({ n: icon.name, a: icon.aliases ?? [], t: icon.tags ?? [] }));

// Font Awesome Free: only free styles of the classic family.
const faFamilies = require("@fortawesome/fontawesome-free/metadata/icon-families.json");
const fa = Object.entries(faFamilies)
  .map(([name, icon]) => {
    const styles = (icon.familyStylesByLicense?.free ?? []).filter((s) => s.family === "classic").map((s) => s.style);
    if (styles.length === 0) return null;
    return { n: name, l: icon.label ?? name, s: styles, a: icon.aliases?.names ?? [], t: icon.search?.terms ?? [] };
  })
  .filter(Boolean);

// Emoji: Japanese and English labels/tags side by side; skin-tone variants are dropped.
const ja = require("emojibase-data/ja/compact.json");
const en = require("emojibase-data/en/compact.json");
const enByHex = new Map(en.map((e) => [e.hexcode, e]));
const emoji = ja
  .filter((e) => e.group !== undefined && e.group !== 2) // 2 = component (skin tones, hair)
  .map((e) => {
    const other = enByHex.get(e.hexcode);
    return { u: e.unicode, g: e.group, l: e.label, le: other?.label ?? "", t: [...(e.tags ?? []), ...(other?.tags ?? [])] };
  });

writeFileSync(join(outDir, "icons-mdi.json"), JSON.stringify(mdi));
writeFileSync(join(outDir, "icons-fa.json"), JSON.stringify(fa));
writeFileSync(join(outDir, "icons-emoji.json"), JSON.stringify(emoji));
console.log(`icon index: mdi ${mdi.length}, fa ${fa.length}, emoji ${emoji.length}`);
