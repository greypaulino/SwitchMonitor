namespace SwitchMonitor.App.Services;

/// <summary>
/// The control page a phone's browser gets from this computer (http://&lt;computer&gt;:47820/). It is the protocol test
/// bench for the Android app and already works on its own: pair with the code from Settings, then brightness, link,
/// profiles, picture modes and inputs, kept up to date as they change on the computer.
/// </summary>
internal static class PhonePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#1c1c1c">
<title>SwitchMonitor</title>
<style>
  :root { --ground:#161616; --card:#232323; --raised:#2e2e2e; --line:#ffffff1f; --text:#f2f2f2; --muted:#a8a8a8; --green:#4cd97b; --warn:#ffd27a; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--ground); color:var(--text); font:15px/1.4 "Segoe UI", Roboto, system-ui, sans-serif; padding:16px 16px 40px; }
  h1 { font-size:20px; margin:4px 0 2px; } .muted { color:var(--muted); font-size:13px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:18px; padding:14px; margin-top:14px; }
  .row { padding:10px 8px; border-radius:12px; } .row.sel { background:var(--raised); }
  .head { display:flex; align-items:center; gap:8px; } .head .name { flex:1; font-weight:600; } .dot { width:8px; height:8px; border-radius:50%; background:#5a5a5a; } .sel .dot, .linked .dot { background:var(--green); }
  .value { min-width:46px; text-align:right; font-variant-numeric:tabular-nums; color:var(--muted); }
  input[type=range] { width:100%; margin-top:10px; accent-color:#f2f2f2; height:28px; }
  button, .chip { background:var(--raised); color:var(--text); border:1px solid var(--line); border-radius:999px; padding:9px 14px; font:inherit; }
  button.on, .chip.on { background:#f2f2f2; color:#141414; font-weight:600; }
  .chips { display:flex; flex-wrap:wrap; gap:8px; margin-top:10px; }
  .badge { width:40px; height:40px; border-radius:50%; display:inline-flex; align-items:center; justify-content:center; font-weight:700; color:#141414; border:2px solid transparent; }
  .badge.on { outline:2px solid #fff; outline-offset:2px; }
  .profile { display:flex; flex-direction:column; align-items:center; gap:4px; font-size:12px; color:var(--muted); background:none; border:0; padding:0 4px; }
  .mini { font-size:12px; padding:5px 10px; }
  button { -webkit-user-select:none; user-select:none; -webkit-touch-callout:none; touch-action:manipulation; }
  .chip.armed { background:var(--warn); color:#141414; font-weight:600; }
  .err { color:var(--warn); margin-top:10px; min-height:18px; }
  input[type=text] { background:var(--raised); color:var(--text); border:1px solid var(--line); border-radius:12px; padding:12px; font:inherit; font-size:22px; letter-spacing:6px; width:100%; text-align:center; }
  .hidden { display:none; }
</style>
</head>
<body>
  <h1 id="title">SwitchMonitor</h1>
  <div class="muted" id="sub">Connecting…</div>

  <div class="card hidden" id="pairCard">
    <div style="font-weight:600">Pair this phone</div>
    <div class="muted">On the computer: SwitchMonitor &gt; Settings &gt; Phone &gt; Pair a phone. Type the 6-digit code here.</div>
    <input type="text" id="code" inputmode="numeric" maxlength="6" placeholder="000000" style="margin-top:12px">
    <button style="margin-top:12px; width:100%" onclick="pair()">Pair</button>
  </div>

  <div id="main" class="hidden">
    <div class="card">
      <div class="head"><div class="name">Brightness</div><button id="link" class="mini" onclick="send({type:'link', on:!state.linked})">Link</button></div>
      <div id="displays"></div>
      <div class="muted" style="margin-top:6px">M: main display · hold M: preferred main (★)</div>
    </div>
    <div class="card"><div style="font-weight:600">Profiles</div><div class="chips" id="profiles"></div>
      <div id="modesBox" class="hidden"><div class="muted" style="margin-top:12px">Picture mode · <span id="modesFor"></span></div><div class="chips" id="modes"></div></div>
    </div>
    <div id="inputs"></div>
  </div>
  <div class="err" id="err"></div>

<script>
  // SHA-256 and HMAC in plain JavaScript: the page is served over http on the home network, where browsers do not
  // offer crypto.subtle. Used only to answer the computer's challenge without sending the token.
  const K256 = new Uint32Array([0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2]);
  function sha256(data) {
    const rot = (x, n) => (x >>> n) | (x << (32 - n));
    const len = data.length, size = ((len + 9 + 63) >> 6) << 6, m = new Uint8Array(size);
    m.set(data); m[len] = 0x80;
    const view = new DataView(m.buffer); view.setUint32(size - 4, len * 8);
    const h = [0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19], w = new Uint32Array(64);
    for (let i = 0; i < size; i += 64) {
      for (let t = 0; t < 16; t++) w[t] = view.getUint32(i + t * 4);
      for (let t = 16; t < 64; t++) {
        const a = w[t - 15], c = w[t - 2];
        w[t] = (w[t - 16] + (rot(a, 7) ^ rot(a, 18) ^ (a >>> 3)) + w[t - 7] + (rot(c, 17) ^ rot(c, 19) ^ (c >>> 10))) >>> 0;
      }
      let [a, b, c, d, e, f, g, k] = h;
      for (let t = 0; t < 64; t++) {
        const t1 = (k + (rot(e, 6) ^ rot(e, 11) ^ rot(e, 25)) + ((e & f) ^ (~e & g)) + K256[t] + w[t]) >>> 0;
        const t2 = ((rot(a, 2) ^ rot(a, 13) ^ rot(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
        k = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
      }
      [a, b, c, d, e, f, g, k].forEach((x, j) => h[j] = (h[j] + x) >>> 0);
    }
    const out = new Uint8Array(32), ov = new DataView(out.buffer);
    h.forEach((x, j) => ov.setUint32(j * 4, x));
    return out;
  }
  function hmac(key, message) {
    if (key.length > 64) key = sha256(key);
    const inner = new Uint8Array(64 + message.length), outer = new Uint8Array(96);
    for (let i = 0; i < 64; i++) { const k = key[i] || 0; inner[i] = k ^ 0x36; outer[i] = k ^ 0x5c; }
    inner.set(message, 64); outer.set(sha256(inner), 64);
    return sha256(outer);
  }
  const utf8 = s => new TextEncoder().encode(s);
  const b64 = bytes => btoa(String.fromCharCode(...bytes));
  const proof = (token, text) => b64(hmac(utf8(token), utf8(text)));
  let ws, state = null, token = localStorage.getItem('sm-token'), dragging = null, pending = false, timer = null, expected = null;
  // While a finger is on a slider the page is not redrawn (that would drop the slider); it catches up on release.
  function release() { dragging = null; if (pending) { pending = false; render(); } }
  document.addEventListener('pointerup', () => setTimeout(release, 300));
  document.addEventListener('touchend', () => setTimeout(release, 300));
  const $ = id => document.getElementById(id);
  function send(m) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(m)); }
  function connect() {
    ws = new WebSocket(`ws://${location.host}/ws`);
    ws.onopen = () => { $('sub').textContent = 'Connected'; };
    ws.onclose = () => { $('sub').textContent = 'Not connected · retrying…'; setTimeout(connect, 2000); };
    ws.onmessage = e => {
      const m = JSON.parse(e.data);
      // The computer's challenge: prove the token without sending it, and ask the computer to prove it back.
      if (m.type === 'challenge') {
        if (!token) { showPair(); return; }
        const mine = b64(crypto.getRandomValues(new Uint8Array(32)));
        expected = proof(token, `computer:${mine}:${m.nonce}`);
        send({type:'hello', nonce: mine, proof: proof(token, `phone:${m.nonce}:${mine}`)});
        return;
      }
      if (m.type === 'welcome' && m.proof !== expected) {
        $('err').textContent = 'This computer could not prove it is the one this page was paired with.';
        ws.onclose = null; ws.close(); return;
      }
      if (m.type === 'paired') { token = m.token; localStorage.setItem('sm-token', token); }
      if (m.type === 'paired' || m.type === 'welcome') { $('title').textContent = m.computer; $('pairCard').classList.add('hidden'); $('main').classList.remove('hidden'); $('err').textContent = ''; }
      if (m.type === 'state') { state = m; if (dragging) pending = true; else render(); }
      if (m.type === 'error') { $('err').textContent = m.error; if (m.code === 'unpaired') { localStorage.removeItem('sm-token'); token = null; showPair(); } }
    };
  }
  function showPair() { $('main').classList.add('hidden'); $('pairCard').classList.remove('hidden'); }
  function pair() { send({type:'pair', code:$('code').value, name: navigator.userAgent.includes('Android') ? 'Android phone (browser)' : 'Browser'}); }
  function setLevel(id, value, label) {
    label.textContent = value + '%';
    clearTimeout(timer); timer = setTimeout(() => send({type:'brightness', display:id, value:+value}), 60);
  }
  // Tap or hold (half a second), like the app's M button. Works with a finger and a mouse alike; no context menu.
  function hold(button, onTap, onHold) {
    let t = null, held = false;
    button.addEventListener('pointerdown', () => { held = false; clearTimeout(t); t = setTimeout(() => { held = true; if (navigator.vibrate) navigator.vibrate(20); onHold(); }, 500); });
    const stop = () => clearTimeout(t);
    button.addEventListener('pointerup', stop); button.addEventListener('pointerleave', stop); button.addEventListener('pointercancel', stop);
    button.addEventListener('contextmenu', ev => ev.preventDefault());
    button.addEventListener('click', ev => { if (held) { held = false; ev.preventDefault(); return; } onTap(); });
  }
  // An input switch needs a second tap within 3 seconds (the page cannot rely on confirm(), which some browsers block).
  let armed = null, armTimer = null;
  function arm(key) { armed = key; clearTimeout(armTimer); armTimer = setTimeout(() => { armed = null; if (state) render(); }, 3000); render(); }
  function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; }
  function render() {
    $('link').classList.toggle('on', state.linked); $('link').disabled = !state.canLink;
    const box = $('displays'); box.innerHTML = '';
    for (const d of state.displays) {
      const row = el('div', 'row' + (d.selected ? ' sel' : '') + (state.linked && d.on ? ' linked' : ''));
      const head = el('div', 'head'); head.append(el('div', 'dot'), el('div', 'name', d.name));
      const main = el('button', 'mini' + (d.main ? ' on' : ''), d.preferred ? 'M ★' : 'M');
      main.disabled = !d.canBeMain && !d.main;
      hold(main, () => send({type:'main', display:d.id}), () => send({type:'preferred', display:d.id}));
      const value = el('div', 'value', d.on ? (d.brightness ?? '…') + (d.brightness === null ? '' : '%') : 'OFF');
      if (d.on) head.append(main); head.append(value); row.append(head);
      row.onclick = ev => { if (ev.target.tagName !== 'BUTTON' && ev.target.tagName !== 'INPUT' && d.on && !d.selected) send({type:'select', display:d.id}); };
      if (d.ready) {
        const slider = el('input'); slider.type = 'range'; slider.min = 0; slider.max = 100;
        slider.value = d.brightness;
        slider.onpointerdown = () => { dragging = d.id; };
        slider.ontouchstart = () => { dragging = d.id; };
        slider.oninput = () => { dragging = d.id; setLevel(d.id, slider.value, value); };
        row.append(slider);
      }
      box.append(row);
    }
    const profiles = $('profiles'); profiles.innerHTML = '';
    for (const p of state.profiles) {
      const b = el('button', 'profile'); const badge = el('span', 'badge' + (p.active ? ' on' : ''), p.name.substring(0, 1));
      badge.style.background = p.color; b.append(badge, el('span', '', p.modified ? p.name + ' · custom' : p.name)); b.onclick = () => send({type:'profile', name:p.name});
      profiles.append(b);
    }
    $('modesBox').classList.toggle('hidden', !state.modes);
    if (state.modes) {
      $('modesFor').textContent = state.modes.display; const modes = $('modes'); modes.innerHTML = '';
      for (const o of state.modes.options) { const c = el('button', 'chip' + (o.value === state.modes.current ? ' on' : ''), o.label); c.onclick = () => send({type:'mode', value:o.value}); modes.append(c); }
    }
    const inputs = $('inputs'); inputs.innerHTML = '';
    for (const d of state.displays.filter(d => d.inputs.length)) {
      const card = el('div', 'card'); card.append(el('div', '', d.name + ' · Inputs'));
      const chips = el('div', 'chips');
      for (const i of d.inputs) {
        const key = d.id + '/' + i.code, isArmed = armed === key && !i.current;
        const c = el('button', 'chip' + (i.current ? ' on' : '') + (isArmed ? ' armed' : ''), isArmed ? 'Tap again: ' + i.label : (i.inCycle ? '● ' : '○ ') + i.label);
        hold(c, () => { if (i.current) return; if (armed === key) { armed = null; send({type:'input', display:d.id, code:i.code}); render(); } else arm(key); },
                () => send({type:'cycle', display:d.id, code:i.code, on:!i.inCycle}));
        chips.append(c);
      }
      card.append(chips, el('div', 'muted', 'Tap twice: switch input · hold: Next input stops here (●) or skips it (○)'));
      inputs.append(card);
    }
  }
  connect();
</script>
</body>
</html>
""";
}
