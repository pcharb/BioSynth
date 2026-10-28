// BioSynth : interface (reprend l'ancienne MainWindow WPF)
'use strict';

const $ = id => document.getElementById(id);
const $$ = sel => Array.from(document.querySelectorAll(sel));
const META = { electrodes: {}, zones: [], channelNames: {}, dataDir: '', lslAvailable: false };

// ═══════════════════════════════════════════════════════════════════════════
// Utilitaires
// ═══════════════════════════════════════════════════════════════════════════

async function api(path, body) {
  try {
    const res = await fetch(path, {
      method: body === undefined ? 'GET' : 'POST',
      headers: body === undefined ? {} : { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    return await res.json();
  } catch (e) {
    return { ok: false, error: 'Serveur BioSynth injoignable : ' + e.message };
  }
}

let toastTimer = 0;
function toast(msg, isError = true) {
  const t = $('toast');
  t.textContent = msg;
  t.classList.toggle('err', isError);
  t.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), isError ? 9000 : 4000);
}
$('toast').addEventListener('click', () => $('toast').classList.add('hidden'));

const num = (id, fallback) => { const v = parseFloat($(id).value); return Number.isFinite(v) ? v : fallback; };
const radio = name => (document.querySelector(`input[name="${name}"]:checked`) || {}).value;
const fmtN = n => Number(n || 0).toLocaleString('fr-CA');
const splitPaths = s => (s || '').split(';').map(p => p.trim()).filter(Boolean);

/** Canvas net sur écrans haute densité ; retourne le contexte et la taille CSS. */
function fitCanvas(cv) {
  const dpr = window.devicePixelRatio || 1;
  const w = cv.clientWidth, h = cv.clientHeight;
  if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(h * dpr)) {
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
  }
  const ctx = cv.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { ctx, w, h };
}

// ═══════════════════════════════════════════════════════════════════════════
// Persistance des réglages (par navigateur)
// ═══════════════════════════════════════════════════════════════════════════

const FORM_KEY = 'biosynth.form';
function loadForm() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(FORM_KEY) || '{}'); } catch { /* rien */ }
  for (const el of $$('[data-persist]')) {
    const key = el.type === 'radio' ? `${el.name}=${el.value}` : el.id;
    if (!(key in saved)) continue;
    if (el.type === 'checkbox' || el.type === 'radio') el.checked = !!saved[key];
    else el.value = saved[key];
  }
}
function saveForm() {
  const data = {};
  for (const el of $$('[data-persist]')) {
    const key = el.type === 'radio' ? `${el.name}=${el.value}` : el.id;
    data[key] = (el.type === 'checkbox' || el.type === 'radio') ? el.checked : el.value;
  }
  try { localStorage.setItem(FORM_KEY, JSON.stringify(data)); } catch { /* rien */ }
}
document.addEventListener('change', e => { if (e.target.matches('[data-persist]')) saveForm(); });

// ═══════════════════════════════════════════════════════════════════════════
// Onglets, thème, contrôles simples
// ═══════════════════════════════════════════════════════════════════════════

let vizVisible = false;
function showTab(name) {
  $$('.tab').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  $$('.tabpane').forEach(p => p.classList.toggle('active', p.id === 'tab-' + name));
  vizVisible = name === 'viz';
  try { localStorage.setItem('biosynth.tab', name); } catch { /* rien */ }
  if (vizVisible) requestAnimationFrame(drawAll);
}
$$('.tab').forEach(b => b.addEventListener('click', () => showTab(b.dataset.tab)));

$('cbTheme').value = Theme.pref;
$('cbTheme').addEventListener('change', () => Theme.set($('cbTheme').value));
Theme.onChange(() => { rebuildLegend(); drawAll(); });

function refreshSliderLabels() {
  for (const el of $$('input[type=range][data-show]'))
    $(el.dataset.show).textContent = Number(el.value).toFixed(+el.dataset.fixed || 0);
}
document.addEventListener('input', e => { if (e.target.matches('input[type=range][data-show]')) refreshSliderLabels(); });

function refreshOutputPanels() {
  for (const sel of $$('select[data-toggle-out]')) {
    const g = sel.dataset.toggleOut, v = sel.value;
    $$(`.out-file[data-out="${g}"]`).forEach(p => p.classList.toggle('hidden', v !== 'File'));
    $$(`.out-stream[data-out="${g}"]`).forEach(p => p.classList.toggle('hidden', v !== 'TCP' && v !== 'UDP'));
  }
}
$$('select[data-toggle-out]').forEach(s => s.addEventListener('change', refreshOutputPanels));

function refreshSourcePanels() {
  const src = radio('eegSource');
  $('panelReplay').classList.toggle('hidden', src !== 'replay');
  $('panelLslInlet').classList.toggle('hidden', src !== 'lsl');
  if (!state.eeg.running) $('txtSourceMode').textContent = src === 'replay' ? 'REPLAY' : src === 'lsl' ? 'LSL IA' : 'GÉNÉRATION';
  const et = radio('etSource');
  $('panelEtReplay').classList.toggle('hidden', et !== 'replay');
  $('panelEtGenerate').classList.toggle('hidden', et === 'replay');
  $('panelEtGenerateOptions').classList.toggle('hidden', et === 'replay');
}
$$('input[name=eegSource], input[name=etSource]').forEach(r => r.addEventListener('change', refreshSourcePanels));

$('cbFtMode').addEventListener('change', () => { if (!state.ft.running) $('txtFtModeLabel').textContent = $('cbFtMode').value === 'Emotion' ? 'ÉMO' : 'FACE'; });

