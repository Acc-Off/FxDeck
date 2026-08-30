import { t } from "../shared/i18n";

/** Image preparation for user icons (design memo §3.8): the browser shrinks to 256×256 PNG, the server normalises again. */

export const ASSET_SIZE = 256;

/**
 * Decodes any browser-supported image (PNG / JPEG / WebP / GIF first frame) and draws it centred, aspect ratio kept,
 * into a transparent 256×256 PNG. Doing this here keeps WebP support without a WebP decoder on the server.
 */
export async function toKeyImage(file: File): Promise<Blob> {
  const bitmap = await decode(file);
  try {
    const canvas = document.createElement("canvas");
    canvas.width = ASSET_SIZE;
    canvas.height = ASSET_SIZE;
    const context = canvas.getContext("2d");
    if (!context) throw new Error(t("image.noCanvas"));
    const scale = Math.min(ASSET_SIZE / bitmap.width, ASSET_SIZE / bitmap.height);
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    context.imageSmoothingEnabled = true;
    context.imageSmoothingQuality = "high";
    context.drawImage(bitmap, (ASSET_SIZE - width) / 2, (ASSET_SIZE - height) / 2, width, height);
    return await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error(t("image.toPngFailed")))), "image/png");
    });
  } finally {
    if ("close" in bitmap) bitmap.close();
  }
}

async function decode(file: File): Promise<ImageBitmap | HTMLImageElement> {
  if (typeof createImageBitmap === "function") {
    try {
      return await createImageBitmap(file);
    } catch {
      // fall through to the <img> route (older browsers, odd containers)
    }
  }
  const url = URL.createObjectURL(file);
  try {
    return await new Promise<HTMLImageElement>((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve(image);
      image.onerror = () => reject(new Error(t("image.decodeFailed", { name: file.name })));
      image.src = url;
    });
  } finally {
    URL.revokeObjectURL(url);
  }
}
