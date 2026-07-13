namespace WifiScanner
{
    /// <summary>
    /// The self-contained HTML/JS page served by <see cref="WebDashboardServer"/>.
    /// No external resources (works offline on the Quest's WLAN).
    ///
    /// Layout: a top toolbar (view / storey / AP selects + an EDITABLE colour
    /// legend), the full-size map canvas, a collapsible layer inspector, a
    /// download drawer (files expand on demand), a bottom timeline (RSSI
    /// history, roaming ticks, connection outages) and a click-to-inspect
    /// tool (point / AP / note / ping / roaming details). A saved scan can be
    /// loaded as a BEFORE dataset for a split comparison view with automatic/
    /// manual rigid alignment and a draggable divider. On phones the map goes fullscreen and
    /// the panels become bottom sheets.
    ///
    /// Written with single quotes throughout so it needs no escaping inside
    /// the C# verbatim string.
    /// </summary>
    public static class WebDashboardPage
    {
        public const string Html = @"
<!doctype html>
<html lang='de'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>WiFi-Scanner Dashboard</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin:0; font-family: system-ui, sans-serif; background:#0d0f13; color:#e6e6e6;
         display:flex; flex-direction:column; height:100vh; overflow:hidden; }
  #toolbar { display:flex; flex-wrap:wrap; align-items:center; gap:8px;
             padding:8px 10px; background:#15181f; border-bottom:1px solid #262b34; }
  #toolbar h1 { font-size:14px; margin:0 6px 0 0; white-space:nowrap; }
  #toolbar select, #toolbar button, #toolbar input {
      padding:5px 8px; background:#232833; color:#e6e6e6; border:1px solid #333b48;
      border-radius:6px; font-size:13px; cursor:pointer; }
  #toolbar input[type=number] { width:58px; cursor:text; }
  #toolbar button.active { background:#2b5b80; border-color:#4a90c8; }
  #legendbox { display:flex; align-items:center; gap:4px; font-size:11px; color:#9aa; }
  #legendbar { width:110px; height:12px; border-radius:3px; }
  #status { font-size:12px; margin-left:auto; white-space:nowrap; }
  .dot { display:inline-block; width:8px; height:8px; border-radius:50%; margin-right:5px; }

  #stage { flex:1; position:relative; overflow:hidden; }
  canvas#c { width:100%; height:100%; display:block; background:#0d0f13; }

  .panel { position:absolute; top:10px; right:10px; width:230px; max-height:75%;
           overflow-y:auto; background:#15181fee; border:1px solid #333b48;
           border-radius:10px; padding:12px; display:none; z-index:5; }
  .panel h2 { font-size:11px; text-transform:uppercase; letter-spacing:1px;
              color:#8a93a6; margin:10px 0 6px; }
  .panel h2:first-child { margin-top:0; }
  .panel label { display:block; font-size:13px; margin:3px 0; cursor:pointer; }
  .panel select, .panel button { width:100%; padding:6px; margin:3px 0; background:#232833;
      color:#e6e6e6; border:1px solid #333b48; border-radius:6px; font-size:13px; cursor:pointer; }
  .alignrow { display:grid; grid-template-columns:42px 1fr 58px; gap:5px;
              align-items:center; margin:4px 0; font-size:12px; color:#aeb6c4; }
  .alignrow input[type=range] { width:100%; min-width:0; accent-color:#4a90c8; }
  .alignrow output { text-align:right; font-variant-numeric:tabular-nums; }
  #alignStatus { font-size:11px; line-height:1.35; color:#8fd4ff; margin:5px 0; }
  #btnPivot.active { background:#6d5122; border-color:#e0a03c; }
  #files a { display:block; font-size:12px; color:#6cc0ff; text-decoration:none;
             padding:2px 0; word-break:break-all; }
  #speedStatus { font-size:11px; line-height:1.4; color:#8fd4ff; margin:4px 0; }
  .sz { color:#6b7280; }

  #popup { position:absolute; display:none; background:#1a2029f2; border:1px solid #4a90c8;
           border-radius:8px; padding:8px 10px; font-size:12px; line-height:1.5;
           max-width:240px; z-index:8; pointer-events:none; white-space:pre-line; }

  #divider { position:absolute; top:0; bottom:0; width:14px; margin-left:-7px;
             cursor:col-resize; display:none; z-index:6; }
  #divider::after { content:''; position:absolute; left:6px; top:0; bottom:0;
             width:2px; background:#4a90c8; }
  .divlabel { position:absolute; top:6px; font-size:11px; color:#9fceff;
              background:#15181fcc; padding:2px 6px; border-radius:4px; z-index:4; display:none; }

  #timeline { height:110px; background:#12151b; border-top:1px solid #262b34;
              position:relative; }
  canvas#tl { width:100%; height:100%; display:block; }

  @media (max-width: 700px) {
    #toolbar h1, #legendbox span.lg { display:none; }
    #timeline { height:80px; }
    .panel { top:auto; bottom:0; left:0; right:0; width:100%; max-height:60%;
             border-radius:14px 14px 0 0; }
  }
</style>
</head>
<body>
<div id='toolbar'>
  <h1>WiFi-Scanner</h1>
  <select id='mode'>
    <option value='points'>Messpunkte</option>
    <option value='heat'>Heatmap</option>
    <option value='forecast'>Prognose</option>
    <option value='3d'>3D-R&auml;ume</option>
  </select>
  <select id='floor'><option value='all'>Etage: Alle</option></select>
  <select id='ap'><option value='all'>AP: Alle</option></select>
  <span id='legendbox'>
    <input type='number' id='lgWeak' value='-90' step='5' title='schwach (dBm)'>
    <canvas id='legendbar' width='110' height='12'></canvas>
    <input type='number' id='lgStrong' value='-40' step='5' title='stark (dBm)'>
    <span class='lg'>dBm</span>
  </span>
  <button id='btnLayers'>&#9776; Ebenen</button>
  <button id='btnFiles'>&#8681; Dateien</button>
  <button id='btnCompare'>&#8646; Vergleich</button>
  <span id='status'><span class='dot' style='background:#c0392b'></span>getrennt</span>
</div>

<div id='stage'>
  <canvas id='c'></canvas>
  <div id='divider'></div>
  <span class='divlabel' id='lblBefore' style='left:8px'>Vorher (Scan)</span>
  <span class='divlabel' id='lblAfter' style='right:8px'>Jetzt (live)</span>
  <div id='popup'></div>

  <div class='panel' id='layers'>
    <h2>Ebenen</h2>
    <label><input type='checkbox' id='l_points' checked> Messpunkte</label>
    <label><input type='checkbox' id='l_trail' checked> Laufpfad</label>
    <label><input type='checkbox' id='l_rooms' checked> R&auml;ume</label>
    <label><input type='checkbox' id='l_pose' checked> Position</label>
    <label><input type='checkbox' id='l_ap' checked> AP-Standorte</label>
    <label><input type='checkbox' id='l_ping' checked> Pings</label>
    <label><input type='checkbox' id='l_throughput' checked> Durchsatz</label>
    <label><input type='checkbox' id='l_notes' checked> Notizen</label>
    <label><input type='checkbox' id='l_roam' checked> Roaming</label>
    <h2>Statistik</h2>
    <div id='stats' style='font-size:12px;color:#aeb6c4;white-space:pre-line'></div>
  </div>

  <div class='panel' id='drawer'>
    <h2>Vergleichs-Scan (vorher)</h2>
    <select id='cmpScan'><option value=''>&ndash; Scan w&auml;hlen &ndash;</option></select>
    <div id='alignControls' style='display:none'>
      <div id='alignStatus'>Noch nicht ausgerichtet</div>
      <div class='alignrow'><span>X</span><input id='alignX' type='range' min='-20' max='20' step='0.05' value='0'><output id='alignXOut'>0,00 m</output></div>
      <div class='alignrow'><span>Z</span><input id='alignZ' type='range' min='-20' max='20' step='0.05' value='0'><output id='alignZOut'>0,00 m</output></div>
      <div class='alignrow'><span>Winkel</span><input id='alignYaw' type='range' min='-180' max='180' step='0.1' value='0'><output id='alignYawOut'>0,0&deg;</output></div>
      <button id='btnAutoAlign'>Automatisch ausrichten</button>
      <button id='btnPivot'>Drehpunkt im Plan setzen</button>
      <button id='btnResetAlign'>Ausrichtung zur&uuml;cksetzen</button>
    </div>
    <h2>Aktionen</h2>
    <button id='btnExport'>Aktuellen Scan exportieren</button>
    <button id='btnSpeed'>Durchsatz hier messen</button>
    <div id='speedStatus'>Browser dient als lokale Gegenstelle.</div>
    <button id='btnReport'>Report &ouml;ffnen</button>
    <button id='btnZip'>Alle als ZIP laden</button>
    <h2><button id='btnFileList' style='width:100%'>Dateien anzeigen &#9656;</button></h2>
    <div id='files' style='display:none'></div>
  </div>
</div>

<div id='timeline'><canvas id='tl'></canvas></div>

<script>
// ---------- state ----------
const S = { points:[], pose:null, rooms:[], apfix:null, apfixAll:null, pings:[],
            notes:[], drills:[], trail:[], roams:[], throughput:[] };
const S2 = { points:[], rooms:[], loaded:false, stem:'', gridYaw:0,
  hasGridYaw:false, alignX:0, alignZ:0, alignYaw:0, pivotX:0, pivotZ:0,
  error:null, alignmentSource:'', needsAuto:false }; // Vergleich (vorher)
const L = { points:true, trail:true, rooms:true, pose:true, ap:true, ping:true,
            throughput:true, notes:true, roam:true };
let mode = 'points';
let floorSel = 'all';
let apSel = 'all';
let gridYaw = 0;
let rssiWeak = -90, rssiStrong = -40;   // editierbare Legende
let compareOn = false, divFrac = 0.5, divDrag = false;
let pivotPick = false;
let selected = null; // {kind, ...}
const floorsSeen = new Set();
const apsSeen = new Map();
let floorBases = [], floorLabels = []; // Etagen (aus dem rooms-Paket)
const hist = [];        // {t, r} RSSI-Verlauf (Ankunftszeit)
const outages = [];     // {a, b} WS-Ausf&auml;lle
let outageStart = 0;

const cv = document.getElementById('c');
const cx = cv.getContext('2d');
const tl = document.getElementById('tl');
const tx = tl.getContext('2d');
const statusEl = document.getElementById('status');
const statsEl = document.getElementById('stats');
const popupEl = document.getElementById('popup');
const dividerEl = document.getElementById('divider');

// ---------- toolbar / panels ----------
for (const k in L) {
  const el = document.getElementById('l_' + k);
  if (el) el.addEventListener('change', () => { L[k] = el.checked; draw(); });
}
document.getElementById('mode').addEventListener('change', e => { mode = e.target.value; draw(); });
document.getElementById('floor').addEventListener('change', e => { floorSel = e.target.value; draw(); });
document.getElementById('ap').addEventListener('change', e => { apSel = e.target.value; draw(); });
document.getElementById('lgWeak').addEventListener('change', e => {
  rssiWeak = parseInt(e.target.value) || -90; drawLegend(); fcKey = ''; draw(); });
document.getElementById('lgStrong').addEventListener('change', e => {
  rssiStrong = parseInt(e.target.value) || -40; drawLegend(); fcKey = ''; draw(); });

function togglePanel(id, btn) {
  const el = document.getElementById(id);
  const show = el.style.display !== 'block';
  document.querySelectorAll('.panel').forEach(p => p.style.display = 'none');
  document.querySelectorAll('#toolbar button').forEach(b => b.classList.remove('active'));
  if (show) { el.style.display = 'block'; btn.classList.add('active'); }
}
document.getElementById('btnLayers').addEventListener('click', e => togglePanel('layers', e.target));
document.getElementById('btnFiles').addEventListener('click', e => togglePanel('drawer', e.target));
document.getElementById('btnCompare').addEventListener('click', e => {
  if (!S2.loaded) { togglePanel('drawer', document.getElementById('btnFiles')); return; }
  compareOn = !compareOn;
  e.target.classList.toggle('active', compareOn);
  updateCompareUi();
  draw();
});
document.getElementById('btnExport').addEventListener('click', () => {
  fetch('/export').then(() => setTimeout(loadFiles, 2000));
});
document.getElementById('btnSpeed').addEventListener('click', () => runSpeedTest());
document.getElementById('btnZip').addEventListener('click', () => { location.href = '/download-all'; });
document.getElementById('btnReport').addEventListener('click', () => {
  // Neuesten Report aus der Dateiliste &ouml;ffnen.
  fetch('/scans').then(r => r.json()).then(d => {
    const reps = (d.files || []).filter(f =>
      f.name.endsWith('_report.html') && f.name !== 'wifi_autosave_report.html')
      .sort((a, b) => (b.modified || 0) - (a.modified || 0));
    if (reps.length) window.open('/download?f=' + encodeURIComponent(reps[0].name), '_blank');
    else alert('Noch kein Report - erst exportieren.');
  }).catch(() => {});
});
document.getElementById('btnFileList').addEventListener('click', () => {
  const box = document.getElementById('files');
  const show = box.style.display !== 'block';
  box.style.display = show ? 'block' : 'none';
  document.getElementById('btnFileList').innerHTML =
    show ? 'Dateien verbergen &#9662;' : 'Dateien anzeigen &#9656;';
  if (show) loadFiles();
});
document.getElementById('cmpScan').addEventListener('change', e => loadCompare(e.target.value));
['alignX', 'alignZ', 'alignYaw'].forEach(id => {
  document.getElementById(id).addEventListener('input', () => {
    S2.alignX = parseFloat(document.getElementById('alignX').value) || 0;
    S2.alignZ = parseFloat(document.getElementById('alignZ').value) || 0;
    S2.alignYaw = (parseFloat(document.getElementById('alignYaw').value) || 0) * Math.PI / 180;
    S2.needsAuto = false;
    S2.alignmentSource = 'manuell';
    updateAlignmentError();
    updateAlignUi();
    saveAlignment();
    draw();
  });
});
document.getElementById('btnAutoAlign').addEventListener('click', () => {
  autoAlignReference(); if (!S2.needsAuto) saveAlignment(); draw();
});
document.getElementById('btnResetAlign').addEventListener('click', () => {
  resetReferenceAlignment(); saveAlignment(); draw();
});
document.getElementById('btnPivot').addEventListener('click', e => {
  pivotPick = !pivotPick;
  e.target.classList.toggle('active', pivotPick);
  e.target.textContent = pivotPick ? 'Punkt im linken Plan anklicken …' : 'Drehpunkt im Plan setzen';
});

function updateCompareUi() {
  dividerEl.style.display = compareOn ? 'block' : 'none';
  document.getElementById('lblBefore').style.display = compareOn ? 'block' : 'none';
  document.getElementById('lblAfter').style.display = compareOn ? 'block' : 'none';
  positionDivider();
}
function positionDivider() { dividerEl.style.left = (divFrac * cv.clientWidth) + 'px'; }
dividerEl.addEventListener('pointerdown', e => { divDrag = true; e.preventDefault(); });
window.addEventListener('pointermove', e => {
  if (!divDrag) return;
  const r = cv.getBoundingClientRect();
  divFrac = Math.min(0.95, Math.max(0.05, (e.clientX - r.left) / r.width));
  positionDivider();
  draw();
});
window.addEventListener('pointerup', () => { divDrag = false; });

// Vergleichs-Scan (Sample-JSON) vom Ger&auml;t laden.
function loadCompare(name) {
  if (!name) {
    S2.loaded = false; compareOn = false; pivotPick = false;
    document.getElementById('alignControls').style.display = 'none';
    updateCompareUi(); draw(); return;
  }
  fetch('/download?f=' + encodeURIComponent(name)).then(r => r.json()).then(d => {
    S2.points = (d.samples || []).map(s => ({ x: s.posX, y: s.posY || 0, z: s.posZ,
      r: s.rssiDbm, f: s.floor || 0, b: s.bssid || '' }));
    S2.stem = name.replace('.json', '');
    S2.gridYaw = (d.gridYawDegrees || 0) * Math.PI / 180;
    S2.hasGridYaw = d.gridYawDegrees !== undefined;
    S2.alignX = S2.alignZ = S2.alignYaw = 0;
    S2.error = null; S2.alignmentSource = ''; S2.needsAuto = false;
    // Passenden Grundriss dazuladen (falls vorhanden).
    fetch('/download?f=' + encodeURIComponent(S2.stem + '_rooms.json'))
      .then(r => r.ok ? r.json() : null).then(rd => {
        S2.rooms = rd && rd.rooms ? rd.rooms : [];
        if (rd && rd.gridYawDegrees !== undefined) {
          S2.gridYaw = rd.gridYawDegrees * Math.PI / 180;
          S2.hasGridYaw = true;
        }
        finishCompareLoad();
      }).catch(() => { S2.rooms = []; finishCompareLoad(); });
  }).catch(() => alert('Scan konnte nicht geladen werden.'));
}

function finishCompareLoad() {
  S2.loaded = true;
  compareOn = true;
  setReferencePivot();
  const restored = loadAlignment();
  if (!restored) {
    autoAlignReference();
    if (!S2.needsAuto) saveAlignment();
  }
  document.getElementById('alignControls').style.display = 'block';
  document.getElementById('btnCompare').classList.add('active');
  updateAlignUi();
  updateCompareUi();
  draw();
}

// ---------- local browser-assisted throughput ----------
let speedRunning = false;
let speedGeneration = 0;
function setSpeedStatus(generation, text) {
  if (generation === speedGeneration)
    document.getElementById('speedStatus').textContent = text;
}
async function speedPing() {
  const values = [];
  for (let i = 0; i < 4; i++) {
    const t = performance.now();
    const r = await fetch('/speed/ping?n=' + Date.now() + '-' + i,
      { cache:'no-store' });
    if (!r.ok) throw new Error('Ping fehlgeschlagen');
    await r.arrayBuffer();
    if (i > 0) values.push(performance.now() - t); // first request warms TCP
  }
  return values.reduce((a, b) => a + b, 0) / Math.max(1, values.length);
}
async function speedDown(id, bytes, parallel) {
  const t = performance.now();
  const data = await Promise.all(Array.from({length:parallel}, (_, i) =>
    fetch('/speed/down?id=' + encodeURIComponent(id) + '&bytes=' + bytes +
      '&n=' + Date.now() + '-' + i, {cache:'no-store'}).then(async r => {
        if (!r.ok) throw new Error('Quest→Browser fehlgeschlagen');
        return (await r.arrayBuffer()).byteLength;
      })));
  const ms = performance.now() - t;
  return { ms, mbps:data.reduce((a, b) => a + b, 0) * 8 / ms / 1000 };
}
async function speedUp(id, bytes, parallel) {
  const payload = new Uint8Array(bytes);
  const t = performance.now();
  await Promise.all(Array.from({length:parallel}, (_, i) =>
    fetch('/speed/up?id=' + encodeURIComponent(id) + '&n=' + i, {
      method:'POST', cache:'no-store', headers:{'Content-Type':'application/octet-stream'},
      body:payload
    }).then(r => { if (!r.ok) throw new Error('Browser→Quest fehlgeschlagen'); return r.json(); })));
  const ms = performance.now() - t;
  return { ms, mbps:bytes * parallel * 8 / ms / 1000 };
}
async function runSpeedTest(requestId) {
  if (speedRunning) return;
  speedRunning = true;
  const generation = ++speedGeneration;
  const btn = document.getElementById('btnSpeed');
  btn.disabled = true;
  try {
    setSpeedStatus(generation, 'Test wird vorbereitet …');
    const startUrl = '/speed/start' + (requestId
      ? '?request=' + encodeURIComponent(requestId) : '');
    const start = await fetch(startUrl, {method:'POST', cache:'no-store'});
    if (start.status === 409) {
      setSpeedStatus(generation, 'Test läuft in einem anderen Dashboard.');
      return;
    }
    if (!start.ok) throw new Error('Test konnte nicht gestartet werden');
    const id = (await start.json()).id;
    const latency = await speedPing();
    // Warm-up removes connection setup/JIT effects from the measured rounds.
    await speedDown(id, 256 * 1024, 2);
    await speedUp(id, 256 * 1024, 2);
    setSpeedStatus(generation, 'Quest → Browser wird gemessen …');
    let down = await speedDown(id, 4 * 1024 * 1024, 4);
    if (down.ms < 900) down = await speedDown(id, 8 * 1024 * 1024, 4);
    if (down.ms < 900) down = await speedDown(id, 16 * 1024 * 1024, 4);
    setSpeedStatus(generation, 'Browser → Quest wird gemessen …');
    let up = await speedUp(id, 4 * 1024 * 1024, 4);
    if (up.ms < 900) up = await speedUp(id, 8 * 1024 * 1024, 4);
    if (up.ms < 900) up = await speedUp(id, 16 * 1024 * 1024, 4);
    const result = { id, questToBrowserMbps:down.mbps,
      browserToQuestMbps:up.mbps, latencyMs:latency };
    const saved = await fetch('/speed/result', {method:'POST',
      headers:{'Content-Type':'application/json'}, body:JSON.stringify(result)});
    if (!saved.ok) throw new Error('Ergebnis konnte nicht gespeichert werden');
    setSpeedStatus(generation, 'Quest → Browser: ' + down.mbps.toFixed(1) +
      ' Mbit/s · Browser → Quest: ' + up.mbps.toFixed(1) +
      ' Mbit/s · ' + latency.toFixed(0) + ' ms');
  } catch (e) {
    setSpeedStatus(generation, 'Durchsatztest fehlgeschlagen: ' + e.message);
  } finally {
    if (generation === speedGeneration) {
      speedRunning = false; btn.disabled = false;
    }
  }
}

// ---------- comparison alignment ----------
function gridWithYaw(x, z, yaw) {
  const c = Math.cos(yaw), s = Math.sin(yaw);
  return [x * c - z * s, x * s + z * c];
}
function rotate2(x, z, yaw) {
  const c = Math.cos(yaw), s = Math.sin(yaw);
  return [x * c - z * s, x * s + z * c];
}
function referenceBase(x, z) { return gridWithYaw(x, z, S2.gridYaw); }
function referenceGridFromBase(q) {
  const r = rotate2(q[0] - S2.pivotX, q[1] - S2.pivotZ, S2.alignYaw);
  return [r[0] + S2.pivotX + S2.alignX, r[1] + S2.pivotZ + S2.alignZ];
}
function referenceGrid(x, z) { return referenceGridFromBase(referenceBase(x, z)); }
function PRef(x, z) { const q = referenceGrid(x, z); return [sx(q[0]), sy(q[1])]; }

function roomAlignmentPoints(rooms, project) {
  const out = [];
  (rooms || []).forEach(room => {
    const c = room.cornersXZ || [];
    for (let i = 0; i < c.length; i++) {
      const a = c[i], b = c[(i + 1) % c.length];
      const len = Math.hypot(b.x - a.x, b.y - a.y);
      const n = Math.max(1, Math.ceil(len / 0.35));
      for (let k = 0; k < n; k++) {
        const f = k / n;
        out.push(project(a.x + (b.x - a.x) * f,
                         a.y + (b.y - a.y) * f));
      }
    }
  });
  return downsample(out, 260);
}
function pointAlignmentPoints(points, project) {
  // One representative per 40-cm cell prevents multi-AP batches at the same
  // headset position from pulling the alignment centre towards that spot.
  const cells = new Map(), out = [];
  (points || []).forEach(p => {
    const q = project(p.x, p.z);
    const k = Math.round(q[0] / 0.4) + ',' + Math.round(q[1] / 0.4);
    if (!cells.has(k)) { cells.set(k, true); out.push(q); }
  });
  return downsample(out, 260);
}
function downsample(a, max) {
  if (a.length <= max) return a;
  const out = [], step = a.length / max;
  for (let i = 0; i < max; i++) out.push(a[Math.floor(i * step)]);
  return out;
}
function centreOf(a) {
  if (!a.length) return [0, 0];
  let x = 0, z = 0;
  a.forEach(p => { x += p[0]; z += p[1]; });
  return [x / a.length, z / a.length];
}
function alignmentSets() {
  const haveRooms = S.rooms.length && S2.rooms.length;
  const liveProject = (x, z) => gridWithYaw(x, z, gridYaw);
  return {
    rooms: !!haveRooms,
    live: haveRooms ? roomAlignmentPoints(S.rooms, liveProject)
                    : pointAlignmentPoints(S.points, liveProject),
    ref: haveRooms ? roomAlignmentPoints(S2.rooms, referenceBase)
                   : pointAlignmentPoints(S2.points, referenceBase)
  };
}
function nearestError(ref, live, yaw, pivot, tx, tz) {
  if (!ref.length || !live.length) return Infinity;
  let sum = 0;
  for (const p of ref) {
    const r = rotate2(p[0] - pivot[0], p[1] - pivot[1], yaw);
    const x = r[0] + pivot[0] + tx, z = r[1] + pivot[1] + tz;
    let best = 4; // cap outliers at 2 m squared
    for (const q of live) {
      const dx = x - q[0], dz = z - q[1], d = dx * dx + dz * dz;
      if (d < best) best = d;
    }
    sum += Math.sqrt(best);
  }
  return sum / ref.length;
}
function setReferencePivot() {
  const base = S2.rooms.length
    ? roomAlignmentPoints(S2.rooms, referenceBase)
    : pointAlignmentPoints(S2.points, referenceBase);
  const c = centreOf(base);
  S2.pivotX = c[0]; S2.pivotZ = c[1];
}
function autoAlignReference() {
  if (!S2.loaded) return;
  const sets = alignmentSets();
  if (!sets.live.length || !sets.ref.length) {
    S2.needsAuto = true;
    S2.alignmentSource = 'wartet auf Live-Daten';
    updateAlignUi();
    return;
  }
  S2.needsAuto = !sets.rooms && sets.live.length < 20;
  const lc = centreOf(sets.live), rc = centreOf(sets.ref);
  S2.pivotX = rc[0]; S2.pivotZ = rc[1];

  // Each scan's grid yaw provides the preferred Quest orientation. Zero can,
  // however, mean either 'already straight' or 'never calibrated', so room
  // geometry still searches the full circle and uses zero only as a tie-break.
  const minDeg = -180, maxDeg = 175, coarse = 5;
  let best = { score:Infinity, yaw:0, tx:lc[0] - rc[0], tz:lc[1] - rc[1] };
  const tryAngle = deg => {
    const yaw = deg * Math.PI / 180;
    const r = rotate2(rc[0] - S2.pivotX, rc[1] - S2.pivotZ, yaw);
    const tx = lc[0] - (r[0] + S2.pivotX);
    const tz = lc[1] - (r[1] + S2.pivotZ);
    let score = nearestError(sets.ref, sets.live, yaw,
      [S2.pivotX, S2.pivotZ], tx, tz);
    score += Math.abs(deg) * 0.001; // prefer Quest calibration on near-ties
    if (score < best.score) best = { score, yaw, tx, tz };
  };
  if (sets.rooms) {
    for (let d = minDeg; d <= maxDeg; d += coarse) tryAngle(d);
    const centreDeg = best.yaw * 180 / Math.PI;
    for (let d = centreDeg - coarse; d <= centreDeg + coarse; d += 0.1) tryAngle(d);
  }
  S2.alignYaw = best.yaw;
  S2.alignX = best.tx;
  S2.alignZ = best.tz;
  S2.alignmentSource = sets.rooms ? 'automatisch · Raumkanten' : 'automatisch · Messfläche';
  updateAlignmentError();
  updateAlignUi();
}
function updateAlignmentError() {
  if (!S2.loaded) return;
  const liveProject = (x, z) => gridWithYaw(x, z, gridYaw);
  const live = roomAlignmentPoints(S.rooms, liveProject);
  const ref = roomAlignmentPoints(S2.rooms, referenceBase);
  S2.error = live.length && ref.length ? nearestError(ref, live, S2.alignYaw,
    [S2.pivotX, S2.pivotZ], S2.alignX, S2.alignZ) : null;
}
function resetReferenceAlignment() {
  S2.alignX = S2.alignZ = S2.alignYaw = 0;
  S2.needsAuto = false;
  setReferencePivot();
  S2.error = null;
  S2.alignmentSource = 'Quest-Raster, ohne Feinabgleich';
  updateAlignUi();
}
function alignmentKey() { return 'wifi-align:' + S2.stem; }
function liveAlignmentSignature() {
  if (!S.points.length) return null;
  const q = gr(S.points[0].x, S.points[0].z);
  return [q[0], q[1], gridYaw];
}
function saveAlignment() {
  if (!S2.loaded) return;
  try { localStorage.setItem(alignmentKey(), JSON.stringify({
    x:S2.alignX, z:S2.alignZ, yaw:S2.alignYaw,
    px:S2.pivotX, pz:S2.pivotZ, source:S2.alignmentSource,
    live:liveAlignmentSignature()
  })); } catch (e) {}
}
function loadAlignment() {
  try {
    const raw = localStorage.getItem(alignmentKey());
    if (!raw) return false;
    const a = JSON.parse(raw);
    if (![a.x, a.z, a.yaw, a.px, a.pz].every(Number.isFinite)) return false;
    const live = liveAlignmentSignature();
    if (!live || !a.live || Math.hypot(live[0] - a.live[0], live[1] - a.live[1]) > 0.75 ||
        Math.abs(live[2] - a.live[2]) > 0.035) return false;
    S2.alignX = a.x; S2.alignZ = a.z; S2.alignYaw = a.yaw;
    S2.pivotX = a.px; S2.pivotZ = a.pz;
    S2.alignmentSource = a.source || 'gespeichert';
    updateAlignmentError();
    return true;
  } catch (e) { return false; }
}
function setRange(id, value) {
  const el = document.getElementById(id);
  if (value < parseFloat(el.min)) el.min = Math.floor(value - 1);
  if (value > parseFloat(el.max)) el.max = Math.ceil(value + 1);
  el.value = value;
}
function updateAlignUi() {
  setRange('alignX', S2.alignX);
  setRange('alignZ', S2.alignZ);
  setRange('alignYaw', S2.alignYaw * 180 / Math.PI);
  document.getElementById('alignXOut').textContent = S2.alignX.toFixed(2).replace('.', ',') + ' m';
  document.getElementById('alignZOut').textContent = S2.alignZ.toFixed(2).replace('.', ',') + ' m';
  document.getElementById('alignYawOut').textContent =
    (S2.alignYaw * 180 / Math.PI).toFixed(1).replace('.', ',') + '°';
  const err = S2.error == null ? '' : ' · Wandfehler Ø ' +
    S2.error.toFixed(2).replace('.', ',') + ' m';
  document.getElementById('alignStatus').textContent =
    (S2.alignmentSource || 'nicht ausgerichtet') + err;
}

// ---------- websocket ----------
function connect() {
  const ws = new WebSocket('ws://' + location.host + '/ws');
  ws.onopen = () => {
    statusEl.innerHTML = `<span class='dot' style='background:#27ae60'></span>live`;
    if (outageStart) { outages.push({ a: outageStart, b: Date.now() }); outageStart = 0; }
  };
  ws.onclose = () => {
    statusEl.innerHTML = `<span class='dot' style='background:#c0392b'></span>getrennt`;
    if (!outageStart) outageStart = Date.now();
    setTimeout(connect, 1500);
  };
  ws.onmessage = ev => { try { handle(JSON.parse(ev.data)); } catch (e) {} };
}

function handle(m) {
  if (!m.type) { addPoint(m); draw(); drawTimeline(); return; }
  if (m.gridYawDegrees !== undefined) {
    const nextYaw = m.gridYawDegrees * Math.PI / 180;
    const changed = Math.abs(nextYaw - gridYaw) > 0.0001;
    gridYaw = nextYaw;
    if (changed && S2.loaded && S2.alignmentSource.startsWith('automatisch')) {
      autoAlignReference(); if (!S2.needsAuto) saveAlignment();
    }
  }
  switch (m.type) {
    case 'speed_request': runSpeedTest(m.requestId); break;
    case 'samplebatch': (m.items || []).forEach(addPoint); break;
    case 'pose': {
      S.pose = m;
      const t = S.trail, last = t[t.length - 1];
      if (!last || Math.hypot(m.posX - last.x, m.posZ - last.z) > 0.2) {
        t.push({ x: m.posX, z: m.posZ });
        if (t.length > 800) t.shift();
      }
      break;
    }
    case 'rooms':
      S.rooms = m.rooms || [];
      floorBases = m.floorBases || [];
      floorLabels = m.floorLabels || [];
      relabelFloorOptions();
      if (S2.loaded && (S2.needsAuto ||
          S2.alignmentSource.startsWith('automatisch'))) {
        autoAlignReference();
        if (!S2.needsAuto) saveAlignment();
      }
      break;
    case 'apfix': S.apfix = m; S.apfixAll = m.aps || null; break;
    case 'ping': S.pings = m.nodes || []; break;
    case 'notes': S.notes = m.notes || []; break;
    case 'drill': S.drills = m.lines || []; break;
    case 'roam': S.roams = m.events || []; break;
    case 'throughput': S.throughput = m.nodes || []; break;
    case 'clear': resetDashboardSession(); break;
  }
  draw();
  drawTimeline();
}

function resetDashboardSession() {
  speedGeneration++; speedRunning = false;
  document.getElementById('btnSpeed').disabled = false;
  S.points = []; S.pose = null; S.rooms = []; S.apfix = null; S.apfixAll = null;
  S.pings = []; S.notes = []; S.drills = []; S.trail = []; S.roams = [];
  S.throughput = [];
  S2.points = []; S2.rooms = []; S2.loaded = false; S2.stem = '';
  S2.alignX = S2.alignZ = S2.alignYaw = 0; S2.error = null;
  compareOn = false; pivotPick = false; selected = null;
  hist.length = 0; outages.length = 0; outageStart = 0;
  floorsSeen.clear(); apsSeen.clear(); floorBases = []; floorLabels = [];
  floorSel = 'all'; apSel = 'all'; gridYaw = 0;
  document.getElementById('floor').innerHTML = `<option value='all'>Etage: Alle</option>`;
  document.getElementById('ap').innerHTML = `<option value='all'>AP: Alle</option>`;
  document.getElementById('cmpScan').value = '';
  document.getElementById('alignControls').style.display = 'none';
  document.getElementById('btnCompare').classList.remove('active');
  document.getElementById('btnPivot').classList.remove('active');
  document.getElementById('btnPivot').textContent = 'Drehpunkt im Plan setzen';
  document.getElementById('speedStatus').textContent = 'Browser dient als lokale Gegenstelle.';
  popupEl.style.display = 'none';
  fcCanvas = null; fcKey = '';
  updateCompareUi();
}

function bandLabel(freq) {
  if (freq >= 5925) return '6 GHz';
  if (freq >= 4900) return '5 GHz';
  if (freq >= 2300) return '2,4 GHz';
  return '?';
}

// Etagen-Zuordnung: Index der Etage direkt unter y (wie FloorIndexForY).
function floorIndexForY(y) {
  let idx = 0;
  for (let i = 0; i < floorBases.length; i++)
    if (y >= floorBases[i] - 0.5) idx = i;
  return idx;
}
function floorName(f) {
  return floorLabels[f] !== undefined ? floorLabels[f] : 'Etage ' + (f + 1);
}
function relabelFloorOptions() {
  document.querySelectorAll(`#floor option`).forEach(o => {
    if (o.value !== 'all') o.textContent = floorName(parseInt(o.value));
    else o.textContent = 'Etage: Alle';
  });
}
// Nur Räume der gewählten Etage (kein Etagenwissen -> alle zeigen).
function roomOnSelectedFloor(room) {
  if (floorSel === 'all' || !floorBases.length) return true;
  return floorIndexForY((room.floorY || 0) + 0.1) === parseInt(floorSel);
}

function addPoint(s) {
  const f = s.floor || 0;
  const b = s.bssid || '';
  S.points.push({ x: s.posX, y: s.posY || 0, z: s.posZ, r: s.rssiDbm, f: f, b: b });
  if (S.points.length > 8000) S.points.shift();
  hist.push({ t: Date.now(), r: s.rssiDbm });
  if (hist.length > 4000) hist.shift();
  if (!floorsSeen.has(f)) {
    floorsSeen.add(f);
    const sel = document.getElementById('floor');
    const o = document.createElement('option');
    o.value = f; o.textContent = floorName(f);
    sel.appendChild(o);
  }
  if (b && !apsSeen.has(b)) {
    apsSeen.set(b, true);
    const sel = document.getElementById('ap');
    const o = document.createElement('option');
    o.value = b;
    o.textContent = 'AP ' + apsSeen.size + ' · ' + bandLabel(s.frequencyMhz || 0)
      + ' (…' + b.slice(-5) + ')';
    sel.appendChild(o);
  }
  if (S2.loaded && S2.needsAuto && S.points.length >= 5) {
    autoAlignReference();
    if (!S2.needsAuto) saveAlignment();
  }
}

// ---------- colours / legend ----------
function rssiColor(r) {
  let t = (r - rssiWeak) / (rssiStrong - rssiWeak);
  t = Math.max(0, Math.min(1, t));
  let cr, cg;
  if (t < 0.5) { cr = 220; cg = Math.round(60 + t * 2 * 160); }
  else { cr = Math.round(220 - (t - 0.5) * 2 * 180); cg = 200; }
  return `rgb(${cr},${cg},70)`;
}
function drawLegend() {
  const lc = document.getElementById('legendbar').getContext('2d');
  for (let x = 0; x < 110; x++) {
    lc.fillStyle = rssiColor(rssiWeak + (rssiStrong - rssiWeak) * x / 109);
    lc.fillRect(x, 0, 1, 12);
  }
}

// ---------- transforms ----------
function filterPoints(list) {
  return list.filter(p =>
    (floorSel === 'all' || String(p.f) === String(floorSel)) &&
    (apSel === 'all' || p.b === apSel));
}
function activePoints() { return filterPoints(S.points); }

function gr(x, z) {
  return gridWithYaw(x, z, gridYaw);
}
function P(x, z) { const g = gr(x, z); return [sx(g[0]), sy(g[1])]; }

let T = { minx:0, minz:0, scale:1, ox:0, oy:0, ch:0 };
function computeTransform(pts) {
  let minx = 1e9, maxx = -1e9, minz = 1e9, maxz = -1e9;
  const addGrid = g => {
    if (g[0] < minx) minx = g[0]; if (g[0] > maxx) maxx = g[0];
    if (g[1] < minz) minz = g[1]; if (g[1] > maxz) maxz = g[1]; };
  const add = (x, z) => addGrid(gr(x, z));
  const addRef = (x, z) => addGrid(referenceGrid(x, z));
  pts.forEach(p => add(p.x, p.z));
  if (L.rooms || mode === 'forecast')
    S.rooms.filter(roomOnSelectedFloor)
      .forEach(r => (r.cornersXZ || []).forEach(c => add(c.x, c.y)));
  if (compareOn) {
    filterPoints(S2.points).forEach(p => addRef(p.x, p.z));
    S2.rooms.filter(roomOnSelectedFloor)
      .forEach(r => (r.cornersXZ || []).forEach(c => addRef(c.x, c.y)));
  }
  if (minx > maxx) { minx = -2; maxx = 2; minz = -2; maxz = 2; }
  const pad = 0.6;
  minx -= pad; maxx += pad; minz -= pad; maxz += pad;
  const cw = cv.width, ch = cv.height;
  const scale = Math.min(cw / (maxx - minx), ch / (maxz - minz));
  T = { minx, minz, scale, ox: (cw - (maxx - minx) * scale) / 2,
        oy: (ch - (maxz - minz) * scale) / 2, ch };
}
function sx(x) { return (x - T.minx) * T.scale + T.ox; }
function sy(z) { return T.ch - ((z - T.minz) * T.scale + T.oy); }

// ---------- main draw ----------
function resize() {
  cv.width = cv.clientWidth; cv.height = cv.clientHeight;
  tl.width = tl.clientWidth; tl.height = tl.clientHeight;
  positionDivider();
  draw();
  drawTimeline();
}
window.addEventListener('resize', resize);

function draw() {
  const pts = activePoints();
  if (mode === '3d') {
    cx.clearRect(0, 0, cv.width, cv.height);
    draw3d(pts);
    updateStats(pts.length);
    return;
  }
  computeTransform(pts);
  cx.clearRect(0, 0, cv.width, cv.height);

  const split = compareOn && (mode === 'points' || mode === 'heat');
  if (split) {
    const dx = divFrac * cv.width;
    // Links: Vorher-Scan (Punkte/Heatmap + gestrichelter Grundriss).
    cx.save();
    cx.beginPath(); cx.rect(0, 0, dx, cv.height); cx.clip();
    const refPts = filterPoints(S2.points);
    if (mode === 'heat') drawHeatmap(refPts, referenceGrid);
    else refPts.forEach(p => {
      cx.fillStyle = rssiColor(p.r);
      const q = PRef(p.x, p.z);
      cx.fillRect(q[0] - 3, q[1] - 3, 6, 6);
    });
    cx.setLineDash([6, 4]);
    S2.rooms.filter(roomOnSelectedFloor).forEach(r => drawRoom(r, PRef));
    cx.setLineDash([]);
    drawReferencePivot();
    cx.restore();
    // Rechts: live.
    cx.save();
    cx.beginPath(); cx.rect(dx, 0, cv.width - dx, cv.height); cx.clip();
    drawLive(pts);
    cx.restore();
  } else {
    drawLive(pts);
  }
  if (selected) drawSelectionRing();
  updateStats(pts.length);
}

function drawLive(pts) {
  if (mode === 'heat') drawHeatmap(pts);
  else if (mode === 'forecast') drawForecast();
  else if (L.points) pts.forEach(p => {
    cx.fillStyle = rssiColor(p.r);
    const q = P(p.x, p.z);
    cx.fillRect(q[0] - 3, q[1] - 3, 6, 6);
  });
  if (L.trail) drawTrail();
  if (L.rooms) S.rooms.filter(roomOnSelectedFloor).forEach(r => drawRoom(r));
  if (L.ping) S.pings.forEach(drawPing);
  if (L.throughput) S.throughput
    .filter(p => floorSel === 'all' || String(p.floor || 0) === String(floorSel))
    .forEach(drawThroughput);
  if (L.notes) S.notes.forEach(drawNote);
  if (L.roam) S.roams.forEach(drawRoam);
  if (L.ap) drawAps();
  if (L.pose && S.pose) drawPose(S.pose);
}

// ---------- heatmap (IDW) ----------
function drawHeatmap(pts, project) {
  if (!pts.length) return;
  project = project || gr;
  const gp = pts.map(p => { const g = project(p.x, p.z); return { x: g[0], z: g[1], r: p.r }; });
  let minx = 1e9, maxx = -1e9, minz = 1e9, maxz = -1e9;
  gp.forEach(p => { if (p.x < minx) minx = p.x; if (p.x > maxx) maxx = p.x;
    if (p.z < minz) minz = p.z; if (p.z > maxz) maxz = p.z; });
  const R = 1.2, R2 = R * R;
  const area = Math.max(0.5, maxx - minx) * Math.max(0.5, maxz - minz);
  const cell = Math.max(0.15, Math.sqrt(area / 18000));
  const hash = new Map();
  const key = (a, b) => a + ',' + b;
  gp.forEach(p => {
    const k = key(Math.floor(p.x / R), Math.floor(p.z / R));
    let a = hash.get(k); if (!a) { a = []; hash.set(k, a); } a.push(p);
  });
  const nx = Math.ceil((maxx - minx) / cell), nz = Math.ceil((maxz - minz) / cell);
  const cs = cell * T.scale + 1;
  for (let iz = 0; iz < nz; iz++) {
    const wz = minz + (iz + 0.5) * cell, hz = Math.floor(wz / R);
    for (let ix = 0; ix < nx; ix++) {
      const wx = minx + (ix + 0.5) * cell, hx = Math.floor(wx / R);
      let wsum = 0, vsum = 0;
      for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) {
        const a = hash.get(key(hx + dx, hz + dz)); if (!a) continue;
        for (const p of a) {
          const ddx = p.x - wx, ddz = p.z - wz, sq = ddx * ddx + ddz * ddz;
          if (sq > R2) continue;
          const w = 1 / (sq + 0.01); wsum += w; vsum += w * p.r;
        }
      }
      if (wsum <= 0) continue;
      cx.fillStyle = rssiColor(vsum / wsum);
      cx.fillRect(sx(wx) - cs / 2, sy(wz) - cs / 2, cs, cs);
    }
  }
}

// ---------- forecast (cached) ----------
let fcCanvas = null, fcKey = '';
function forecastKey() {
  const vaps = (S.notes || []).filter(n => n.type === 'VirtualAp')
    .map(n => [n.posX.toFixed(2), n.posZ.toFixed(2)]);
  const ap = S.apfix ? [S.apfix.posX.toFixed(2), S.apfix.posZ.toFixed(2),
    S.apfix.pathLossN, S.apfix.powerAt1m] : null;
  return JSON.stringify([S.rooms.length,
    S.rooms.map(r => (r.cornersXZ || []).length), vaps, ap, gridYaw,
    cv.width, cv.height, T.minx, T.minz, T.scale, rssiWeak, rssiStrong,
    floorSel]);
}
function drawForecast() {
  if (!S.rooms.length) {
    label('Prognose: erst Räume zeichnen', cv.width / 2, 24, '#e0a03c');
    return;
  }
  const key = forecastKey();
  if (key !== fcKey || !fcCanvas) { fcKey = key; renderForecast(); }
  if (fcCanvas) cx.drawImage(fcCanvas, 0, 0);
  const hasAp = (S.notes || []).some(n => n.type === 'VirtualAp') || S.apfix;
  if (!hasAp)
    label('Kein AP: virtuellen AP setzen (Notiz) oder messen', cv.width / 2, 24, '#ff5544');
}
function renderForecast() {
  fcCanvas = document.createElement('canvas');
  fcCanvas.width = cv.width; fcCanvas.height = cv.height;
  const fc = fcCanvas.getContext('2d');
  const walls = [];
  let minx = 1e9, maxx = -1e9, minz = 1e9, maxz = -1e9;
  S.rooms.filter(roomOnSelectedFloor).forEach(r => {
    const c = r.cornersXZ || [], n = c.length;
    for (let i = 0; i < n; i++) {
      const a = gr(c[i].x, c[i].y), b = gr(c[(i + 1) % n].x, c[(i + 1) % n].y);
      walls.push([a[0], a[1], b[0], b[1]]);
      if (a[0] < minx) minx = a[0]; if (a[0] > maxx) maxx = a[0];
      if (a[1] < minz) minz = a[1]; if (a[1] > maxz) maxz = a[1];
    }
  });
  if (!walls.length) return;
  minx -= 1; maxx += 1; minz -= 1; maxz += 1;
  const n = (S.apfix && S.apfix.pathLossN) ? S.apfix.pathLossN : 2.7;
  const aps = [];
  (S.notes || []).forEach(nt => {
    if (nt.type === 'VirtualAp') {
      const g = gr(nt.posX, nt.posZ);
      aps.push({ x: g[0], z: g[1], p1: -40, n: n });
    }
  });
  if (!aps.length && S.apfix) {
    const g = gr(S.apfix.posX, S.apfix.posZ);
    aps.push({ x: g[0], z: g[1], p1: (S.apfix.powerAt1m || -40), n: n });
  }
  const cell = Math.max(0.15,
    Math.sqrt(Math.max(0.5, maxx - minx) * Math.max(0.5, maxz - minz) / 15000));
  const cs = cell * T.scale + 1;
  const nx = Math.ceil((maxx - minx) / cell), nz = Math.ceil((maxz - minz) / cell);
  for (let iz = 0; iz < nz; iz++) {
    const wz = minz + (iz + 0.5) * cell;
    for (let ix = 0; ix < nx; ix++) {
      const wx = minx + (ix + 0.5) * cell;
      if (!aps.length) {
        fc.fillStyle = 'rgb(200,25,20)';
      } else {
        let best = -127;
        for (const ap of aps) {
          const d = Math.max(0.3, Math.hypot(wx - ap.x, wz - ap.z, 1.4));
          let r = ap.p1 - 10 * ap.n * Math.log10(d);
          for (const w of walls)
            if (segInt(wx, wz, ap.x, ap.z, w[0], w[1], w[2], w[3])) r -= 5;
          if (r > best) best = r;
        }
        fc.fillStyle = rssiColor(best);
      }
      fc.fillRect(sx(wx) - cs / 2, sy(wz) - cs / 2, cs, cs);
    }
  }
}
function segInt(p1x, p1z, p2x, p2z, q1x, q1z, q2x, q2z) {
  const d1 = (q2x - q1x) * (p1z - q1z) - (q2z - q1z) * (p1x - q1x);
  const d2 = (q2x - q1x) * (p2z - q1z) - (q2z - q1z) * (p2x - q1x);
  const d3 = (p2x - p1x) * (q1z - p1z) - (p2z - p1z) * (q1x - p1x);
  const d4 = (p2x - p1x) * (q2z - p1z) - (p2z - p1z) * (q2x - p1x);
  return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
         ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
}

// ---------- overlays ----------
function drawTrail() {
  if (S.trail.length < 2) return;
  cx.strokeStyle = '#ff9d3c';
  cx.lineWidth = 2;
  cx.beginPath();
  const p0 = P(S.trail[0].x, S.trail[0].z);
  cx.moveTo(p0[0], p0[1]);
  for (let i = 1; i < S.trail.length; i++) {
    const q = P(S.trail[i].x, S.trail[i].z);
    cx.lineTo(q[0], q[1]);
  }
  cx.stroke();
}

function drawRoom(room, project) {
  project = project || P;
  const c = room.cornersXZ || [];
  const n = c.length;
  if (n < 2) return;
  const ops = room.openings || [];
  cx.strokeStyle = '#5aa0d8';
  cx.lineWidth = 2;
  for (let i = 0; i < n; i++) {
    const a = c[i], b = c[(i + 1) % n];
    const len = Math.hypot(b.x - a.x, b.y - a.y) || 1;
    const eops = ops.filter(o => o.edgeIndex === i).sort((p, q) => p.startAlong - q.startAlong);
    let cur = 0;
    const seg = (f0, f1) => {
      const p0 = project(a.x + (b.x - a.x) * f0, a.y + (b.y - a.y) * f0);
      const p1 = project(a.x + (b.x - a.x) * f1, a.y + (b.y - a.y) * f1);
      cx.beginPath();
      cx.moveTo(p0[0], p0[1]);
      cx.lineTo(p1[0], p1[1]);
      cx.stroke();
    };
    for (const o of eops) {
      const s0 = Math.max(cur, Math.min(1, o.startAlong / len));
      const e0 = Math.min(1, o.endAlong / len);
      if (s0 > cur) seg(cur, s0);
      drawOpening(a, b, s0, e0, o.isDoor, project);
      cur = Math.max(cur, e0);
    }
    if (cur < 1) seg(cur, 1);
  }
  if (room.name) {
    let cxm = 0, czm = 0;
    c.forEach(p => { cxm += p.x; czm += p.y; });
    const q = project(cxm / n, czm / n);
    label(room.name, q[0], q[1], '#8fd4ff');
  }
}

function drawOpening(a, b, f0, f1, isDoor, project) {
  project = project || P;
  const mx = a.x + (b.x - a.x) * (f0 + f1) / 2;
  const mz = a.y + (b.y - a.y) * (f0 + f1) / 2;
  const q = project(mx, mz);
  cx.fillStyle = isDoor ? '#e08a3c' : '#e0c53c';
  cx.beginPath();
  cx.arc(q[0], q[1], 4, 0, 7);
  cx.fill();
}

function drawPose(p) {
  const q = P(p.posX, p.posZ);
  const a = p.yawDeg * Math.PI / 180;
  const tip = P(p.posX + Math.sin(a) * 0.6, p.posZ + Math.cos(a) * 0.6);
  cx.fillStyle = '#25b7dd';
  cx.beginPath(); cx.arc(q[0], q[1], 6, 0, 7); cx.fill();
  cx.strokeStyle = '#25b7dd'; cx.lineWidth = 3;
  cx.beginPath(); cx.moveTo(q[0], q[1]); cx.lineTo(tip[0], tip[1]); cx.stroke();
}

function drawAps() {
  if (S.apfixAll && S.apfixAll.length) {
    S.apfixAll.forEach(ap => {
      if (apSel !== 'all' && ap.bssid !== apSel) return;
      const q = P(ap.posX, ap.posZ);
      cx.fillStyle = '#00d2ff';
      star(q[0], q[1], 5, 11, 5);
      label((ap.ssid || 'AP') + ' · ' + bandLabel(ap.frequencyMhz || 0) +
        ' (' + ap.strongestRssi + ' dBm)', q[0], q[1] - 16, '#00d2ff');
    });
  } else if (S.apfix) {
    const q = P(S.apfix.posX, S.apfix.posZ);
    cx.fillStyle = '#00d2ff';
    star(q[0], q[1], 5, 11, 5);
    label((S.apfix.ssid || 'AP') + ' (' + S.apfix.rssi + ' dBm)', q[0], q[1] - 16, '#00d2ff');
  }
}

function drawPing(p) {
  const q = P(p.posX, p.posZ);
  const ms = p.latencyMs;
  cx.fillStyle = ms < 0 ? '#cc2222' : ms < 50 ? '#22bb44' : ms < 120 ? '#e6b800' : '#cc5522';
  cx.beginPath(); cx.arc(q[0], q[1], 6, 0, 7); cx.fill();
  label(ms < 0 ? 'Fehler' : ms + ' ms', q[0], q[1] - 12, '#ddd');
}

function drawThroughput(p) {
  const q = P(p.posX, p.posZ);
  const weak = Math.min(p.questToBrowserMbps || 0, p.browserToQuestMbps || 0);
  cx.fillStyle = weak >= 150 ? '#35e65b' : weak >= 50 ? '#f0c629' : '#ef5348';
  cx.beginPath(); cx.arc(q[0], q[1], 7, 0, 7); cx.fill();
  cx.strokeStyle = '#ffffff'; cx.lineWidth = 1;
  cx.beginPath(); cx.arc(q[0], q[1], 9, 0, 7); cx.stroke();
  label('⇢' + (p.questToBrowserMbps || 0).toFixed(0) + ' ⇠' +
    (p.browserToQuestMbps || 0).toFixed(0) + ' Mbit/s', q[0], q[1] - 15, '#dff5ff');
}

function drawNote(nt) {
  const q = P(nt.posX, nt.posZ);
  cx.fillStyle = nt.type === 'VirtualAp' ? '#25b7dd' : nt.type === 'Interference' ? '#dd4433' : '#cccccc';
  cx.fillRect(q[0] - 5, q[1] - 5, 10, 10);
  if (nt.text) label(nt.text, q[0], q[1] - 12, '#ddd');
}

function drawRoam(ev) {
  const q = P(ev.posX, ev.posZ);
  cx.fillStyle = '#ff9d3c';
  cx.beginPath();
  cx.moveTo(q[0], q[1] - 7);
  cx.lineTo(q[0] + 7, q[1]);
  cx.lineTo(q[0], q[1] + 7);
  cx.lineTo(q[0] - 7, q[1]);
  cx.closePath();
  cx.fill();
}

function star(x, y, ri, ro, pts) {
  cx.beginPath();
  for (let i = 0; i < pts * 2; i++) {
    const r = i % 2 ? ri : ro;
    const a = Math.PI * i / pts - Math.PI / 2;
    const px = x + Math.cos(a) * r, py = y + Math.sin(a) * r;
    i ? cx.lineTo(px, py) : cx.moveTo(px, py);
  }
  cx.closePath(); cx.fill();
}

function label(text, x, y, color) {
  cx.font = '12px system-ui';
  cx.textAlign = 'center';
  const w = cx.measureText(text).width + 8;
  cx.fillStyle = 'rgba(0,0,0,0.6)';
  cx.fillRect(x - w / 2, y - 12, w, 15);
  cx.fillStyle = color;
  cx.fillText(text, x, y);
}

// ---------- 3D view ----------
let yaw3 = 0.8, pitch3 = 0.5, zoom3 = 1;
const roomPalette = ['#5aa0d8', '#7cd85a', '#d8a05a', '#c05ad8', '#5ad8c8', '#d85a6e'];

function bounds3(pts) {
  let n = 0, sx3 = 0, sy3 = 0, sz3 = 0, ext = 1;
  const acc = (x, y, z) => { sx3 += x; sy3 += y; sz3 += z; n++; };
  S.rooms.forEach(r => {
    const h = (r.height && r.height > 0.05) ? r.height : 2.5;
    (r.cornersXZ || []).forEach(c => { acc(c.x, r.floorY || 0, c.y); acc(c.x, (r.floorY || 0) + h, c.y); });
  });
  pts.forEach(p => acc(p.x, p.y, p.z));
  if (!n) return { x: 0, y: 0, z: 0, ext: 3 };
  const ctr = { x: sx3 / n, y: sy3 / n, z: sz3 / n };
  const d2 = (x, y, z) => Math.max(Math.abs(x - ctr.x), Math.abs(y - ctr.y), Math.abs(z - ctr.z));
  S.rooms.forEach(r => {
    const h = (r.height && r.height > 0.05) ? r.height : 2.5;
    (r.cornersXZ || []).forEach(c => { ext = Math.max(ext, d2(c.x, r.floorY || 0, c.y), d2(c.x, (r.floorY || 0) + h, c.y)); });
  });
  pts.forEach(p => { ext = Math.max(ext, d2(p.x, p.y, p.z)); });
  ctr.ext = ext;
  return ctr;
}

function draw3d(pts) {
  const ctr = bounds3(pts);
  const scale3 = Math.min(cv.width, cv.height) * 0.42 / ctr.ext * zoom3;
  const cyaw = Math.cos(yaw3), syaw = Math.sin(yaw3);
  const cp = Math.cos(pitch3), sp = Math.sin(pitch3);
  const V = (x, y, z) => {
    // X negiert: Unity ist linkshändig (gleiche Konvention wie OBJ-Export).
    const dx = ctr.x - x, dy = y - ctr.y, dz = z - ctr.z;
    const rx = dx * cyaw - dz * syaw;
    const rz = dx * syaw + dz * cyaw;
    const ry = dy * cp - rz * sp;
    return [cv.width / 2 + rx * scale3, cv.height / 2 - ry * scale3];
  };
  if (L.points) pts.forEach(p => {
    const q = V(p.x, p.y, p.z);
    cx.fillStyle = rssiColor(p.r);
    cx.fillRect(q[0] - 2, q[1] - 2, 4, 4);
  });
  S.rooms.forEach((r, ri) => {
    const c = r.cornersXZ || [];
    if (c.length < 3) return;
    const y0 = r.floorY || 0;
    const h = (r.height && r.height > 0.05) ? r.height : 2.5;
    cx.strokeStyle = roomPalette[ri % roomPalette.length];
    cx.lineWidth = 1.6;
    const loop = (yy) => {
      cx.beginPath();
      for (let i = 0; i <= c.length; i++) {
        const p = c[i % c.length];
        const q = V(p.x, yy, p.y);
        i ? cx.lineTo(q[0], q[1]) : cx.moveTo(q[0], q[1]);
      }
      cx.stroke();
    };
    loop(y0);
    loop(y0 + h);
    for (const p of c) {
      const a = V(p.x, y0, p.y), b = V(p.x, y0 + h, p.y);
      cx.beginPath(); cx.moveTo(a[0], a[1]); cx.lineTo(b[0], b[1]); cx.stroke();
    }
    if (r.name) {
      let cxm = 0, czm = 0;
      c.forEach(p => { cxm += p.x; czm += p.y; });
      const q = V(cxm / c.length, y0 + 1.2, czm / c.length);
      label(r.name, q[0], q[1], '#8fd4ff');
    }
  });
  if (L.ap && S.apfix) {
    const q = V(S.apfix.posX, S.apfix.posY, S.apfix.posZ);
    cx.fillStyle = '#00d2ff';
    star(q[0], q[1], 5, 11, 5);
    label((S.apfix.ssid || 'AP'), q[0], q[1] - 16, '#00d2ff');
  }
  if (L.pose && S.pose) {
    const q = V(S.pose.posX, S.pose.posY || 1.6, S.pose.posZ);
    cx.fillStyle = '#25b7dd';
    cx.beginPath(); cx.arc(q[0], q[1], 5, 0, 7); cx.fill();
  }
  label('Ziehen = drehen, Rad/Pinch = Zoom', cv.width / 2, cv.height - 8, '#8a93a6');
}

let dragging = false, lastX = 0, lastY = 0, lastPinch = 0, moved = false;
cv.addEventListener('mousedown', e => { dragging = true; moved = false; lastX = e.clientX; lastY = e.clientY; });
window.addEventListener('mouseup', () => { dragging = false; });
window.addEventListener('mousemove', e => {
  if (!dragging || mode !== '3d') return;
  moved = true;
  yaw3 += (e.clientX - lastX) * 0.008;
  pitch3 = Math.max(0.05, Math.min(1.5, pitch3 + (e.clientY - lastY) * 0.006));
  lastX = e.clientX; lastY = e.clientY;
  draw();
});
cv.addEventListener('wheel', e => {
  if (mode !== '3d') return;
  e.preventDefault();
  zoom3 = Math.max(0.3, Math.min(6, zoom3 * (e.deltaY < 0 ? 1.1 : 0.9)));
  draw();
}, { passive: false });
cv.addEventListener('touchstart', e => {
  if (e.touches.length === 1) { lastX = e.touches[0].clientX; lastY = e.touches[0].clientY; }
  else if (e.touches.length === 2) lastPinch = Math.hypot(
    e.touches[0].clientX - e.touches[1].clientX,
    e.touches[0].clientY - e.touches[1].clientY);
}, { passive: true });
cv.addEventListener('touchmove', e => {
  if (mode !== '3d') return;
  if (e.touches.length === 1) {
    yaw3 += (e.touches[0].clientX - lastX) * 0.008;
    pitch3 = Math.max(0.05, Math.min(1.5, pitch3 + (e.touches[0].clientY - lastY) * 0.006));
    lastX = e.touches[0].clientX; lastY = e.touches[0].clientY;
  } else if (e.touches.length === 2) {
    const d = Math.hypot(
      e.touches[0].clientX - e.touches[1].clientX,
      e.touches[0].clientY - e.touches[1].clientY);
    if (lastPinch > 0) zoom3 = Math.max(0.3, Math.min(6, zoom3 * d / lastPinch));
    lastPinch = d;
  }
  e.preventDefault();
  draw();
}, { passive: false });

// ---------- selection tool ----------
cv.addEventListener('click', e => {
  if (mode === '3d' && moved) return; // Orbit-Drag ist keine Auswahl
  if (mode === '3d') return;
  const r = cv.getBoundingClientRect();
  const px = e.clientX - r.left, py = e.clientY - r.top;
  if (pivotPick && compareOn) {
    setPivotFromScreen(px, py);
    return;
  }
  pick(px, py);
});

function setPivotFromScreen(px, py) {
  const mapped = [T.minx + (px - T.ox) / T.scale,
    T.minz + (T.ch - py - T.oy) / T.scale];
  // Invert the current reference transform so changing the pivot does not
  // make the plan jump. Only later slider rotations use the new hinge point.
  const shifted = [mapped[0] - S2.alignX - S2.pivotX,
                   mapped[1] - S2.alignZ - S2.pivotZ];
  const inv = rotate2(shifted[0], shifted[1], -S2.alignYaw);
  const base = [inv[0] + S2.pivotX, inv[1] + S2.pivotZ];
  S2.pivotX = base[0]; S2.pivotZ = base[1];
  S2.alignX = mapped[0] - base[0];
  S2.alignZ = mapped[1] - base[1];
  S2.needsAuto = false;
  S2.alignmentSource = 'manuell · eigener Drehpunkt';
  pivotPick = false;
  const btn = document.getElementById('btnPivot');
  btn.classList.remove('active');
  btn.textContent = 'Drehpunkt im Plan setzen';
  updateAlignmentError(); updateAlignUi(); saveAlignment(); draw();
}

function drawReferencePivot() {
  if (!S2.loaded) return;
  const q = referenceGridFromBase([S2.pivotX, S2.pivotZ]);
  const x = sx(q[0]), y = sy(q[1]);
  cx.save();
  cx.strokeStyle = pivotPick ? '#ffd166' : 'rgba(255,209,102,0.65)';
  cx.lineWidth = 2;
  cx.beginPath(); cx.arc(x, y, 6, 0, 7); cx.stroke();
  cx.beginPath(); cx.moveTo(x - 9, y); cx.lineTo(x + 9, y);
  cx.moveTo(x, y - 9); cx.lineTo(x, y + 9); cx.stroke();
  cx.restore();
}

function pick(px, py) {
  const hitR = 14;
  let best = null, bestD = hitR;
  const test = (q, obj) => {
    const d = Math.hypot(q[0] - px, q[1] - py);
    if (d < bestD) { bestD = d; best = obj; }
  };
  if (S.apfixAll) S.apfixAll.forEach(ap => {
    if (apSel !== 'all' && ap.bssid !== apSel) return;
    test(P(ap.posX, ap.posZ), { kind: 'ap', ap });
  });
  S.notes.forEach(n => test(P(n.posX, n.posZ), { kind: 'note', n }));
  S.pings.forEach(p => test(P(p.posX, p.posZ), { kind: 'ping', p }));
  S.throughput.forEach(p => test(P(p.posX, p.posZ), { kind: 'throughput', p }));
  S.roams.forEach(ev => test(P(ev.posX, ev.posZ), { kind: 'roam', ev }));
  activePoints().forEach(p => test(P(p.x, p.z), { kind: 'point', p }));

  selected = best;
  if (!best) { popupEl.style.display = 'none'; draw(); return; }
  let txt = '';
  if (best.kind === 'point')
    txt = 'Messpunkt\nRSSI: ' + best.p.r + ' dBm\nEtage ' + (best.p.f + 1) +
      (best.p.b ? '\nAP: …' + best.p.b.slice(-5) : '') +
      '\nx ' + best.p.x.toFixed(2) + ' / z ' + best.p.z.toFixed(2) + ' m';
  else if (best.kind === 'ap')
    txt = 'Access Point\n' + (best.ap.ssid || '?') + ' · ' +
      bandLabel(best.ap.frequencyMhz || 0) + '\nBSSID …' +
      (best.ap.bssid || '').slice(-8) + '\nmax ' + best.ap.strongestRssi +
      ' dBm · ±' + (best.ap.radius || 0).toFixed(1) + ' m\nn = ' +
      (best.ap.pathLossN || 0).toFixed(2) + ' · P1m = ' +
      (best.ap.powerAt1m || 0).toFixed(1) + ' dBm';
  else if (best.kind === 'note')
    txt = 'Notiz (' + best.n.type + ')\n' + (best.n.text || '');
  else if (best.kind === 'ping')
    txt = 'Ping-Knoten\n' +
      (best.p.latencyMs < 0 ? 'Fehler' : best.p.latencyMs + ' ms');
  else if (best.kind === 'throughput')
    txt = 'Lokaler Durchsatz\nQuest → Browser: ' +
      best.p.questToBrowserMbps.toFixed(1) + ' Mbit/s\nBrowser → Quest: ' +
      best.p.browserToQuestMbps.toFixed(1) + ' Mbit/s\nLatenz: ' +
      best.p.latencyMs.toFixed(0) + ' ms';
  else if (best.kind === 'roam')
    txt = 'Roaming\n…' + (best.ev.fromBssid || '').slice(-5) + ' → …' +
      (best.ev.toBssid || '').slice(-5) + '\n' +
      bandLabel(best.ev.fromFrequencyMhz) + ' → ' + bandLabel(best.ev.toFrequencyMhz);
  popupEl.textContent = txt;
  popupEl.style.display = 'block';
  popupEl.style.left = Math.min(px + 14, cv.width - 250) + 'px';
  popupEl.style.top = Math.max(6, py - 10) + 'px';
  draw();
}

function selectionPos() {
  const s = selected;
  if (!s) return null;
  if (s.kind === 'point') return P(s.p.x, s.p.z);
  if (s.kind === 'ap') return P(s.ap.posX, s.ap.posZ);
  if (s.kind === 'note') return P(s.n.posX, s.n.posZ);
  if (s.kind === 'ping') return P(s.p.posX, s.p.posZ);
  if (s.kind === 'throughput') return P(s.p.posX, s.p.posZ);
  if (s.kind === 'roam') return P(s.ev.posX, s.ev.posZ);
  return null;
}
function drawSelectionRing() {
  const q = selectionPos();
  if (!q) return;
  cx.strokeStyle = '#ffffff';
  cx.lineWidth = 2;
  cx.beginPath();
  cx.arc(q[0], q[1], 11, 0, 7);
  cx.stroke();
}

// ---------- timeline ----------
function drawTimeline() {
  const w = tl.width, h = tl.height;
  tx.clearRect(0, 0, w, h);
  if (!hist.length) {
    tx.fillStyle = '#5a6472';
    tx.font = '12px system-ui';
    tx.fillText('Aufnahmeverlauf erscheint mit den ersten Messpunkten …', 10, h / 2);
    return;
  }
  const t0 = hist[0].t;
  const t1 = Math.max(Date.now(), t0 + 60000);
  const X = t => 4 + (t - t0) / (t1 - t0) * (w - 8);
  const Y = r => {
    const lo = rssiWeak - 5, hi = rssiStrong + 5;
    return h - 16 - (Math.max(lo, Math.min(hi, r)) - lo) / (hi - lo) * (h - 26);
  };
  // Ausfälle (rote Bänder).
  tx.fillStyle = 'rgba(200,40,40,0.25)';
  outages.forEach(o => tx.fillRect(X(o.a), 0, Math.max(2, X(o.b) - X(o.a)), h));
  if (outageStart) tx.fillRect(X(outageStart), 0, Math.max(2, X(Date.now()) - X(outageStart)), h);
  // Schwellenlinie.
  tx.strokeStyle = '#333b48';
  tx.setLineDash([3, 4]);
  tx.beginPath(); tx.moveTo(0, Y(-75)); tx.lineTo(w, Y(-75)); tx.stroke();
  tx.setLineDash([]);
  // RSSI-Verlauf.
  tx.strokeStyle = '#4a90c8';
  tx.lineWidth = 1.5;
  tx.beginPath();
  hist.forEach((p2, i) => {
    const x = X(p2.t), y = Y(p2.r);
    i ? tx.lineTo(x, y) : tx.moveTo(x, y);
  });
  tx.stroke();
  // Roaming-Ticks.
  tx.fillStyle = '#ff9d3c';
  S.roams.forEach(ev => {
    const x = X(ev.timestampMs);
    if (x >= 0 && x <= w) { tx.fillRect(x - 1, 0, 2, h); }
  });
  // Aktuelle Leistungswerte.
  const last = hist[hist.length - 1];
  tx.fillStyle = '#aeb6c4';
  tx.font = '11px system-ui';
  tx.textAlign = 'left';
  tx.fillText('RSSI ' + last.r + ' dBm · ' + S.points.length + ' Punkte · ' +
    S.roams.length + ' Roaming · ' + outages.length + ' Ausfälle', 8, 13);
  tx.textAlign = 'right';
  tx.fillText(new Date(t0).toLocaleTimeString() + ' – ' +
    new Date(t1).toLocaleTimeString(), w - 8, 13);
  tx.textAlign = 'left';
}
setInterval(drawTimeline, 2000);

// ---------- stats / files ----------
function updateStats(shown) {
  const last = S.points[S.points.length - 1];
  statsEl.textContent =
    'Punkte: ' + S.points.length + ' (angezeigt ' + shown + ')\n' +
    'Räume: ' + S.rooms.length + '   Pings: ' + S.pings.length +
    '   Speed: ' + S.throughput.length + '\n' +
    'Notizen: ' + S.notes.length + '   APs: ' + apsSeen.size +
    '   Roaming: ' + S.roams.length +
    (compareOn ? '\nVergleich: ' + S2.stem : '') +
    (last ? '\nLetzte: ' + last.r + ' dBm' : '');
}

const sidecars = ['_rooms', '_drill', '_apfix', '_ping', '_notes', '_measure',
  '_heatmap', '_walls', '_report', '_coverage', '_roam', '_throughput'];
function isScanJson(name) {
  if (!name.endsWith('.json')) return false;
  const stem = name.slice(0, -5);
  return !sidecars.some(sfx => stem.endsWith(sfx) || stem.includes(sfx + '_'));
}

function loadFiles() {
  fetch('/scans').then(r => r.json()).then(d => {
    const files = (d.files || []);
    const box = document.getElementById('files');
    box.innerHTML = '';
    files.sort((a, b) => a.name.localeCompare(b.name)).forEach(f => {
      const a = document.createElement('a');
      a.href = '/download?f=' + encodeURIComponent(f.name);
      a.innerHTML = f.name + ` <span class='sz'>(${(f.size / 1024).toFixed(0)} kB)</span>`;
      box.appendChild(a);
    });
    if (!files.length) box.textContent = '(keine Dateien)';
    // Vergleichs-Auswahl: nur ladbare Sample-JSONs.
    const sel = document.getElementById('cmpScan');
    const cur = sel.value;
    sel.innerHTML = `<option value=''>&ndash; Scan w&auml;hlen &ndash;</option>`;
    files.filter(f => isScanJson(f.name)).forEach(f => {
      const o = document.createElement('option');
      o.value = f.name;
      o.textContent = f.name.replace('.json', '');
      sel.appendChild(o);
    });
    sel.value = cur;
  }).catch(() => {});
}

drawLegend();
resize();
connect();
loadFiles();
setInterval(loadFiles, 15000);
</script>
</body>
</html>";
    }
}
