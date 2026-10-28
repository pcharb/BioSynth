// Carte topographique 2D (vue du dessus, nez en haut), port de EEGTopoMap.cs.
// Heatmap par interpolation IDW de Shepard (puissance 3) sur une grille 28 × 28,
// électrodes colorées par puissance (palette thermique), halo et pulsation.
'use strict';

const cv = document.getElementById('topo');
const HEAT_RES = 28;
// Couleurs tirées de la palette du thème courant (app.css), relues à chaque image
const C = n => Theme.color(n);
let electrodes = {};       // nom → [x, y, z]
let topo = null;           // dernière trame reçue
let pulse = 0;

// Rampe séquentielle CIRICS (Seq0 faible → Seq4 fort), propre au thème
function heatColor(t) { return Theme.ramp(t).map(Math.round); }

/**
 * Puissance normalisée : signal brut lissé (70 %) + activation de la zone (30 %), comme NormPower().
 * Le gain de l'ancienne version WPF (× 5) saturait toute la carte : la puissance lissée (≈ |x| / 200 µV)
 * vaut de 0,1 (sommeil profond) à 0,65 (activation maximale) avec le générateur. Un gain de 1,5
 * répartit ces états sur toute l'échelle de couleurs.
 */
const RAW_GAIN = 1.5;
function normPower(i) {
  if (!topo) return 0.2;
  const raw = topo.power[i] || 0;
  const zone = topo.zones[topo.region[i]] ?? 0.5;
  return Math.min(1, Math.max(0, raw * RAW_GAIN * 0.7 + zone * 0.3));
}

function positions() {
  const out = [];
  if (!topo) return out;
  topo.labels.forEach((name, i) => {
    const p = electrodes[name];
    if (p) out.push({ i, name, x: p[0], y: -p[1] });   // projection axiale : avant en haut
  });
  return out;
}