// Boutons « … » des fichiers de sortie
$$('[data-save-for]').forEach(btn => btn.addEventListener('click', async () => {
  const target = $(btn.dataset.saveFor);
  const res = await FileDialog.open({
    title: 'Fichier de sortie', mode: 'save', start: target.value || META.dataDir,
    name: btn.dataset.name, filters: [{ label: 'CSV (*.csv)', exts: ['.csv'] }, { label: 'Binaire (*.bin)', exts: ['.bin'] }, { label: 'BDF (*.bdf)', exts: ['.bdf'] }, { label: 'Tous', exts: [] }],
  });
  if (res) { target.value = res[0]; saveForm(); }
}));

// ═══════════════════════════════════════════════════════════════════════════
// Canaux affichés
// ═══════════════════════════════════════════════════════════════════════════

function configuredChannels() {
  return state.eeg.running ? state.eeg.channels : parseInt($('cbChannels').value, 10) || 8;
}
function refreshDisplaySlider() {
  const sl = $('slDisplayChannels');
  sl.max = Math.max(1, Math.min(64, configuredChannels()));
  if (+sl.value > +sl.max) sl.value = sl.max;
  const v = +sl.value;
  $('txtDisplayChannels').textContent = `${v} canal${v > 1 ? 'x' : ''} affiché${v > 1 ? 's' : ''}`;
}
$('slDisplayChannels').addEventListener('input', () => { refreshDisplaySlider(); });
$('slDisplayChannels').addEventListener('change', () => {
  api('/api/eeg/display', { channels: +$('slDisplayChannels').value });
  if (!state.eeg.running) previewScope();
});
$('cbChannels').addEventListener('change', () => {
  refreshDisplaySlider();
  api('/api/eeg/display', { channels: +$('slDisplayChannels').value });
  if (!state.eeg.running) previewScope();
});
$('txtSampleRate').addEventListener('change', () => { if (!state.eeg.running) previewScope(); });

// ═══════════════════════════════════════════════════════════════════════════
// Zones cérébrales
// ═══════════════════════════════════════════════════════════════════════════

function buildZoneSliders() {
  const host = $('zoneSliders');
  host.innerHTML = '';
  META.zones.forEach((z, i) => {
    const row = document.createElement('div');
    row.className = 'zone';
    row.innerHTML = `<span class="name"><i style="background:var(--S${i + 1})"></i>${z.label === 'Fronto-Central' ? 'FC' : z.label}</span>
      <input type="range" min="0" max="1" step="0.01" value="${z.activation}" data-zone="${i}" aria-label="${z.label}">
      <span class="v">${z.activation.toFixed(2)}</span>`;
    host.appendChild(row);
  });
}
let zoneTimer = 0;
$('zoneSliders').addEventListener('input', e => {
  if (!e.target.dataset.zone) return;
  e.target.nextElementSibling.textContent = (+e.target.value).toFixed(2);
  clearTimeout(zoneTimer);
  zoneTimer = setTimeout(() => api('/api/zones', { activation: $$('#zoneSliders input').map(s => +s.value) }), 40);
});
function setZoneSliders(act) {
  $$('#zoneSliders input').forEach((s, i) => { if (act[i] != null) { s.value = act[i]; s.nextElementSibling.textContent = (+act[i]).toFixed(2); } });
}
$('btnApplyPreset').addEventListener('click', async () => {
  const r = await api('/api/zones/preset', { preset: $('cbBrainPreset').value });
  if (r.ok) setZoneSliders(r.activation); else toast(r.error);
});
$('btnOpenBrain3D').addEventListener('click', () => {
  const w = window.open('brain3d.html', 'biosynth-brain3d', 'width=1000,height=800');
  if (!w) toast('Le navigateur a bloqué la fenêtre Cerveau 3D : autoriser les fenêtres pour localhost.');
  else w.focus();
});
$('btnOpenTopoMap').addEventListener('click', () => {
  const w = window.open('topomap.html', 'biosynth-topomap', 'width=560,height=640');
  if (!w) toast('Le navigateur a bloqué la fenêtre TopoMap : autoriser les fenêtres pour localhost.');
  else w.focus();
});

// ═══════════════════════════════════════════════════════════════════════════
// Replay EEG / ET, LSL inlet
// ═══════════════════════════════════════════════════════════════════════════

const REPLAY_FILTERS = [
  { label: 'Enregistrements (*.csv;*.bin;*.xlsx;*.vhdr;*.dat;*.eeg)', exts: ['.csv', '.bin', '.xlsx', '.vhdr', '.dat', '.eeg'] },
  { label: 'BrainVision (*.vhdr;*.dat;*.eeg)', exts: ['.vhdr', '.dat', '.eeg'] },
  { label: 'CSV (*.csv)', exts: ['.csv'] }, { label: 'Excel (*.xlsx)', exts: ['.xlsx'] },
  { label: 'Binaire (*.bin)', exts: ['.bin'] }, { label: 'Tous', exts: [] },
];
const ET_FILTERS = [
  { label: 'Enregistrements (*.csv;*.xlsx;*.tsv;*.txt)', exts: ['.csv', '.xlsx', '.tsv', '.txt'] }, { label: 'Tous', exts: [] },
];

async function inspectInto(kind, paths, infoEl) {
  if (!paths.length) return;
  infoEl.textContent = 'Inspection...'; infoEl.className = 'info';
  const r = await api(`/api/${kind}/inspect`, { paths });
  const ok = !!r.ok;
  infoEl.textContent = (ok ? '✓ ' : '✗ ') + (r.info || r.error || '');
  infoEl.className = 'info ' + (ok ? 'ok' : 'err');
}

