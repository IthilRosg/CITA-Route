// Isolated world. Only retry an already failed, unsigned, same-frame image GET.
const failed = new WeakSet(), retried = new WeakSet();
document.addEventListener('error', event => {
  if (event.target instanceof HTMLImageElement && !event.target.naturalWidth) failed.add(event.target);
}, true);
chrome.runtime.onMessage.addListener((message, sender) => {
  if (sender.id !== chrome.runtime.id || message?.kind !== 'routeReady' || typeof message.domain !== 'string' || !/^[a-f0-9]{64}$/.test(message.fingerprint)) return;
  for (const image of document.images) {
    if (!failed.has(image) || retried.has(image) || image.naturalWidth || !image.isConnected || image.srcset || image.closest('picture')) continue;
    try {
      const url = new URL(image.currentSrc || image.src);
      if (url.protocol !== 'https:' || url.hostname !== message.domain || url.search || url.hash || url.username || url.password || image.src !== url.href) continue;
      const original = url.href;
      crypto.subtle.digest('SHA-256', new TextEncoder().encode(original)).then(value => {
        const fingerprint = Array.from(new Uint8Array(value), byte => byte.toString(16).padStart(2, '0')).join('');
        if (fingerprint !== message.fingerprint || image.src !== original || image.naturalWidth || retried.has(image) || !image.isConnected) return;
        retried.add(image); image.src = original;
      }).catch(() => {});
    } catch {}
  }
});
