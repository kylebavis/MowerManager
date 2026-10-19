const $ = s => document.querySelector(s);
const fmt = t => new Date(t).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
// MOWING -> Mowing, SECONDARY_AREA -> Secondary area
const human = s => s ? s.charAt(0) + s.slice(1).toLowerCase().replaceAll('_', ' ') : '';

let toastTimer;
function toast(msg, error = false) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'show' + (error ? ' error' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.className = error ? 'error' : ''; }, error ? 6000 : 2500);
}
const fail = e => toast(e.message, true);

async function call(method, url, body) {
  const res = await fetch(url, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body && JSON.stringify(body) });
  if (res.status === 401) { location.href = '/login?returnUrl=' + encodeURIComponent(location.pathname); throw new Error('Signed out'); }
  if (!res.ok) {
    const text = await res.text();
    let msg = text || res.statusText;
    try { const p = JSON.parse(text); msg = p.errors ? Object.values(p.errors).flat().join(' ') : (p.title ?? text); } catch { }
    throw new Error(msg);
  }
  return res.status === 200 || res.status === 201 ? res.json() : null;
}

// Fills the shared top bar and returns /api/me.
async function loadMe() {
  const me = await call('GET', '/api/me');
  $('#me-name').textContent = me.name ?? '';
  $('#me-avatar').textContent = (me.name ?? '?').charAt(0);
  $('#nav-users').hidden = !me.manageUsers;
  return me;
}
