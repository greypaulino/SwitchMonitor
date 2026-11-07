namespace SwitchMonitor.App.Services;

/// <summary>
/// The control page a browser gets from this computer (http://&lt;computer&gt;:47820/). It speaks the same protocol as the
/// Android app and is drawn like it: the same colors, panels, icons, sizes and motion (Brightness, Windows volume,
/// Profiles with picture modes, Inputs), on a phone, a tablet or a desktop browser. Pair with the code from Settings;
/// everything stays up to date as it changes on the computer.
/// </summary>
internal static class PhonePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#161616">
<title>SwitchMonitor</title>
<style>
  /* The same look as the Android app (Graphite): colors, radii, sizes and motion follow its Compose screens. */
  :root {
    --ground:#161616; --card:#232323; --raised:#2e2e2e; --track:#3a3a3a; --line:#ffffff1f; --text:#f2f2f2; --soft:#d6d6d6;
    --muted:#a8a8a8; --ink:#141414; --green:#4cd97b; --warn:#ffd27a;
    --ease:cubic-bezier(.4,0,.2,1); --t:200ms;
  }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  html { background:var(--ground); }
  body { margin:0; background:var(--ground); color:var(--text); font:15px/1.4 Roboto, "Segoe UI", system-ui, sans-serif;
         padding:max(24px, env(safe-area-inset-top)) max(16px, env(safe-area-inset-right)) max(40px, env(safe-area-inset-bottom)) max(16px, env(safe-area-inset-left)); }
  button { font:inherit; color:inherit; cursor:pointer; -webkit-user-select:none; user-select:none; -webkit-touch-callout:none; touch-action:manipulation;
           transition:background var(--t) var(--ease), color var(--t) var(--ease), border-color var(--t) var(--ease), transform var(--t) var(--ease), opacity var(--t) var(--ease); }
  button:active:not(:disabled) { transform:scale(.97); }
  button:disabled { opacity:.45; cursor:default; }
  svg.i { display:block; fill:none; stroke:currentColor; stroke-width:2; stroke-linecap:round; stroke-linejoin:round; flex:none; }
  .hidden { display:none !important; }

  /* Layout: one column on a phone; centered and wider on a tablet; two columns on a desktop */
  .wrap { max-width:560px; margin:0 auto; display:flex; flex-direction:column; gap:14px; }
  .cols { display:flex; flex-direction:column; gap:14px; }
  .col { display:flex; flex-direction:column; gap:14px; min-width:0; }
  @media (min-width:900px) {
    .wrap { max-width:980px; }
    .cols { flex-direction:row; align-items:flex-start; }
    .col { flex:1; }
  }

  /* Header: monitor tile, computer name, connection status */
  .header { display:flex; align-items:center; padding:4px 4px 12px; gap:12px; }
  .tile { width:40px; height:40px; border-radius:12px; background:var(--raised); display:flex; align-items:center; justify-content:center; flex:none; }
  .header .who { flex:1; min-width:0; }
  .header h1 { font-size:17px; font-weight:600; margin:0; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .status { display:flex; align-items:center; gap:6px; color:var(--muted); font-size:13px; }
  .status .d { width:7px; height:7px; border-radius:50%; background:var(--muted); transition:background var(--t) var(--ease); }
  .status.ok .d { background:var(--green); } .status.off .d { background:var(--warn); }
  .iconbtn { width:40px; height:40px; border-radius:50%; border:0; background:none; display:flex; align-items:center; justify-content:center; }
  .iconbtn:hover { background:#ffffff14; }
  .menu { position:absolute; right:0; top:44px; background:var(--raised); border:1px solid var(--line); border-radius:14px; padding:6px; min-width:210px; z-index:5;
          box-shadow:0 8px 24px #0008; }
  .menu button { display:block; width:100%; text-align:left; background:none; border:0; padding:10px 12px; border-radius:10px; }
  .menu button:hover { background:#ffffff14; }
  .menu .addr { color:var(--muted); font-size:13px; padding:8px 12px; }

  /* Panels */
  .panel { background:var(--card); border:1px solid var(--line); border-radius:22px; padding:16px; }
  .panel.tight { padding:12px 10px 8px; }
  .title { font-size:16px; font-weight:600; }
  .phead { display:flex; align-items:center; gap:10px; padding:0 4px 6px 6px; }
  .phead .title { flex:1; }
  .circle { width:38px; height:38px; border-radius:50%; background:var(--raised); border:1px solid var(--line); display:flex; align-items:center; justify-content:center; flex:none; }
  .circle.on { background:var(--green); border-color:var(--green); color:#0f2416; }
  .circle.light { background:var(--text); border-color:var(--text); color:var(--ink); }
  .muted { color:var(--muted); } .small { font-size:12px; }

  /* A display: status dot, icon, name, M, value, slider */
  .row { border-radius:16px; padding:12px 8px 6px; transition:background var(--t) var(--ease); }
  .row.sel { background:var(--raised); }
  .row.clickable { cursor:pointer; }
  .line { display:flex; align-items:center; gap:10px; min-height:30px; }
  .line .dot { width:8px; height:8px; border-radius:50%; background:#5a5a5a; flex:none; transition:background var(--t) var(--ease); }
  .line .dot.lit { background:var(--green); }
  .line .name { flex:1; min-width:0; font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .line .val { width:48px; text-align:right; color:var(--soft); font-variant-numeric:tabular-nums; }
  .m { height:30px; min-width:34px; padding:0 10px; border-radius:15px; border:1px solid #ffffff33; background:none; color:var(--muted); font-size:12px; font-weight:700; }
  .m.on { background:var(--text); border-color:var(--text); color:var(--ink); }
  .linked { background:var(--raised); border-radius:16px; padding:10px 8px 6px; display:flex; flex-direction:column; gap:6px; }

  /* Slider: a thin white fill on a grey track and a small round white thumb */
  input[type=range] { -webkit-appearance:none; appearance:none; width:100%; height:36px; background:none; margin:0; display:block; --v:0%; --fill:var(--text); }
  input[type=range]::-webkit-slider-runnable-track { height:6px; border-radius:3px; background:linear-gradient(to right, var(--fill) var(--v), var(--track) var(--v)); }
  input[type=range]::-moz-range-track { height:6px; border-radius:3px; background:var(--track); }
  input[type=range]::-moz-range-progress { height:6px; border-radius:3px; background:var(--fill); }
  input[type=range]::-webkit-slider-thumb { -webkit-appearance:none; width:16px; height:16px; border-radius:50%; background:var(--text); margin-top:-5px; box-shadow:0 1px 3px #0009; transition:transform var(--t) var(--ease); }
  input[type=range]::-moz-range-thumb { width:16px; height:16px; border:0; border-radius:50%; background:var(--text); box-shadow:0 1px 3px #0009; }
  input[type=range]:active::-webkit-slider-thumb { transform:scale(1.25); }
  input[type=range]:focus-visible { outline:2px solid var(--text); outline-offset:2px; border-radius:6px; }

  /* Volume, folded until opened */
  .fold { display:flex; align-items:center; gap:10px; width:100%; background:none; border:0; border-radius:14px; padding:8px 4px 8px 6px; text-align:left; }
  .fold:hover { background:#ffffff0d; }
  .fold .title { flex:1; }
  .fold .chev { transition:transform var(--t) var(--ease); color:var(--muted); }
  .fold.closed .chev { transform:rotate(-90deg); }
  .vol { display:grid; grid-template-rows:1fr; transition:grid-template-rows var(--t) var(--ease), opacity var(--t) var(--ease); }
  .vol.closed { grid-template-rows:0fr; opacity:0; }
  .vol > div { overflow:hidden; display:flex; align-items:center; gap:12px; padding:0 4px 4px 2px; }

  /* Profiles and picture modes: a row that scrolls sideways; the edge fades and a ">" moves it along */
  .scroller { position:relative; }
  .scroller .rail { display:flex; gap:6px; overflow-x:auto; scrollbar-width:none; padding-right:0; }
  .scroller .rail::-webkit-scrollbar { display:none; }
  .scroller.more .rail { padding-right:28px; }
  .scroller .fade { position:absolute; right:0; top:0; bottom:0; width:44px; display:none; align-items:center; justify-content:flex-end;
                    background:linear-gradient(to right, #23232300, #232323bf); pointer-events:none; }
  .scroller.more .fade { display:flex; }
  .scroller .fade button { pointer-events:auto; width:32px; height:32px; border-radius:50%; border:0; background:none; color:#f2f2f2cc; display:flex; align-items:center; justify-content:center; }
  .badge { width:56px; flex:none; background:none; border:0; border-radius:12px; padding:2px 0; display:flex; flex-direction:column; align-items:center; }
  .badge:hover { background:#ffffff0d; }
  .badge .c { width:46px; height:46px; border-radius:50%; padding:4px; border:2px solid transparent; transition:border-color var(--t) var(--ease); }
  .badge .c > span { width:100%; height:100%; border-radius:50%; display:flex; align-items:center; justify-content:center; color:var(--ink); font-weight:700; font-size:16px; }
  .badge.on .c { border-color:var(--text); }
  .badge .n { margin-top:5px; font-size:11px; color:var(--soft); max-width:56px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .badge .k { font-size:10px; color:var(--muted); }
  .hr { height:1px; background:#ffffff1a; margin:16px 0 14px; }
  .pill { height:38px; border-radius:19px; background:var(--raised); border:1px solid var(--line); padding:0 14px 0 12px; display:flex; align-items:center; gap:6px; font-size:13px; white-space:nowrap; flex:none; }
  .pill:hover:not(.on) { background:#3a3a3a; }
  .pill.on { background:var(--text); border-color:var(--text); color:var(--ink); font-weight:600; }
  .pill.on svg { color:var(--ink); } .pill svg { color:var(--soft); }
  /* Picture modes: a dropdown under the profiles, opened from Custom while it is in use */
  .badge.custom .k2 { display:flex; align-items:center; gap:2px; font-size:10px; color:var(--muted); }
  .badge.custom .k2 svg { transition:transform var(--t) var(--ease); }
  .badge.custom.open .k2 svg { transform:rotate(180deg); }
  .dd { display:grid; grid-template-rows:0fr; opacity:0; transition:grid-template-rows var(--t) var(--ease), opacity var(--t) var(--ease), margin var(--t) var(--ease); margin-top:0; }
  .dd.open { grid-template-rows:1fr; opacity:1; margin-top:12px; }
  .dd > div { overflow:hidden; }
  .ddcard { background:var(--raised); border:1px solid var(--line); border-radius:16px; padding:8px; }
  .ddhead { display:flex; align-items:center; padding:2px 4px 6px 8px; }
  .ddhead .w { flex:1; min-width:0; } .ddhead .t { font-weight:600; font-size:14px; } .ddhead .s { color:var(--muted); font-size:11px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .ddx { width:28px; height:28px; border-radius:50%; border:0; background:none; display:flex; align-items:center; justify-content:center; color:var(--muted); }
  .ddx:hover { background:#ffffff14; }
  .item { display:flex; align-items:center; gap:10px; width:100%; text-align:left; background:none; border:0; border-radius:10px; padding:9px 10px; position:relative; }
  .item:hover { background:#ffffff17; }
  .item.on { background:#ffffff1f; font-weight:600; }
  .item svg.i { color:var(--soft); } .item .ck { margin-left:auto; color:var(--text); }

  /* Inputs: tiles of three; the current one filled, an armed one in amber */
  .grid { display:grid; grid-template-columns:repeat(3, 1fr); gap:8px; margin-bottom:8px; }
  .port { position:relative; min-height:96px; border-radius:16px; background:var(--raised); border:1px solid var(--line); display:flex; flex-direction:column; align-items:center; justify-content:center;
          padding:14px 6px 6px; gap:2px; color:var(--text); }
  .port:hover:not(.cur):not(.armed) { background:#383838; }
  .port.cur { background:var(--text); border-color:var(--text); color:var(--ink); }
  .port.armed { background:var(--warn); border-color:var(--warn); color:var(--ink); }
  .port .l { font-size:13px; font-weight:600; max-width:100%; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; margin-top:4px; }
  .port .s { font-size:11px; opacity:.8; }
  .port .cyc { position:absolute; left:7px; top:7px; width:20px; height:20px; border-radius:50%; border:1.5px solid currentColor; opacity:.45; display:flex; align-items:center; justify-content:center; transition:background var(--t) var(--ease), opacity var(--t) var(--ease); }
  .port .cyc.in { background:var(--green); border-color:var(--green); color:var(--ink); opacity:1; }

  /* Pairing: the whole page centered; the code checks itself on the sixth digit */
  body.pairing .header { display:none; }
  body.pairing .wrap { min-height:calc(100vh - 28px); min-height:calc(100dvh - 28px); justify-content:center; align-items:center; }
  .pair { width:100%; max-width:360px; text-align:center; display:flex; flex-direction:column; align-items:center; }
  .pair h2 { font-size:20px; font-weight:600; margin:16px 0 6px; }
  .pair p { margin:0 0 20px; font-size:14px; }
  .tile.big { width:64px; height:64px; border-radius:20px; }
  .code { background:var(--raised); color:var(--text); border:1px solid var(--line); border-radius:16px; padding:14px 12px 14px 20px; font:inherit; font-size:28px; font-variant-numeric:tabular-nums;
          letter-spacing:10px; width:100%; max-width:280px; text-align:center; transition:border-color var(--t) var(--ease), opacity var(--t) var(--ease); }
  .code:focus { outline:none; border-color:var(--text); }
  .code:disabled { opacity:.6; }
  .code.bad { border-color:var(--warn); animation:shake .35s var(--ease); }
  @keyframes shake { 20% { transform:translateX(-6px); } 40% { transform:translateX(6px); } 60% { transform:translateX(-4px); } 80% { transform:translateX(4px); } }
  .pairmsg { min-height:22px; margin-top:14px; font-size:14px; color:var(--muted); }
  .pairmsg.bad { color:var(--warn); }
  /* Messages */
  .toast { position:fixed; left:50%; bottom:max(16px, env(safe-area-inset-bottom)); transform:translate(-50%, 20px); opacity:0; pointer-events:none; max-width:min(92vw, 440px);
           background:var(--card); border:1px solid #ffd27a80; color:var(--warn); border-radius:14px; padding:12px 16px; transition:opacity var(--t) var(--ease), transform var(--t) var(--ease); z-index:9; }
  .toast.show { opacity:1; transform:translate(-50%, 0); }
  .bone { background:var(--raised); border-radius:6px; animation:pulse .85s var(--ease) infinite alternate; }
  @keyframes pulse { from { opacity:.35; } to { opacity:.8; } }
  .dim { opacity:.55; transition:opacity var(--t) var(--ease); }
  @media (prefers-reduced-motion:reduce) { * { transition:none !important; animation:none !important; } }
</style>
</head>
<body>
  <div class="wrap">
    <div class="header">
      <div class="tile" id="tile"></div>
      <div class="who">
        <h1 id="title">SwitchMonitor</h1>
        <div class="status" id="status"><span class="d"></span><span id="sub">Connecting…</span></div>
      </div>
      <div style="position:relative">
        <button class="iconbtn" id="moreBtn" aria-label="Menu"></button>
        <div class="menu hidden" id="menu">
          <div class="addr" id="addr"></div>
          <button id="forget">Use another computer</button>
        </div>
      </div>
    </div>

    <div class="pair hidden" id="pairCard">
      <div class="tile big" id="pairTile"></div>
      <h2>Pair with this computer</h2>
      <p class="muted">On the computer open SwitchMonitor &gt; Settings &gt; Phone &gt; Pair a phone, then type the 6-digit code.</p>
      <input class="code" type="text" id="code" inputmode="numeric" maxlength="6" placeholder="000000" autocomplete="one-time-code" aria-label="6-digit pairing code">
      <div class="pairmsg" id="pairMsg" role="status" aria-live="polite"></div>
    </div>

    <div id="skeleton" class="cols">
      <div class="col">
        <div class="panel tight"><div class="phead"><div id="skSun"></div><div class="title">Brightness</div><div class="bone circle" style="border:0"></div></div>
          <div style="padding:12px 8px 6px"><div style="display:flex;gap:10px;align-items:center"><div class="bone" style="width:8px;height:8px;border-radius:50%"></div><div class="bone" style="width:170px;height:14px"></div></div><div style="height:36px;display:flex;align-items:center"><div class="bone" style="width:100%;height:6px;border-radius:3px"></div></div></div>
        </div>
      </div>
      <div class="col">
        <div class="panel"><div class="title" style="margin-bottom:12px">Profiles</div>
          <div style="display:flex;gap:6px"><div class="bone" style="width:46px;height:46px;border-radius:50%"></div><div class="bone" style="width:46px;height:46px;border-radius:50%"></div><div class="bone" style="width:46px;height:46px;border-radius:50%"></div><div class="bone" style="width:46px;height:46px;border-radius:50%"></div></div>
        </div>
      </div>
    </div>

    <div id="main" class="cols hidden">
      <div class="col" id="colA">
        <div class="panel tight" id="brightPanel"></div>
        <div class="panel hidden" id="volPanel" style="padding:6px 10px"></div>
      </div>
      <div class="col" id="colB">
        <div class="panel hidden" id="profPanel"></div>
        <div id="inputs" class="col"></div>
      </div>
    </div>
  </div>
  <div class="toast" id="toast"></div>

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
  let ws, state = null, token = localStorage.getItem('sm-token'), dragging = null, pending = false, timer = null, volTimer = null, expected = null, volumeOpen = false, live = false;
  // While a finger is on a slider the page is not redrawn (that would drop the slider); it catches up on release.
  function release() { dragging = null; if (pending) { pending = false; render(); } }
  document.addEventListener('pointerup', () => setTimeout(release, 300));
  document.addEventListener('touchend', () => setTimeout(release, 300));
  const $ = id => document.getElementById(id);
  function send(m) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(m)); }

  // ---- Icons: the Android app's line icons (24x24, round 2px strokes) ----
  const circ = (cx, cy, r) => `M${cx - r} ${cy}a${r} ${r} 0 1 0 ${2 * r} 0a${r} ${r} 0 1 0 ${-2 * r} 0`;
  const ICONS = {
    monitor: ['M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z', 'M8 20h8M12 16v4'],
    laptop: ['M5.5 5h13a1.5 1.5 0 0 1 1.5 1.5v8a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 14.5v-8A1.5 1.5 0 0 1 5.5 5z', 'M2 19h20'],
    sun: [circ(12, 12, 4), 'M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4'],
    link: ['M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7', 'M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7'],
    more: ['M12 5h.01M12 12h.01M12 19h.01'],
    chevronRight: ['M9 5l7 7-7 7'], chevronDown: ['M6 9l6 6 6-6'],
    speaker: ['M4 9.5h3.5L12 5.5v13l-4.5-4H4z', 'M15.5 9a4.5 4.5 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11'],
    speakerOff: ['M4 9.5h3.5L12 5.5v13l-4.5-4H4z', 'M16 9.5l5 5M21 9.5l-5 5'],
    port: ['M2 7h20v6l-3 4H5l-3-4z', 'M7 10h10'], next: ['M4 12h12M12 6l6 6-6 6'],
    edit: ['M4 20h4L19 9a2.8 2.8 0 0 0-4-4L4 16z', 'M13.5 6.5l4 4'],
    sport: [circ(14.5, 4, 1.5), 'M7 21l3-6 3 2v5M10 15l1-5 3 2 3 1M8 9l3-1'],
    tv: ['M4 6h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1z', 'M8 21h8'],
    custom: ['M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12', circ(16, 6, 2), circ(10, 12, 2), circ(18, 18, 2)],
    work: ['M5 7h14a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2z', 'M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M3 13h18'],
    night: ['M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z'],
    game: ['M6 11h4M8 9v4M15 12h.01M18 10h.01', 'M17.3 5H6.7a4 4 0 0 0-4 3.6L2 15a3 3 0 0 0 5.4 1.8L9 15h6l1.6 1.8A3 3 0 0 0 22 15l-.7-6.4a4 4 0 0 0-4-3.6z'],
    movie: ['M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z', 'M7 4v16M17 4v16M2 9h5M2 15h5M17 9h5M17 15h5'],
    photo: ['M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z', circ(12, 13.5, 3)],
    reading: ['M2 5h6a4 4 0 0 1 4 4v11a3 3 0 0 0-3-3H2z', 'M22 5h-6a4 4 0 0 0-4 4v11a3 3 0 0 1 3-3h7z'],
    star: ['M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.3 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z'],
    home: ['M3 11l9-8 9 8', 'M5 9.5V20h14V9.5'],
    music: ['M9 18V5l12-2v13', circ(6, 18, 3), circ(18, 16, 3)],
    bulb: ['M9 18h6M10 21h4', 'M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2V16h5v-.1c0-.8.4-1.5 1-2A6 6 0 0 0 12 3z'],
    eye: ['M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z', circ(12, 12, 3)],
    close: ['M6 6l12 12M18 6L6 18'], check: ['M5 12.5l4.5 4.5L19 7.5'],
    headphones: ['M3 18v-6a9 9 0 0 1 18 0v6', 'M21 19a2 2 0 0 1-2 2h-1v-6h3zM3 19a2 2 0 0 0 2 2h1v-6H3z']
  };
  function icon(name, size, width) {
    const e = document.createElement('span'); e.style.display = 'inline-flex';
    e.innerHTML = `<svg class="i" viewBox="0 0 24 24" width="${size}" height="${size}"${width ? ` style="stroke-width:${width}"` : ''}>${(ICONS[name] || []).map(p => `<path d="${p}"/>`).join('')}</svg>`;
    return e;
  }
  const PROFILE_ICON = { work:'work', night:'night', game:'game', movie:'movie', video:'movie', photo:'photo', picture:'photo', reading:'reading', library:'reading',
    document:'reading', school:'reading', star:'star', home:'home', music:'music', sound:'music', headphones:'headphones', sun:'sun', bulb:'bulb', eye:'eye', monitor:'monitor', laptop:'laptop' };
  const profileGlyph = p => p.custom ? 'custom' : (PROFILE_ICON[p.icon] || null);
  function modeGlyph(label) {
    const v = label.toLowerCase();
    if (v.includes('game') || v.startsWith('fps') || v.startsWith('rts')) return 'game';
    if (v.includes('custom') || v.includes('user')) return 'edit';
    if (v.includes('read')) return 'reading'; if (v.includes('photo')) return 'photo';
    if (v.includes('cinema') || v.includes('movie')) return 'movie'; if (v.includes('hdr')) return 'bulb';
    if (v.includes('weak')) return 'eye'; if (v.includes('productivity')) return 'work'; if (v.includes('sport')) return 'sport';
    return 'tv';
  }

  // ---- Connection ----
  function setStatus(kind, text) { $('status').className = 'status ' + kind; $('sub').textContent = text; }
  function connect() {
    ws = new WebSocket(`ws://${location.host}/ws`);
    ws.onopen = () => { live = true; setStatus('ok', 'Connected over Wi-Fi'); $('main').classList.remove('dim'); };
    ws.onclose = () => { live = false; setStatus('off', "Can't reach it · trying again"); $('main').classList.add('dim'); setTimeout(connect, 2000); };
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
        toast('This computer could not prove it is the one this page was paired with.');
        ws.onclose = null; ws.close(); return;
      }
      if (m.type === 'paired') { token = m.token; localStorage.setItem('sm-token', token); checking = false; }
      if (m.type === 'paired' || m.type === 'welcome') { $('title').textContent = m.computer; hidePair(); if (!state) $('skeleton').classList.remove('hidden'); }
      if (m.type === 'state') { state = m; if (dragging) pending = true; else render(); }
      if (m.type === 'error') {
        if (checking || !$('pairCard').classList.contains('hidden')) pairFailed(m.error);
        else toast(m.error);
        if (m.code === 'unpaired') { localStorage.removeItem('sm-token'); token = null; showPair(); }
      }
    };
  }
  function showPair() {
    state = null; $('main').classList.add('hidden'); $('skeleton').classList.add('hidden'); $('pairCard').classList.remove('hidden');
    document.body.classList.add('pairing'); resetCode(); setTimeout(() => $('code').focus(), 50);
  }
  function hidePair() { $('pairCard').classList.add('hidden'); document.body.classList.remove('pairing'); }
  function pairMessage(text, bad) { const m = $('pairMsg'); m.textContent = text; m.className = 'pairmsg' + (bad ? ' bad' : ''); }
  function resetCode() { const c = $('code'); c.value = ''; c.disabled = false; c.classList.remove('bad'); pairMessage('', false); }
  let checking = false;
  // The sixth digit is the "Pair" button: nothing else to press.
  function pair() {
    if (checking) return;
    if (!ws || ws.readyState !== 1) { pairMessage('Not connected to the computer yet. Try again in a moment.', true); return; }
    checking = true; $('code').disabled = true; $('code').classList.remove('bad'); pairMessage('Checking the code…', false);
    send({type:'pair', code:$('code').value, name: navigator.userAgent.includes('Android') ? 'Android phone (browser)' : 'Browser'});
  }
  function pairFailed(text) {
    checking = false; const c = $('code'); c.disabled = false; c.value = ''; void c.offsetWidth; c.classList.add('bad'); c.focus(); pairMessage(text || 'That code did not work. Check it and try again.', true);
  }
  $('code').addEventListener('input', () => {
    const c = $('code'); c.value = c.value.replace(/\D/g, '').slice(0, 6); c.classList.remove('bad'); if (!checking) pairMessage('', false);
    if (c.value.length === 6) pair();
  });
  let toastTimer = null;
  function toast(text) { const t = $('toast'); t.textContent = text; t.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 4500); }
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

  // ---- Pieces (the Android screen's, in the same order and sizes) ----
  function slider(value, fill) {
    const s = el('input'); s.type = 'range'; s.min = 0; s.max = 100; s.value = value;
    const paint = () => s.style.setProperty('--v', s.value + '%');
    if (fill) s.style.setProperty('--fill', fill);
    paint(); s.addEventListener('input', paint); return s;
  }
  function displayLine(d, lit, valueText) {
    const line = el('div', 'line');
    line.append(el('div', 'dot' + (lit ? ' lit' : '')), icon(d.kind === 'laptop' ? 'laptop' : 'monitor', 18), el('div', 'name', d.name));
    line.lastChild.style.color = 'var(--text)';
    line.children[1].style.color = 'var(--muted)';
    if (d.main || d.canBeMain) {
      const m = el('button', 'm' + (d.main ? ' on' : ''), d.preferred ? 'M ★' : 'M');
      hold(m, () => { if (!d.main) send({type:'main', display:d.id}); }, () => send({type:'preferred', display:d.id}));
      line.append(m);
    }
    const value = el('div', 'val', valueText); line.append(value);
    return { line, value };
  }
  function wireSlider(s, d, label) {
    s.onpointerdown = () => { dragging = d.id; }; s.ontouchstart = () => { dragging = d.id; };
    s.oninput = () => { dragging = d.id; setLevel(d.id, s.value, label); };
  }
  function scrollerOf(items, id, selectedIndex, cls) {
    const wrap = el('div', 'scroller'), rail = el('div', 'rail' + (cls ? ' ' + cls : ''));
    rail.id = id; rail.append(...items); wrap.append(rail);
    const fade = el('div', 'fade'), next = el('button'); next.setAttribute('aria-label', 'More'); next.append(icon('chevronRight', 16, 2.4));
    next.onclick = () => rail.scrollBy({left: 660, behavior: 'smooth'}); fade.append(next); wrap.append(fade);
    const update = () => wrap.classList.toggle('more', rail.scrollWidth - rail.clientWidth - rail.scrollLeft > 4);
    rail.addEventListener('scroll', update);
    requestAnimationFrame(() => {
      rail.scrollLeft = scrollPos[id] || 0;
      if (selectedIndex >= 0 && lastSelected[id] !== selectedIndex) {   // keep the chosen one in sight when it changes
        const chosen = rail.children[selectedIndex];
        if (chosen && (chosen.offsetLeft < rail.scrollLeft || chosen.offsetLeft + chosen.offsetWidth > rail.scrollLeft + rail.clientWidth - 90)) rail.scrollTo({left: chosen.offsetLeft - 8, behavior: 'smooth'});
      }
      lastSelected[id] = selectedIndex; update();
    });
    rail.addEventListener('scroll', () => { scrollPos[id] = rail.scrollLeft; });
    return wrap;
  }
  const scrollPos = {}, lastSelected = {};

  function render() {
    $('skeleton').classList.add('hidden'); $('main').classList.remove('hidden');
    renderBrightness(); renderVolume(); renderProfiles(); renderInputs();
  }

  function renderBrightness() {
    const panel = $('brightPanel'); panel.innerHTML = '';
    const head = el('div', 'phead'); head.append(icon('sun', 20), el('div', 'title', 'Brightness'));
    if (state.canLink) {
      const link = el('button', 'circle' + (state.linked ? ' on' : '')); link.append(icon('link', 18));
      link.setAttribute('aria-label', state.linked ? 'Unlink brightness' : 'Link brightness');
      link.onclick = () => send({type:'link', on:!state.linked}); head.append(link);
    }
    panel.append(head);
    const shown = state.displays.filter(d => d.on);
    if (!shown.length) panel.append(Object.assign(el('div', 'muted', 'No display can be controlled right now.'), {style: 'padding:8px'}));
    if (state.linked && shown.length > 1) {
      const lead = shown.find(d => d.ready) || shown[0], box = el('div', 'linked');
      let leadValue = null;
      shown.forEach((d, i) => { const { line, value } = displayLine(d, true, i === 0 ? (lead.ready ? lead.brightness + '%' : '…') : ''); if (i === 0) leadValue = value; box.append(line); });
      if (lead.ready) { const s = slider(lead.brightness); wireSlider(s, lead, leadValue); box.append(s); }
      else box.append(bone());
      panel.append(box);
    } else for (const d of shown) {
      const row = el('div', 'row' + (d.selected ? ' sel' : ' clickable'));
      const { line, value } = displayLine(d, d.selected, d.ready ? d.brightness + '%' : '…'); row.append(line);
      row.onclick = ev => { if (!d.selected && !ev.target.closest('button,input')) send({type:'select', display:d.id}); };
      if (d.ready) { const s = slider(d.brightness); wireSlider(s, d, value); row.append(s); } else row.append(bone());
      panel.append(row);
    }
  }
  function bone() { const b = el('div'); b.style.cssText = 'height:36px;display:flex;align-items:center'; const x = el('div', 'bone'); x.style.cssText = 'width:100%;height:6px;border-radius:3px'; b.append(x); return b; }

  function renderVolume() {
    const panel = $('volPanel'), v = state.volume; panel.classList.toggle('hidden', !v); if (!v) return; panel.innerHTML = '';
    const fold = el('button', 'fold' + (volumeOpen ? '' : ' closed'));
    fold.setAttribute('aria-expanded', volumeOpen);
    fold.append(icon(v.muted ? 'speakerOff' : 'speaker', 20), el('div', 'title', 'Volume'), Object.assign(el('div', '', v.muted ? 'Muted' : v.percent + '%'), {style: 'color:var(--soft)'}));
    const chev = el('span', 'chev'); chev.append(icon('chevronDown', 18, 2.4)); fold.append(chev);
    fold.onclick = () => { volumeOpen = !volumeOpen; render(); };
    const body = el('div', 'vol' + (volumeOpen ? '' : ' closed')), inner = el('div');
    const mute = el('button', 'circle' + (v.muted ? ' light' : '')); mute.append(icon(v.muted ? 'speakerOff' : 'speaker', 18));
    mute.setAttribute('aria-label', v.muted ? 'Turn the sound on' : 'Mute'); mute.onclick = () => send({type:'mute', on:!v.muted});
    const s = slider(v.percent, v.muted ? 'var(--muted)' : null); s.style.flex = '1';
    const label = fold.children[2];
    s.onpointerdown = () => { dragging = 'volume'; }; s.ontouchstart = () => { dragging = 'volume'; };
    s.oninput = () => { dragging = 'volume'; label.textContent = s.value + '%'; clearTimeout(volTimer); volTimer = setTimeout(() => send({type:'volume', value:+s.value}), 60); };
    inner.append(mute, s); body.append(inner); panel.append(fold, body);
  }

  function renderProfiles() {
    const panel = $('profPanel'); panel.innerHTML = '';
    const profiles = state.profiles || [];
    panel.classList.toggle('hidden', !profiles.length && !state.modes); if (!profiles.length && !state.modes) return;
    const t = el('div', 'title', 'Profiles'); t.style.marginBottom = '12px'; panel.append(t);
    const customOn = profiles.some(p => p.custom && p.active), canModes = !!state.modes && customOn;
    if (!canModes) modesOpen = false;
    let hoverTimer = null;
    const items = profiles.map(p => {
      const b = el('button', 'badge' + (p.active ? ' on' : '') + (p.custom ? ' custom' : '') + (p.custom && modesOpen ? ' open' : '')), c = el('div', 'c'), inner = el('span');
      inner.style.background = p.color;
      const g = profileGlyph(p); if (g) inner.append(icon(g, 18)); else inner.textContent = p.name.substring(0, 1).toUpperCase();
      c.append(inner); b.append(c, el('div', 'n', p.name)); if (p.modified) b.append(el('div', 'k', 'custom'));
      b.title = p.name;
      if (p.custom && p.active && canModes) {
        // Custom in use: its picture modes open under the row (a click, or half a second of the mouse resting on it).
        const k = el('div', 'k2'); k.append(document.createTextNode('modes'), icon('chevronDown', 10, 2.6)); b.append(k);
        b.setAttribute('aria-haspopup', 'true'); b.setAttribute('aria-expanded', modesOpen);
        b.onclick = () => toggleModes();
        b.addEventListener('pointerenter', ev => { if (ev.pointerType === 'mouse' && !modesOpen) { clearTimeout(hoverTimer); hoverTimer = setTimeout(() => { if (!modesOpen) toggleModes(); }, 500); } });
        b.addEventListener('pointerleave', () => clearTimeout(hoverTimer));
      } else b.onclick = () => { modesOpen = false; send({type:'profile', name:p.name}); };
      return b;
    });
    panel.append(scrollerOf(items, 'railProfiles', profiles.findIndex(p => p.active)));
    if (canModes) {
      const dd = el('div', 'dd' + (modesOpen && !modesAnim ? ' open' : '')), wrap = el('div'), card = el('div', 'ddcard'); dd.id = 'modesCard';
      const head = el('div', 'ddhead'), w = el('div', 'w'); w.append(el('div', 't', 'Picture mode'), el('div', 's', state.modes.display));
      const x = el('button', 'ddx'); x.setAttribute('aria-label', 'Close picture modes'); x.append(icon('close', 12, 2.2)); x.onclick = () => closeModes();
      head.append(w, x); card.append(head);
      for (const o of state.modes.options) {
        const on = o.value === state.modes.current, it = el('button', 'item' + (on ? ' on' : ''));
        it.append(icon(modeGlyph(o.label), 16), document.createTextNode(o.label)); if (on) { const ck = el('span', 'ck'); ck.append(icon('check', 14, 2.2)); it.append(ck); }
        it.onclick = () => { if (!on) send({type:'mode', value:o.value}); closeModes(); };
        card.append(it);
      }
      wrap.append(card); dd.append(wrap); panel.append(dd);
      if (modesOpen && modesAnim) setTimeout(() => dd.classList.add('open'), 30);
      modesAnim = false;
    }
  }
  let modesOpen = false, modesAnim = false;
  function toggleModes() { if (modesOpen) { closeModes(); return; } modesOpen = true; modesAnim = true; render(); }
  function closeModes() {
    const dd = $('modesCard'); if (!modesOpen) return;
    if (dd) { dd.classList.remove('open'); document.querySelector('.badge.custom')?.classList.remove('open'); setTimeout(() => { modesOpen = false; render(); }, 220); }
    else { modesOpen = false; }
  }
  document.addEventListener('click', ev => { if (modesOpen && !ev.target.closest('#modesCard') && !ev.target.closest('.badge.custom')) closeModes(); });
  document.addEventListener('keydown', ev => { if (ev.key === 'Escape' && modesOpen) closeModes(); });

  function renderInputs() {
    const box = $('inputs'); box.innerHTML = '';
    for (const d of state.displays.filter(d => d.on && d.inputs.length)) {
      const card = el('div', 'panel'); const t = el('div', 'title', 'Input · ' + d.name); t.style.marginBottom = '12px'; card.append(t);
      const grid = el('div', 'grid');
      for (const i of d.inputs) {
        const key = d.id + '/' + i.code, isArmed = armed === key && !i.current;
        const tile = el('button', 'port' + (i.current ? ' cur' : '') + (isArmed ? ' armed' : ''));
        const cyc = el('span', 'cyc' + (i.inCycle ? ' in' : '')); cyc.append(icon('next', 12, 2.6));
        cyc.title = i.inCycle ? 'Next input stops here' : 'Skipped by Next input';
        tile.append(cyc, icon('port', 24, 1.8), el('div', 'l', i.label));
        if (i.current || isArmed) tile.append(el('div', 's', i.current ? 'In use' : 'Tap again'));
        hold(tile, () => { if (i.current) return; if (armed === key) { armed = null; send({type:'input', display:d.id, code:i.code}); render(); } else arm(key); },
                   () => send({type:'cycle', display:d.id, code:i.code, on:!i.inCycle}));
        grid.append(tile);
      }
      card.append(grid, Object.assign(el('div', 'muted small', 'Tap twice to switch · hold to add or skip it in Next input'), {}));
      box.append(card);
    }
  }

  // ---- Header ----
  $('tile').append(icon('monitor', 20)); $('pairTile').append(icon('monitor', 30)); $('moreBtn').append(icon('more', 20, 3)); $('skSun').append(icon('sun', 20));
  $('addr').textContent = 'Address: ' + location.host;
  $('moreBtn').onclick = ev => { ev.stopPropagation(); $('menu').classList.toggle('hidden'); };
  document.addEventListener('click', () => $('menu').classList.add('hidden'));
  $('forget').onclick = () => { localStorage.removeItem('sm-token'); token = null; showPair(); if (ws) ws.close(); };
  connect();
</script>
</body>
</html>
""";
}
