/* FxDeck service worker: keeps the app shell, hashed assets and icon fonts available offline. API calls always go to the network. */
const VERSION = "fxdeck-v2";
const SHELL = ["/deck/", "/manifest.webmanifest", "/icons/icon-192.png", "/icons/icon-512.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(VERSION)
      .then((cache) => cache.addAll(SHELL).catch(() => undefined))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  // Content-addressed user images are immutable too; everything else under /api/ always goes to the network.
  const immutable = url.pathname.startsWith("/assets/") || url.pathname.startsWith("/api/deck/assets/");
  if (url.pathname.startsWith("/api/") && !immutable) return;

  // Hashed build output and user images: cache first, forever.
  if (immutable) {
    event.respondWith(
      caches.open(VERSION).then(async (cache) => {
        const hit = await cache.match(request);
        if (hit) return hit;
        const response = await fetch(request);
        if (response.ok) cache.put(request, response.clone());
        return response;
      }),
    );
    return;
  }

  // Navigations and the rest of the shell: network first, cached copy when offline.
  event.respondWith(
    caches.open(VERSION).then(async (cache) => {
      try {
        const response = await fetch(request);
        if (response.ok && (request.mode === "navigate" || url.pathname.startsWith("/icons/") || url.pathname === "/manifest.webmanifest")) {
          cache.put(request.mode === "navigate" ? "/deck/" : request, response.clone());
        }
        return response;
      } catch (error) {
        const fallback = await cache.match(request.mode === "navigate" ? "/deck/" : request);
        if (fallback) return fallback;
        throw error;
      }
    }),
  );
});