$('btnBrowseReplay').addEventListener('click', async () => {
  const files = await FileDialog.open({ title: 'Sélectionner un ou plusieurs enregistrements (même type)', mode: 'open', multi: true,
    start: splitPaths($('txtReplayPath').value)[0] || META.dataDir, filters: REPLAY_FILTERS });
  if (!files) return;
  files.sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
  $('txtReplayPath').value = files.join(';'); saveForm();
  inspectInto('eeg', files, $('txtReplayInfo'));
});
$('txtReplayPath').addEventListener('change', () => inspectInto('eeg', splitPaths($('txtReplayPath').value), $('txtReplayInfo')));

$('btnBrowseEtReplay').addEventListener('click', async () => {
  const files = await FileDialog.open({ title: 'Sélectionner un ou plusieurs enregistrements oculométriques', mode: 'open', multi: true,
    start: splitPaths($('txtEtReplayPath').value)[0] || META.dataDir, filters: ET_FILTERS });
  if (!files) return;
  files.sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
  $('txtEtReplayPath').value = files.join(';'); saveForm();
  inspectInto('et', files, $('txtEtReplayInfo'));
});
$('txtEtReplayPath').addEventListener('change', () => inspectInto('et', splitPaths($('txtEtReplayPath').value), $('txtEtReplayInfo')));

$('cbReplaySpeed').addEventListener('change', () => api('/api/eeg/speed', { speed: +$('cbReplaySpeed').value }));
$('cbEtReplaySpeed').addEventListener('change', () => api('/api/et/speed', { speed: +$('cbEtReplaySpeed').value }));
$('btnReplayPause').addEventListener('click', async () => { const r = await api('/api/eeg/pause', {}); if (!r.ok) toast(r.error); });
$('btnEtReplayPause').addEventListener('click', async () => { const r = await api('/api/et/pause', {}); if (!r.ok) toast(r.error); });

let lslStreams = [];
$('btnDiscoverLsl').addEventListener('click', async () => {
  const btn = $('btnDiscoverLsl');
  btn.disabled = true;
  $('lstLslStreams').innerHTML = '';
  $('txtLslInletInfo').textContent = 'Recherche en cours...';
  const r = await api('/api/lsl/discover', {});
  btn.disabled = false;
  if (!r.ok) { $('txtLslInletInfo').textContent = r.error; toast(r.error); return; }
  lslStreams = r.streams;
  for (const s of r.streams) {
    const o = document.createElement('option');
    o.textContent = s.label; $('lstLslStreams').appendChild(o);
  }
  if (r.streams.length === 1) $('lstLslStreams').selectedIndex = 0;
  showSelectedStream();
});
function showSelectedStream() {
  const s = lslStreams[$('lstLslStreams').selectedIndex];
  if (s) $('txtLslInletInfo').textContent = `✓ ${s.name} — ${s.channels} canaux · ${s.sampleRate} Hz · Type: ${s.type}`;
}
$('lstLslStreams').addEventListener('change', showSelectedStream);

