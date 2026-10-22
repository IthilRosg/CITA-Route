let active = false;
async function refresh() {
  const state = await chrome.runtime.sendMessage({ kind: 'state' }); active = state.enabled;
  document.getElementById('toggle').textContent = active ? 'Выключить' : 'Включить';
  document.getElementById('status').textContent = !active ? 'Адаптер выключен' : state.connected ? 'Адаптер включён' : 'Ожидание клиента CITA Route';
}
document.getElementById('toggle').addEventListener('click', async () => { await chrome.storage.local.set({ enabled: !active }); setTimeout(refresh, 300); });
void refresh();