function draw() {
  const dpr = window.devicePixelRatio || 1;
  const W = cv.clientWidth, H = cv.clientHeight;
  if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
  const ctx = cv.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = C('BgCanvas'); ctx.fillRect(0, 0, W, H);
  if (W < 60 || H < 60) return;
  const cx = W / 2, cy = H / 2, R = Math.min(W, H) * 0.42;
  const pts = positions();

  // 1. Heatmap
  if (document.getElementById('chkHeatmap').checked && pts.length) {
    const step = Math.min(W, H) * 0.84 / HEAT_RES;
    const x0 = cx - HEAT_RES / 2 * step, y0 = cy - HEAT_RES / 2 * step;
    const vals = pts.map(p => normPower(p.i));
    for (let row = 0; row < HEAT_RES; row++) {
      for (let col = 0; col < HEAT_RES; col++) {
        const nx = (col + 0.5) / HEAT_RES * 2 - 1, ny = (row + 0.5) / HEAT_RES * 2 - 1;
        const r2 = nx * nx + ny * ny;
        if (r2 > 1) continue;
        let sw = 0, swv = 0, exact = -1;
        for (let k = 0; k < pts.length; k++) {
          const d2 = (nx - pts[k].x) ** 2 + (ny - pts[k].y) ** 2;
          if (d2 < 1e-6) { exact = k; break; }
          const w = 1 / Math.pow(d2, 1.5);
          sw += w; swv += w * vals[k];
        }
        const v = exact >= 0 ? vals[exact] : (sw > 0 ? swv / sw : 0);
        const [r, g, b] = heatColor(v);
        const alpha = (180 * (1 - r2) + 40) / 255;
        ctx.fillStyle = `rgba(${r},${g},${b},${alpha.toFixed(3)})`;
        ctx.fillRect(x0 + col * step, y0 + row * step, step + 1, step + 1);
      }
    }
  }

  // 2. Contour de la tête, nez, oreilles
  if (document.getElementById('chkContour').checked) {
    ctx.strokeStyle = C('BrandStrong'); ctx.lineWidth = 2;
    ctx.beginPath(); ctx.ellipse(cx, cy, R, R * 1.025, 0, 0, Math.PI * 2); ctx.stroke();
    ctx.fillStyle = C('BgCanvas'); ctx.lineJoin = 'round';
    ctx.beginPath(); ctx.moveTo(cx - R * 0.08, cy - R * 0.98); ctx.lineTo(cx, cy - R * 1.18); ctx.lineTo(cx + R * 0.08, cy - R * 0.98); ctx.closePath(); ctx.fill(); ctx.stroke();
    ctx.lineWidth = 1.8;
    for (const sx of [-1, 1]) { ctx.beginPath(); ctx.ellipse(cx + sx * R, cy + R * 0.08, R * 0.10, R * 0.18, 0, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); }
    ctx.strokeStyle = C('TextDim'); ctx.lineWidth = 1;
    const c = R * 0.04;
    ctx.beginPath(); ctx.moveTo(cx - c, cy); ctx.lineTo(cx + c, cy); ctx.moveTo(cx, cy - c); ctx.lineTo(cx, cy + c); ctx.stroke();
    ctx.fillStyle = C('TextMuted'); ctx.font = '8px monospace'; ctx.textAlign = 'center';
    ctx.fillText('AVANT', cx, cy - R * 1.22);
  }

  // 3. Électrodes
  const showLabels = document.getElementById('chkLabels').checked;
  ctx.textAlign = 'left'; ctx.font = 'bold 8px monospace';
  for (const p of pts) {
    const power = normPower(p.i);
    const px = cx + p.x * R, py = cy + p.y * R;
    const pulseK = 1 + 0.28 * Math.sin(pulse + p.i * 0.42) * power;
    const dotR = (4.5 + power * 5) * pulseK;
    const [r, g, b] = heatColor(power);
    if (power > 0.25) {
      const hr = dotR * 2.6;
      const grd = ctx.createRadialGradient(px, py, 0, px, py, hr);
      grd.addColorStop(0, `rgba(${r},${g},${b},${(70 * power / 255).toFixed(3)})`);
      grd.addColorStop(1, `rgba(${r},${g},${b},0)`);
      ctx.fillStyle = grd; ctx.beginPath(); ctx.arc(px, py, hr, 0, Math.PI * 2); ctx.fill();
    }
    ctx.fillStyle = `rgb(${r},${g},${b})`; ctx.strokeStyle = C('BgCanvas'); ctx.lineWidth = 1.2;
    ctx.beginPath(); ctx.arc(px, py, dotR, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
    if (showLabels) {
      ctx.lineWidth = 3; ctx.lineJoin = 'round'; ctx.strokeStyle = C('BgCanvas'); ctx.strokeText(p.name, px + dotR + 1.5, py + 3);
      ctx.fillStyle = C('TextPrimary'); ctx.fillText(p.name, px + dotR + 1.5, py + 3);
    }
  }

  // 4. Informations
  if (topo) {
    ctx.fillStyle = C('TextMuted'); ctx.font = '8px monospace';
    const unplaced = topo.labels.length - pts.length;
    ctx.fillText(`${topo.channels} canaux  •  ${topo.rate} Hz  •  Bande : Total${unplaced > 0 ? `  •  ${unplaced} canal(aux) hors 10-20 non placés` : ''}`, 6, H - 8);
  }
}

setInterval(() => { pulse += 0.10; draw(); }, 50);
window.addEventListener('resize', draw);
Theme.onChange(draw);

function connect() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws?kind=topo`);
  const conn = document.getElementById('conn');
  ws.onopen = () => { conn.textContent = '● connecté'; conn.style.color = C('Good'); };
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.type === 'topo') { topo = m.topo; document.getElementById('wait').style.display = 'none'; }
  };
  ws.onclose = () => { conn.textContent = '● déconnecté'; conn.style.color = C('Critical'); setTimeout(connect, 1500); };
}

fetch('/api/meta').then(r => r.json()).then(m => { electrodes = m.electrodes || {}; connect(); });