// LSL : activation à chaud
for (const [chk, name, modality] of [['chkLslEeg', 'txtLslNameEeg', 'eeg'], ['chkLslEt', 'txtLslNameEt', 'et'], ['chkLslFt', 'txtLslNameFt', 'ft']]) {
  $(chk).addEventListener('change', () => {
    if ($(chk).checked && !META.lslAvailable) toast('liblsl introuvable : LSL désactivé.\n' + META.lslHint);
    api('/api/lsl/toggle', { modality, enabled: $(chk).checked, name: $(name).value });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Démarrer / arrêter
// ═══════════════════════════════════════════════════════════════════════════

$('btnStartEeg').addEventListener('click', async () => {
  const btn = $('btnStartEeg'); btn.disabled = true;
  let r;
  if (state.eeg.running) r = await api('/api/eeg/stop', {});
  else {
    r = await api('/api/eeg/start', {
      source: radio('eegSource'),
      channels: parseInt($('cbChannels').value, 10),
      sampleRate: num('txtSampleRate', 256),
      output: $('cbOutputMode').value, format: $('cbFormat').value,
      filePath: $('txtFilePath').value, host: $('txtHost').value, port: num('txtPort', 9999),
      artifacts: $('chkArtifacts').checked, noise: num('slNoise', 0.5),
      replayPaths: splitPaths($('txtReplayPath').value), speed: +$('cbReplaySpeed').value, loop: $('chkReplayLoop').checked,
      lslStreamIndex: $('lstLslStreams').selectedIndex,
      lsl: { enabled: $('chkLslEeg').checked, name: $('txtLslNameEeg').value },
    });
    if (r.ok) { scope.epoch = -1; if (r.info && radio('eegSource') === 'replay') { $('txtReplayInfo').textContent = '✓ ' + r.info; $('txtReplayInfo').className = 'info ok'; } }
  }
  btn.disabled = false;
  if (!r.ok) toast(r.error);
});

$('btnStartEt').addEventListener('click', async () => {
  const btn = $('btnStartEt'); btn.disabled = true;
  let r;
  if (state.et.running) r = await api('/api/et/stop', {});
  else r = await api('/api/et/start', {
    source: radio('etSource'),
    sampleRate: parseInt($('cbEtSampleRate').value, 10),
    screenW: num('txtScreenW', 1920), screenH: num('txtScreenH', 1080),
    output: $('cbEtOutputMode').value, filePath: $('txtEtFilePath').value,
    host: $('txtEtHost').value, port: num('txtEtPort', 9998),
    saccades: $('chkEtSaccades').checked, blinks: $('chkEtBlinks').checked, pupil: $('chkEtPupil').checked,
    noise: num('slEtNoise', 0.3),
    replayPaths: splitPaths($('txtEtReplayPath').value), speed: +$('cbEtReplaySpeed').value,
    loop: $('chkEtReplayLoop').checked, rawColumns: $('chkEtReplayRaw').checked,
    lsl: { enabled: $('chkLslEt').checked, name: $('txtLslNameEt').value },
  });
  btn.disabled = false;
  if (!r.ok) toast(r.error);
});

$('btnStartFt').addEventListener('click', async () => {
  const btn = $('btnStartFt'); btn.disabled = true;
  let r;
  if (state.ft.running) r = await api('/api/ft/stop', {});
  else r = await api('/api/ft/start', {
    mode: $('cbFtMode').value, sampleRate: parseInt($('cbFtSampleRate').value, 10),
    output: $('cbFtOutputMode').value, filePath: $('txtFtFilePath').value,
    host: $('txtFtHost').value, port: num('txtFtPort', 9997),
    headMove: $('chkFtHeadMove').checked, micro: $('chkFtMicro').checked,
    emotionSec: num('slFtEmoDur', 4), noise: num('slFtNoise', 0.2),
    lsl: { enabled: $('chkLslFt').checked, name: $('txtLslNameFt').value },
  });
  btn.disabled = false;
  if (!r.ok) toast(r.error);
});

const EEG_LOCK = ['cbChannels', 'txtSampleRate', 'cbOutputMode', 'cbFormat', 'txtFilePath', 'txtHost', 'txtPort', 'slNoise', 'chkArtifacts', 'txtReplayPath', 'btnBrowseReplay', 'btnDiscoverLsl', 'lstLslStreams', 'txtLslNameEeg'];
const ET_LOCK = ['cbEtSampleRate', 'cbEtOutputMode', 'txtScreenW', 'txtScreenH', 'slEtNoise', 'chkEtSaccades', 'chkEtBlinks', 'chkEtPupil', 'txtEtHost', 'txtEtPort', 'txtEtFilePath', 'txtEtReplayPath', 'btnBrowseEtReplay', 'chkEtReplayLoop', 'chkEtReplayRaw', 'txtLslNameEt'];
const FT_LOCK = ['cbFtMode', 'cbFtSampleRate', 'cbFtOutputMode', 'slFtNoise', 'slFtEmoDur', 'chkFtMicro', 'chkFtHeadMove', 'txtFtHost', 'txtFtPort', 'txtFtFilePath', 'txtLslNameFt'];
function lock(ids, running, radios) {
  ids.forEach(id => { if ($(id)) $(id).disabled = running; });
  $$(`input[name=${radios}]`).forEach(r => r.disabled = running);
  $$('[data-save-for]').forEach(b => { if (ids.includes(b.dataset.saveFor)) b.disabled = running; });
}

// ═══════════════════════════════════════════════════════════════════════════
// État reçu du serveur
// ═══════════════════════════════════════════════════════════════════════════

const state = {
  eeg: { running: false, channels: 8, rate: 256, mode: 'generate' },
  et: { running: false }, ft: { running: false },
};

// ── Oscilloscope ───────────────────────────────────────────────────────────
const BUFFER_SIZE = 512;
// Palette catégorielle validée (S1..S8, ordre fixe) : couleur liée au canal, jamais réattribuée
const CHAN_COLORS = ['S1', 'S2', 'S3', 'S4', 'S5', 'S6', 'S7', 'S8'];
const scope = { epoch: -1, labels: [], bufs: [], head: 0, canvases: [] };

function buildScope(labels) {
  const host = $('eegPlots');
  host.innerHTML = '';
  scope.labels = labels.slice();
  scope.bufs = labels.map(() => new Float32Array(BUFFER_SIZE));
  scope.head = 0;
  scope.canvases = labels.map((name, i) => {
    const div = document.createElement('div');
    div.className = 'chan';
    const cv = document.createElement('canvas');
    const lbl = document.createElement('span');
    lbl.className = 'name'; lbl.innerHTML = `<i style="background:var(--${CHAN_COLORS[i % CHAN_COLORS.length]})"></i>`; lbl.append(name);
    div.append(cv, lbl);
    host.appendChild(div);
    return cv;
  });
}
function previewScope() {
  const n = Math.min(+$('slDisplayChannels').value, configuredChannels());
  const names = META.channelNames[String(configuredChannels())] || [];
  buildScope(Array.from({ length: n }, (_, i) => names[i] || `Ch${i + 1}`));
  $('txtChannelInfo').textContent = `${configuredChannels()}CH·${num('txtSampleRate', 256)}Hz`;
}
function drawScope() {
  for (let i = 0; i < scope.canvases.length; i++) {
    const { ctx, w, h } = fitCanvas(scope.canvases[i]);
    ctx.clearRect(0, 0, w, h);
    const mid = h / 2, scale = h / 600, step = w / (BUFFER_SIZE - 1);
    ctx.strokeStyle = Theme.color('Border'); ctx.lineWidth = 0.5;
    ctx.beginPath(); ctx.moveTo(0, mid); ctx.lineTo(w, mid); ctx.stroke();
    ctx.strokeStyle = Theme.color(CHAN_COLORS[i % CHAN_COLORS.length]); ctx.lineWidth = 1.2; ctx.lineJoin = 'round';
    ctx.beginPath();
    const buf = scope.bufs[i];
    for (let j = 0; j < BUFFER_SIZE; j++) {
      const v = buf[(scope.head + j) % BUFFER_SIZE];
      const y = Math.min(h, Math.max(0, mid - v * scale));
      j ? ctx.lineTo(j * step, y) : ctx.moveTo(0, y);
    }
    ctx.stroke();
  }
}
function updateBands() {
  // Même indicateur que l'ancienne version WPF : RMS du premier canal réparti par coefficients de bande
  const BANDS = [['δ DELTA 0.5-4Hz', 0.35, 'S7'], ['θ THETA 4-8Hz', 0.20, 'S1'], ['α ALPHA 8-13Hz', 0.30, 'S3'], ['β BETA 13-30Hz', 0.10, 'S4'], ['γ GAMMA 30-80Hz', 0.05, 'S8']];
  const host = $('bands');
  if (!host.children.length) {
    host.innerHTML = BANDS.map(([l, , c]) => `<div class="bar-row"><span>${l}</span><div class="bar"><div style="background:var(--${c})"></div></div></div>`).join('');
  }
  if (!scope.bufs.length) return;
  let s2 = 0; for (const v of scope.bufs[0]) s2 += v * v;
  const rms = Math.sqrt(s2 / BUFFER_SIZE);
  if (rms < 1) return;
  Array.from(host.querySelectorAll('.bar > div')).forEach((d, i) => d.style.width = Math.min(100, rms * BANDS[i][1]) + '%');
}

// ── Eye tracking ───────────────────────────────────────────────────────────
const ET_BUFFER = 256;
const RAW_COLORS = ['S1', 'S2', 'S3', 'S4', 'S5', 'S6', 'S7', 'S8'];
const et = { epoch: -1, pl: new Float32Array(ET_BUFFER), pr: new Float32Array(ET_BUFFER), head: 0, rawMode: false, rawNames: [], raw: [], rawHead: 0, trail: [[.5, .5], [.5, .5], [.5, .5]], lastEvt: 'fixation' };

function resetEt(f) {
  et.epoch = f.epoch; et.pl.fill(0); et.pr.fill(0); et.head = 0;
  et.rawMode = false; et.rawNames = []; et.raw = []; et.rawHead = 0;
}
function setRawMode(names) {
  et.rawMode = names.length > 0;
  et.rawNames = names.slice();
  et.raw = names.map(() => new Float32Array(ET_BUFFER));
  et.rawHead = 0;
  $('txtPupilTitle').textContent = et.rawMode ? `COLONNES DU FICHIER (${names.length})` : 'DIAMÈTRE PUPILLAIRE';
  $('lblGazeX').textContent = et.rawMode ? (names[0] || '—').toUpperCase() : 'GAZE X';
  $('lblGazeY').textContent = et.rawMode ? (names[1] || '—').toUpperCase() : 'GAZE Y';
  $('lblPupilL').textContent = et.rawMode ? (names[2] || '—').toUpperCase() : 'PUPILLE G (mm)';
  $('lblPupilR').textContent = et.rawMode ? (names[3] || '—').toUpperCase() : 'PUPILLE D (mm)';
  rebuildLegend();
}
function rebuildLegend() {
  const items = et.rawMode ? et.rawNames.map((n, i) => [n, RAW_COLORS[i % RAW_COLORS.length]]) : [['Gauche', 'S1'], ['Droite', 'S2']];
  $('pupilLegend').innerHTML = items.map(([n, c]) => `<span><i style="background:var(--${c})"></i>${n}</span>`).join('');
}
function drawGaze() {
  const { ctx, w, h } = fitCanvas($('gazeMap'));
  ctx.clearRect(0, 0, w, h);
  ctx.strokeStyle = Theme.color('Border'); ctx.lineWidth = 0.5; ctx.setLineDash([3, 4]);
  ctx.beginPath(); ctx.moveTo(w / 2, 0); ctx.lineTo(w / 2, h); ctx.moveTo(0, h / 2); ctx.lineTo(w, h / 2); ctx.stroke();
  ctx.setLineDash([]);
  const col = { blink: 'S5', saccade: 'S4', microsaccade: 'S2' }[et.lastEvt] || 'S1';
  const sizes = [[12, 1], [8, 0.45], [6, 0.25]];
  for (let k = 2; k >= 0; k--) {
    const [x, y] = et.trail[k];
    ctx.globalAlpha = sizes[k][1];
    ctx.fillStyle = Theme.color(k === 0 ? col : 'S1');
    ctx.beginPath(); ctx.arc(x * w, y * h, sizes[k][0] / 2, 0, Math.PI * 2); ctx.fill();
  }
  ctx.globalAlpha = 1;
}
function drawPupil() {
  const { ctx, w, h } = fitCanvas($('pupilPlot'));
  ctx.clearRect(0, 0, w, h);
  const step = w / (ET_BUFFER - 1);
  const line = (buf, head, color, toY) => {
    ctx.strokeStyle = Theme.color(color); ctx.lineWidth = 1.2; ctx.lineJoin = 'round';
    ctx.beginPath();
    for (let j = 0; j < ET_BUFFER; j++) {
      const y = Math.min(h, Math.max(0, toY(buf[(head + j) % ET_BUFFER]) * h));
      j ? ctx.lineTo(j * step, y) : ctx.moveTo(0, y);
    }
    ctx.stroke();
  };
  if (et.rawMode) {
    let min = Infinity, max = -Infinity;
    for (const b of et.raw) for (const v of b) { if (v < min) min = v; if (v > max) max = v; }
    if (!(max - min > 1e-9)) max = min + 1;
    const pad = (max - min) * 0.05; min -= pad; max += pad;
    et.raw.forEach((b, c) => line(b, et.rawHead, RAW_COLORS[c % RAW_COLORS.length], v => 1 - (v - min) / (max - min)));
  } else {
    const PMIN = 1.5, PMAX = 6.5, toY = v => 1 - (v - PMIN) / (PMAX - PMIN);
    line(et.pl, et.head, 'S1', toY);
    line(et.pr, et.head, 'S2', toY);
  }
}

// ── Face tracking ──────────────────────────────────────────────────────────
const EMO_NAMES = ['NEUTRAL', 'HAPPY', 'SAD', 'ANGRY', 'SURPRISED', 'FEARFUL', 'DISGUSTED'];
// neutre violet, joie jaune, tristesse bleu, colère rouge, surprise orange, peur magenta, dégoût vert
const EMO_COLORS = ['S7', 'S4', 'S1', 'S8', 'S2', 'S5', 'S6'];
const ft = { face: null };
function buildEmoBars() {
  $('emoBars').innerHTML = EMO_NAMES.map((n, i) =>
    `<div class="bar-row"><span>${n}</span><div class="bar"><div style="background:var(--${EMO_COLORS[i]});width:${i ? 0 : 100}%"></div></div></div>`).join('');
}
function drawFace() {
  const { ctx, w, h } = fitCanvas($('faceCanvas'));
  ctx.clearRect(0, 0, w, h);
  const f = ft.face;
  if (!f) return;
  const region = i => i <= 16 ? 'TextDim' : i <= 26 ? 'S4' : i <= 35 ? 'S1' : i <= 47 ? 'S3' : 'S5';
  ctx.globalAlpha = 0.85;
  for (let i = 0; i < 68; i++) {
    ctx.fillStyle = Theme.color(region(i));
    ctx.beginPath(); ctx.arc(f.lx[i] * w, f.ly[i] * h, 1.6, 0, Math.PI * 2); ctx.fill();
  }
  ctx.globalAlpha = 1;
}

// ── Application d'une trame ────────────────────────────────────────────────
let lastFrameAt = 0;
function applyFrame(f) {
  lastFrameAt = performance.now();
  const E = f.eeg, T = f.et, F = f.ft;

  // EEG
  const eegWas = state.eeg.running;
  Object.assign(state.eeg, { running: E.running, channels: E.channels, rate: E.rate, mode: E.mode });
  if (E.running && (E.epoch !== scope.epoch || E.labels.length !== scope.labels.length || E.labels.some((l, i) => l !== scope.labels[i]))) {
    scope.epoch = E.epoch;
    buildScope(E.labels);
  }
  if (E.running || E.data[0]?.length) {
    const n = Math.min(E.data.length, scope.bufs.length);
    const len = E.data[0]?.length || 0;
    for (let k = 0; k < len; k++) {
      for (let c = 0; c < n; c++) scope.bufs[c][scope.head] = E.data[c][k];
      scope.head = (scope.head + 1) % BUFFER_SIZE;
    }
  }
  if (eegWas !== E.running) { refreshDisplaySlider(); refreshSourcePanels(); }
  const startEeg = $('btnStartEeg');
  startEeg.classList.toggle('running', E.running);
  startEeg.textContent = E.running ? (E.mode === 'generate' ? '■  ARRÊTER EEG' : '■  ARRÊTER') : '▶  DÉMARRER EEG';
  lock(EEG_LOCK, E.running, 'eegSource');
  $('txtStatus').textContent = E.status;
  $('txtSampleCount').textContent = fmtN(E.samples);
  $('txtDuration').textContent = `${(+E.duration).toFixed(1)}s`;
  if (E.running) $('txtSourceMode').textContent = E.mode === 'replay' ? 'REPLAY' : E.mode === 'lsl' ? 'LSL IA' : 'GÉNÉRATION';
  if (E.running) $('txtChannelInfo').textContent = `${E.channels}CH·${E.rate}Hz`;
  for (const id of ['recEeg', 'recEegHdr']) $(id).textContent = E.rec.replace(/^● /, '');
  const replayOn = !!E.replay;
  $('btnReplayPause').classList.toggle('hidden', !replayOn);
  if (replayOn) {
    $('pbReplay').style.width = E.replay.pct + '%';
    $('txtReplayProgress').textContent = `${E.replay.pct.toFixed(1)}%`;
    $('txtReplayPct').textContent = `${E.replay.pct.toFixed(1)}% · ${fmtN(E.replay.frame)}`;
    $('btnReplayPause').textContent = E.replay.paused ? '▶' : '⏸';
  }
  $('txtLslInletStatus').textContent = E.inlet.status;

  // ET
  if (T.epoch !== et.epoch) resetEt(T);
  if (T.rawMode !== et.rawMode || T.rawNames.join('\u0001') !== et.rawNames.join('\u0001')) setRawMode(T.rawMode ? T.rawNames : []);
  for (let k = 0; k < T.pupilL.length; k++) { et.pl[et.head] = T.pupilL[k]; et.pr[et.head] = T.pupilR[k]; et.head = (et.head + 1) % ET_BUFFER; }
  for (const row of T.raw) { row.forEach((v, c) => { if (et.raw[c]) et.raw[c][et.rawHead] = v; }); et.rawHead = (et.rawHead + 1) % ET_BUFFER; }
  et.trail = T.trail; et.lastEvt = T.lastEvt;
  state.et.running = T.running;
  const startEt = $('btnStartEt');
  startEt.classList.toggle('running', T.running);
  startEt.textContent = T.running ? '■  ARRÊTER ET' : '▶  DÉMARRER ET';
  lock(ET_LOCK, T.running, 'etSource');
  $('txtEtStatus').textContent = T.status;
  $('txtEtSampleCount').textContent = fmtN(T.samples);
  $('txtEtEvent').textContent = T.text.evt;
  for (const id of ['recEt', 'recEtHdr']) $(id).textContent = T.rec.replace(/^● /, '');
  $('txtGazeX').textContent = T.text.gx; $('txtGazeY').textContent = T.text.gy;
  $('txtPupilL').textContent = T.text.pl; $('txtPupilR').textContent = T.text.pr;
  $('txtEtVelocity').textContent = T.text.vel;
  const det = $('txtEtEventDetail');
  det.textContent = T.text.detail;
  det.style.color = '';
  $('txtFixationCount').textContent = fmtN(T.stats.fix);
  $('txtSaccadeCount').textContent = fmtN(T.stats.sac);
  $('txtBlinkCount').textContent = fmtN(T.stats.blink);
  $('txtAvgVelocity').textContent = T.stats.avgVel;
  $('btnEtReplayPause').classList.toggle('hidden', !T.replay);
  if (T.replay) {
    $('pbEtReplay').style.width = T.replay.pct + '%';
    $('txtEtReplayProgress').textContent = `${T.replay.pct.toFixed(1)}%`;
    $('btnEtReplayPause').textContent = T.replay.paused ? '▶' : '⏸';
  }

  // FT
  state.ft.running = F.running;
  const startFt = $('btnStartFt');
  startFt.classList.toggle('running', F.running);
  startFt.textContent = F.running ? '■  ARRÊTER FT' : '▶  DÉMARRER FT';
  lock(FT_LOCK, F.running, '__none');
  $('txtFtStatus').textContent = F.status;
  $('txtFtSampleCount').textContent = fmtN(F.samples);
  if (F.running) $('txtFtModeLabel').textContent = F.mode;
  for (const id of ['recFt', 'recFtHdr']) $(id).textContent = F.rec.replace(/^● /, '');
  if (F.emo) {
    const c = `var(--${EMO_COLORS[F.emo.dom]})`;
    $('txtFtEmoLabel').textContent = EMO_NAMES[F.emo.dom];
    $('ftEmoBadge').style.borderColor = c;
    $('txtFtArousal').textContent = F.emo.arousal.toFixed(2);
    $('txtFtValence').textContent = (F.emo.valence >= 0 ? '+' : '') + F.emo.valence.toFixed(2);
    $('txtFtDomEmo').textContent = EMO_NAMES[F.emo.dom];
    Array.from($('emoBars').querySelectorAll('.bar > div')).forEach((d, i) => d.style.width = (F.emo.scores[i] * 100) + '%');
  }
  if (F.face) {
    ft.face = F.face;
    $('txtFtPitch').textContent = `${F.face.pitch.toFixed(1)}°`;
    $('txtFtYaw').textContent = `${F.face.yaw.toFixed(1)}°`;
    $('txtFtRoll').textContent = `${F.face.roll.toFixed(1)}°`;
    $('txtFtZ').textContent = `${F.face.z}mm`;
    $('txtFtConf').textContent = `${Math.round(F.face.conf * 100)} %`;
    $('txtFtMicro').textContent = F.face.micro ? 'OUI' : 'NON';
  }
  if (F.stats.avgArousal != null) {
    $('txtFtAvgArousal').textContent = F.stats.avgArousal.toFixed(2);
    $('txtFtAvgValence').textContent = (F.stats.avgValence >= 0 ? '+' : '') + F.stats.avgValence.toFixed(2);
    $('txtFtTransCount').textContent = fmtN(F.stats.trans);
  }

  // Statut global
  const yn = (id, on) => { $(id).textContent = on ? 'OUI' : 'NON'; $(id).style.color = `var(--${on ? 'Good' : 'TextMuted'})`; };
  yn('txtEegActive', E.running);
  yn('txtEtActive', T.running);
  yn('txtFtActive', F.running);
  yn('txtReplayActive', replayOn || !!T.replay);
  const nOn = [E.running, T.running, F.running].filter(Boolean).length;
  $('txtSync').textContent = nOn === 3 ? 'SYNC3' : nOn === 2 ? 'SYNC2' : '—';
  $('txtSync').style.color = `var(--${nOn >= 2 ? 'BrandStrong' : 'TextMuted'})`;

  // LSL
  for (const el of $$('[data-lsl-status]')) {
    el.textContent = f.lsl.text;
    el.classList.toggle('on', f.lsl.active.length > 0);
    el.classList.toggle('warn', !f.lsl.available);
  }
}

// ── Boucle de rendu ────────────────────────────────────────────────────────
function drawAll() {
  if (!vizVisible) return;
  drawScope(); drawGaze(); drawPupil(); drawFace(); updateBands();
}
function blink() {
  const half = (Date.now() % 1000) < 500;
  const set = (ids, on) => ids.forEach(id => $(id).style.opacity = on ? (half ? 1 : 0.15) : 0);
  set(['dotEeg', 'dotEeg2'], state.eeg.running);
  set(['dotEt', 'dotEt2'], state.et.running);
  set(['dotFt', 'dotFt2'], state.ft.running);
}
function loop() {
  drawAll(); blink();
  requestAnimationFrame(loop);
}

// ── WebSocket ──────────────────────────────────────────────────────────────
function connect() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`);
  ws.onopen = () => { $('connState').textContent = '● connecté'; $('connState').className = 'conn ok'; };
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.type === 'frame') applyFrame(m.frame);
  };
  ws.onclose = () => {
    $('connState').textContent = '● serveur déconnecté, nouvelle tentative…'; $('connState').className = 'conn down';
    setTimeout(connect, 1500);
  };
}

// ═══════════════════════════════════════════════════════════════════════════
// Sélecteur de fichiers
// ═══════════════════════════════════════════════════════════════════════════

const FileDialog = (() => {
  const dlg = $('fileDialog');
  let opts = null, cur = null, selected = new Set(), resolveFn = null, lastClick = -1;

  function extOk(name) {
    const f = opts.filters[$('fdFilter').selectedIndex];
    if (!f || !f.exts.length) return true;
    const n = name.toLowerCase();
    return f.exts.some(e => n.endsWith(e));
  }
  function join(dir, name) { return dir.endsWith(cur.sep) ? dir + name : dir + cur.sep + name; }

  async function nav(path) {
    const r = await api('/api/fs?path=' + encodeURIComponent(path || ''));
    if (!r.ok) { toast(r.error); return; }
    cur = r; selected.clear(); lastClick = -1;
    $('fdPath').value = r.path;
    $('fdPlaces').innerHTML = '';
    for (const p of r.places) {
      const b = document.createElement('button'); b.type = 'button'; b.textContent = p.label; b.title = p.path;
      b.onclick = () => nav(p.path); $('fdPlaces').appendChild(b);
    }
    render();
  }
  function render() {
    const ul = $('fdList'); ul.innerHTML = '';
    for (const d of cur.dirs) {
      const li = document.createElement('li');
      li.innerHTML = `<span>📁</span><span></span><span class="sz"></span>`;
      li.children[1].textContent = d;
      li.ondblclick = () => nav(join(cur.path, d));
      li.onclick = () => { if (opts.mode === 'save') return; };
      ul.appendChild(li);
    }
    const files = cur.files.filter(f => extOk(f.name));
    files.forEach((f, idx) => {
      const li = document.createElement('li');
      li.innerHTML = `<span>📄</span><span></span><span class="sz">${fmtSize(f.size)}</span>`;
      li.children[1].textContent = f.name;
      if (selected.has(f.name)) li.classList.add('sel');
      li.onclick = e => {
        if (opts.multi && e.shiftKey && lastClick >= 0) {
          const [a, b] = [Math.min(lastClick, idx), Math.max(lastClick, idx)];
          for (let k = a; k <= b; k++) selected.add(files[k].name);
        } else if (opts.multi && (e.ctrlKey || e.metaKey)) {
          selected.has(f.name) ? selected.delete(f.name) : selected.add(f.name);
        } else { selected.clear(); selected.add(f.name); }
        lastClick = idx;
        $('fdName').value = [...selected].join('; ');
        render();
      };
      li.ondblclick = () => { selected.clear(); selected.add(f.name); confirm(); };
      ul.appendChild(li);
    });
    if (!cur.dirs.length && !files.length) ul.innerHTML = '<li class="dim"><span></span><span>(dossier vide)</span><span></span></li>';
  }
  function fmtSize(n) { return n < 1024 ? n + ' o' : n < 1048576 ? (n / 1024).toFixed(1) + ' ko' : (n / 1048576).toFixed(1) + ' Mo'; }

  function confirm() {
    let out;
    if (opts.mode === 'save') {
      const name = $('fdName').value.trim();
      if (!name) { toast('Indiquer un nom de fichier.'); return; }
      out = [/^([a-zA-Z]:)?[\\/]/.test(name) ? name : join(cur.path, name)];
    } else {
      const typed = $('fdName').value.split(';').map(s => s.trim()).filter(Boolean);
      const names = selected.size ? [...selected] : typed;
      if (!names.length) { toast('Sélectionner au moins un fichier.'); return; }
      out = names.map(n => /^([a-zA-Z]:)?[\\/]/.test(n) ? n : join(cur.path, n));
    }
    const fn = resolveFn; resolveFn = null;
    dlg.close();
    fn && fn(out);
  }

  $('fdUp').onclick = () => cur?.parent && nav(cur.parent);
  $('fdPath').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); nav($('fdPath').value); } });
  $('fdName').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); confirm(); } });
  $('fdFilter').onchange = () => cur && render();
  $('fdOk').onclick = confirm;
  dlg.addEventListener('close', () => { if (resolveFn) { const fn = resolveFn; resolveFn = null; fn(null); } });

  return {
    open(o) {
      opts = Object.assign({ mode: 'open', multi: false, filters: [{ label: 'Tous', exts: [] }] }, o);
      $('fdTitle').textContent = opts.title || 'Sélectionner';
      $('fdOk').textContent = opts.mode === 'save' ? 'Enregistrer' : 'Ouvrir';
      $('fdName').value = opts.mode === 'save' ? (o.start && !o.start.endsWith('/') ? o.start.split(/[\\/]/).pop() : opts.name || '') : '';
      $('fdName').placeholder = opts.mode === 'save' ? 'nom du fichier' : (opts.multi ? 'Ctrl/Maj-clic pour plusieurs fichiers' : 'nom du fichier');
      $('fdFilter').innerHTML = opts.filters.map(f => `<option>${f.label}</option>`).join('');
      dlg.showModal();
      nav(opts.start || META.dataDir);
      return new Promise(res => { resolveFn = res; });
    },
  };
})();

// ═══════════════════════════════════════════════════════════════════════════
// Démarrage
// ═══════════════════════════════════════════════════════════════════════════

(async function init() {
  const meta = await api('/api/meta');
  Object.assign(META, meta);
  $('platform').textContent = meta.platform || '';
  $('footInfo').textContent = `Données : ${meta.dataDir}`;
  loadForm();
  const sep = (meta.dataDir || '').includes('\\') ? '\\' : '/';
  for (const [id, name] of [['txtFilePath', 'eeg_data.csv'], ['txtEtFilePath', 'eyetracking_data.csv'], ['txtFtFilePath', 'face_data.csv']])
    if (!$(id).value) $(id).value = meta.dataDir + sep + name;

  buildZoneSliders();
  const z = await api('/api/zones'); if (z.ok) setZoneSliders(z.activation);
  buildEmoBars(); rebuildLegend(); updateBands();
  refreshSliderLabels(); refreshOutputPanels(); refreshSourcePanels(); refreshDisplaySlider();
  api('/api/eeg/display', { channels: +$('slDisplayChannels').value });
  previewScope();
  if (!meta.lslAvailable) for (const el of $$('[data-lsl-status]')) { el.textContent = '⚠ liblsl introuvable : voir README_LSL.txt'; el.classList.add('warn'); }

  let tab = 'eeg'; try { tab = localStorage.getItem('biosynth.tab') || 'eeg'; } catch { /* rien */ }
  showTab(tab);
  connect();
  requestAnimationFrame(loop);
})();
