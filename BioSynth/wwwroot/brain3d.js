// Cerveau 3D : reprise web de l'ancienne vue HelixToolkit (BrainView3DWindow).
// Rendu three.js (embarqué dans lib/three, aucun accès réseau).
//
//   - Modèle : brain.obj du dossier de données s'il existe (mêmes noms de parties
//     Brain_Part_01..06 que l'ancienne vue), sinon un cerveau procédural (deux hémisphères
//     à circonvolutions, cervelet, tronc cérébral).
//   - Heatmap : interpolée sommet par sommet à partir des électrodes (pondération inverse de
//     la distance, comme la TopoMap), mêlée à la couleur anatomique ; l'ancienne vue colorait
//     chaque hémisphère d'un seul bloc selon la moyenne des activations de zone.
//   - Électrodes : projetées sur la surface du modèle, quel qu'il soit, pulsation et couleur
//     thermique selon la puissance du canal.
import * as THREE from 'three';
import { OrbitControls } from './lib/three/OrbitControls.js';
import { OBJLoader } from './lib/three/OBJLoader.js';

const $ = id => document.getElementById(id);
const stage = $('stage');

// ── Rendu ──────────────────────────────────────────────────────────────────
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
renderer.setClearColor(Theme.color('BgCanvas'));
stage.prepend(renderer.domElement);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(42, 1, 0.05, 40);
camera.position.set(2.2, 1.3, 2.4);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.08;
controls.minDistance = 1.4;
controls.maxDistance = 8;
controls.autoRotateSpeed = 1.2;

// Lumières neutres (blanc, bleu marine) : les couleurs viennent des jetons de palette
scene.add(new THREE.HemisphereLight(0xffffff, 0x1b2540, 0.9));
scene.add(new THREE.AmbientLight(0x27324f, 0.6));
const key = new THREE.DirectionalLight(0xffffff, 1.6); key.position.set(2, 3, 3); scene.add(key);
const fill = new THREE.DirectionalLight(0x3d4f80, 1.0); fill.position.set(-2, -0.6, -1.5); scene.add(fill);
const headLight = new THREE.PointLight(0xffffff, 0.6, 0, 0); camera.add(headLight); scene.add(camera);

function resize() {
  const w = stage.clientWidth, h = stage.clientHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / Math.max(1, h);
  camera.updateProjectionMatrix();
}
window.addEventListener('resize', resize);

// ── Couleurs : jetons de palette du thème courant (app.css) ────────────────
// three.js travaille en espace linéaire : les couleurs sRGB des jetons sont converties.
const _c = new THREE.Color();
function lin(rgb, out = [0, 0, 0]) {
  _c.setRGB(rgb[0] / 255, rgb[1] / 255, rgb[2] / 255, THREE.SRGBColorSpace);
  out[0] = _c.r; out[1] = _c.g; out[2] = _c.b;
  return out;
}
const tokenColor = name => lin(Theme.rgb(name));
/** Rampe séquentielle CIRICS (Seq0 faible → Seq4 fort), identique à la TopoMap. */
const _r = [0, 0, 0];
function heat(t, out) { return lin(Theme.ramp(t, _r), out); }

// ── Bruit de Perlin 3D (circonvolutions du modèle procédural) ───────────────
const perm = new Uint8Array(512);
{
  const p = Array.from({ length: 256 }, (_, i) => i);
  let seed = 1337;
  const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  for (let i = 255; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [p[i], p[j]] = [p[j], p[i]]; }
  for (let i = 0; i < 512; i++) perm[i] = p[i & 255];
}
const fade = t => t * t * t * (t * (t * 6 - 15) + 10);
const lerp = (a, b, t) => a + (b - a) * t;
function grad(h, x, y, z) {
  const u = (h & 15) < 8 ? x : y, v = (h & 15) < 4 ? y : ((h & 15) === 12 || (h & 15) === 14 ? x : z);
  return ((h & 1) ? -u : u) + ((h & 2) ? -v : v);
}
function perlin(x, y, z) {
  const X = Math.floor(x) & 255, Y = Math.floor(y) & 255, Z = Math.floor(z) & 255;
  x -= Math.floor(x); y -= Math.floor(y); z -= Math.floor(z);
  const u = fade(x), v = fade(y), w = fade(z);
  const A = perm[X] + Y, AA = perm[A] + Z, AB = perm[A + 1] + Z, B = perm[X + 1] + Y, BA = perm[B] + Z, BB = perm[B + 1] + Z;
  return lerp(lerp(lerp(grad(perm[AA], x, y, z), grad(perm[BA], x - 1, y, z), u),
                   lerp(grad(perm[AB], x, y - 1, z), grad(perm[BB], x - 1, y - 1, z), u), v),
              lerp(lerp(grad(perm[AA + 1], x, y, z - 1), grad(perm[BA + 1], x - 1, y, z - 1), u),
                   lerp(grad(perm[AB + 1], x, y - 1, z - 1), grad(perm[BB + 1], x - 1, y - 1, z - 1), u), v), w);
}
const smoothstep = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

// ── Cerveau procédural ─────────────────────────────────────────────────────
// Repère (direct) : Y haut, Z avant (le nez regarde la caméra en vue « Face ») ; un sujet
// tourné vers +Z a donc sa gauche en +X. Le modèle procédural est symétrique.

function hemisphere(side) {
  const geo = new THREE.SphereGeometry(1, 200, 150);
  const pos = geo.attributes.position;
  const v = new THREE.Vector3(), c = new THREE.Vector3(side * 0.2, 0.05, 0), n = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    const dx = v.x, dy = v.y, dz = v.z;
    const medial = dx * side < 0;
    // Ellipsoïde de base : base plus plate, pôle frontal plus étroit ; la face médiane est
    // aplanie plus bas, en plaquant contre la ligne médiane ce qui la dépasse
    let x = dx * 0.5 * (1 - 0.12 * Math.max(0, dz));
    let y = dy * (dy < 0 ? 0.42 : 0.62);
    let z = dz * 0.88;
    // Lobe temporal : renflement latéral inférieur, vers l'avant
    const tl = Math.exp(-(((dy + 0.45) / 0.3) ** 2 + ((dz - 0.15) / 0.45) ** 2)) * Math.max(0, dx * side);
    x += side * 0.06 * tl; y -= 0.12 * tl;
    // Scissure latérale (sylvienne) : sillon oblique au-dessus du lobe temporal
    const sy = Math.exp(-(((dy + 0.08 - 0.25 * dz) / 0.07) ** 2)) * smoothstep(-0.3, 0.2, dz) * Math.max(0, dx * side);
    // Circonvolutions : sillons aux passages par zéro du bruit (deux octaves)
    const px = x * 5.2, py = y * 5.2, pz = z * 5.2;
    const n1 = Math.abs(perlin(px + side * 7.3, py, pz));
    const n2 = Math.abs(perlin(px * 2.1 + 11, py * 2.1, pz * 2.1));
    const sulci = (smoothstep(0, 0.16, n1) - 1) * 0.045 + (smoothstep(0, 0.2, n2) - 1) * 0.012;
    const k = medial ? 0.35 : 1;
    n.set(x, y, z).normalize();
    const d = sulci * k - 0.05 * sy;
    let X = c.x + x + n.x * d;
    if (X * side < 0.012) X = side * 0.012;   // face médiane plane, scissure de 2,4 cm (échelle ~1:7)
    pos.setXYZ(i, X, c.y + y + n.y * d, c.z + z + n.z * d);
  }
  geo.computeVertexNormals();
  return geo;
}

function cerebellum() {
  const geo = new THREE.SphereGeometry(1, 120, 80);
  const pos = geo.attributes.position, v = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    const folia = -0.012 * Math.abs(Math.sin(v.y * 22 + v.z * 4));
    const r = 1 + folia / 0.3;
    pos.setXYZ(i, v.x * 0.52 * r, -0.36 + v.y * 0.24 * r, -0.6 + v.z * 0.3 * r);
  }
  geo.computeVertexNormals();
  return geo;
}

function brainstem() {
  const geo = new THREE.CylinderGeometry(0.12, 0.08, 0.62, 40, 8);
  geo.rotateX(0.35);
  geo.translate(0, -0.62, -0.28);
  return geo;
}

// ── Modèle courant ─────────────────────────────────────────────────────────
// Chaque partie : { mesh, base:[r,g,b], heat:bool, dirs:Float32Array (directions des sommets),
//                   idx:Uint8Array, w:Float32Array (k plus proches électrodes et poids) }
const K = 6;
let model = null;       // THREE.Group
let parts = [];
let modelLabel = 'procédural';
const objTransform = loadObjTransform();

function makePart(geometry, token, heatOn, name) {
  const base = tokenColor(token);
  if (geometry.index) geometry = geometry.toNonIndexed();
  const n = geometry.attributes.position.count;
  const colors = new Float32Array(n * 3);
  for (let i = 0; i < n; i++) colors.set(base, i * 3);
  geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  const mat = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.62, metalness: 0.02, side: THREE.DoubleSide });
  const mesh = new THREE.Mesh(geometry, mat);
  mesh.name = name || '';
  return { mesh, token, base, heat: heatOn, dirs: null, idx: null, w: null };
}

function buildProcedural() {
  const g = new THREE.Group();
  parts = [
    makePart(hemisphere(-1), 'Cortex', true, 'hémisphère droit'),
    makePart(hemisphere(+1), 'Cortex', true, 'hémisphère gauche'),
    makePart(cerebellum(), 'CortexDeep', true, 'cervelet'),
    makePart(brainstem(), 'CortexStem', false, 'tronc'),
  ];
  parts.forEach(p => g.add(p.mesh));
  modelLabel = 'procédural';
  return g;
}

// Parties du modèle OBJ Brain_Part_01..06 : jeton de couleur et heatmap ou non
const PART_COLORS = {
  brain_part_04: ['Cortex', true], brain_part_06: ['Cortex', true],          // hémisphères
  brain_part_02: ['CortexDeep', true], brain_part_05: ['Cortex', false],     // cervelet, corps calleux
  brain_part_03: ['CortexStem', false], brain_part_01: ['CortexStem', false], // tronc, fissure
};

function buildFromObj(text, label) {
  const obj = new OBJLoader().parse(text);
  const g = new THREE.Group();
  parts = [];
  obj.traverse(o => {
    if (!o.isMesh) return;
    const key = (o.name || '').toLowerCase();
    const [token, heatOn] = PART_COLORS[key] || ['Cortex', true];
    const geo = o.geometry.clone();
    o.updateWorldMatrix(true, false);
    geo.applyMatrix4(o.matrixWorld);
    const p = makePart(geo, token, heatOn, o.name);
    parts.push(p);
    g.add(p.mesh);
  });
  if (!parts.length) throw new Error('aucun maillage dans le fichier');
  normalizeObj(g);
  modelLabel = label;
  return g;
}

/** Centre le modèle, applique l'orientation choisie et l'amène à la taille du modèle procédural. */
function normalizeObj(g) {
  for (const p of parts) {
    const geo = p.mesh.geometry;
    if (objTransform.zUp) geo.rotateX(-Math.PI / 2);
    if (objTransform.turns) geo.rotateY(objTransform.turns * Math.PI / 2);
  }
  const box = new THREE.Box3().setFromObject(g);
  const size = box.getSize(new THREE.Vector3()), center = box.getCenter(new THREE.Vector3());
  const s = 1.9 / Math.max(size.x, size.y, size.z);
  for (const p of parts) {
    p.mesh.geometry.translate(-center.x, -center.y, -center.z);
    p.mesh.geometry.scale(s, s, s);
    p.mesh.geometry.computeVertexNormals();
    p.mesh.geometry.computeBoundingSphere();
  }
}

function setModel(g) {
  if (model) {
    scene.remove(model);
    model.traverse(o => { if (o.isMesh) { o.geometry.dispose(); o.material.dispose(); } });
  }
  model = g;
  scene.add(model);
  $('modelName').textContent = modelLabel;
  $('btnTurn').disabled = $('btnUp').disabled = $('btnProc').disabled = modelLabel === 'procédural';
  $('btnUp').textContent = objTransform.zUp ? 'Axe Z' : 'Axe Y';
  layoutElectrodes();
}

// ── Électrodes ─────────────────────────────────────────────────────────────
let electrodes = {};    // nom → [x, y, z] (repère 10-20 : X droite, Y avant, Z haut)
let topo = null;        // dernière trame reçue du serveur
let elec = [];          // { name, ch, dir, pos, normal, mesh, label, value, shown }
const elecGroup = new THREE.Group(); scene.add(elecGroup);
const sphereGeo = new THREE.SphereGeometry(1, 16, 12);
const raycaster = new THREE.Raycaster();
const ORIGIN = new THREE.Vector3(0, -0.05, 0);

/** Direction d'une électrode dans le repère de la scène. La table 10-20 a la droite du sujet en +X ;
 *  dans la scène (sujet tourné vers +Z, repère direct) la droite du sujet est en -X, d'où le signe.
 *  Les électrodes de l'équateur 10-20 (Fp1, T7, O1) tombent sur les faces latérales et inférieures. */
function electrodeDir(p) { return new THREE.Vector3(-p[0], p[2] * 1.05 + 0.08, p[1]).normalize(); }

/** Point de sortie de surface le long d'une direction, ou null. */
function surfaceHit(dir, targets) {
  raycaster.set(ORIGIN, dir);
  const hits = raycaster.intersectObjects(targets, false);
  return hits.length ? hits[hits.length - 1] : null;
}

function layoutElectrodes() {
  elecGroup.clear();
  $('labels').innerHTML = '';
  elec = [];
  if (!topo || !model) { computeWeights(); return; }
  const targets = parts.filter(p => p.heat).map(p => p.mesh);
  topo.labels.forEach((name, ch) => {
    const p = electrodes[name];
    if (!p) return;
    const dir = electrodeDir(p);
    let pos, normal = dir.clone();
    const h = surfaceHit(dir, targets);        // sortie de surface (la plus éloignée)
    if (h) {
      if (h.face) normal = h.face.normal.clone().transformDirection(h.object.matrixWorld);
      if (normal.dot(dir) < 0) normal.negate();
      pos = h.point.clone().addScaledVector(dir, 0.025);
    } else {
      // Ligne médiane (Fz, Cz, Pz...) : le rayon passe dans la scissure inter-hémisphérique.
      // On vise un peu à gauche et à droite, et on pose l'électrode entre les deux surfaces.
      const found = [];
      for (const off of [0.06, 0.12, 0.2]) {
        const l = surfaceHit(dir.clone().add(new THREE.Vector3(off, 0, 0)).normalize(), targets);
        const r = surfaceHit(dir.clone().add(new THREE.Vector3(-off, 0, 0)).normalize(), targets);
        if (l && r) { found.push(l.point, r.point); break; }
      }
      pos = found.length
        ? found[0].clone().add(found[1]).multiplyScalar(0.5).addScaledVector(dir, 0.03)
        : ORIGIN.clone().addScaledVector(dir, 0.95);
    }
    const mesh = new THREE.Mesh(sphereGeo, new THREE.MeshStandardMaterial({ color: Theme.color('Seq2'), emissive: 0x000000, roughness: 0.35 }));
    mesh.position.copy(pos);
    elecGroup.add(mesh);
    const label = document.createElement('span');
    label.textContent = name;
    $('labels').appendChild(label);
    elec.push({ name, ch, dir, pos, normal, mesh, label, value: 0.2 });
  });
  computeWeights();
  const placed = elec.length, total = topo.labels.length;
  $('info').textContent = `${topo.channels} canaux · ${topo.rate} Hz · ${placed}/${total} électrodes placées · modèle ${modelLabel}`;
}

/** Pour chaque sommet : les K électrodes angulairement les plus proches et leurs poids IDW. */
function computeWeights() {
  const v = new THREE.Vector3();
  for (const p of parts) {
    p.idx = p.w = null;
    if (!p.heat || !elec.length) continue;
    const pos = p.mesh.geometry.attributes.position, n = pos.count;
    const kk = Math.min(K, elec.length);
    const idx = new Uint8Array(n * kk), w = new Float32Array(n * kk);
    const ex = elec.map(e => e.dir.x), ey = elec.map(e => e.dir.y), ez = elec.map(e => e.dir.z);
    const bestD = new Float32Array(kk), bestI = new Uint8Array(kk);
    for (let i = 0; i < n; i++) {
      v.fromBufferAttribute(pos, i).sub(ORIGIN).normalize();
      bestD.fill(1e9);
      for (let e = 0; e < elec.length; e++) {
        const d2 = 2 - 2 * (v.x * ex[e] + v.y * ey[e] + v.z * ez[e]);   // corde² sur la sphère unité
        if (d2 >= bestD[kk - 1]) continue;
        let j = kk - 1;
        while (j > 0 && bestD[j - 1] > d2) { bestD[j] = bestD[j - 1]; bestI[j] = bestI[j - 1]; j--; }
        bestD[j] = d2; bestI[j] = e;
      }
      let sum = 0;
      for (let j = 0; j < kk; j++) { const ww = 1 / Math.pow(bestD[j] + 1e-4, 1.5); w[i * kk + j] = ww; sum += ww; }
      for (let j = 0; j < kk; j++) { w[i * kk + j] /= sum; idx[i * kk + j] = bestI[j]; }
    }
    p.idx = idx; p.w = w; p.k = kk;
  }
}

// ── Valeurs : même normalisation que la TopoMap (puissance lissée 70 %, zone 30 %) ──
const RAW_GAIN = 1.5;
function normPower(ch) {
  if (!topo) return 0.2;
  const raw = topo.power[ch] || 0;
  const zone = topo.zones[topo.region[ch]] ?? 0.5;
  return Math.min(1, Math.max(0, raw * RAW_GAIN * 0.7 + zone * 0.3));
}

const tmp = [0, 0, 0];
function recolor() {
  const heatOn = $('chkHeat').checked;
  const vals = elec.map(e => e.value);
  for (const p of parts) {
    const col = p.mesh.geometry.attributes.color, arr = col.array, n = col.count;
    if (!heatOn || !p.idx) {
      if (p.painted !== false) { for (let i = 0; i < n; i++) arr.set(p.base, i * 3); col.needsUpdate = true; p.painted = false; }
      continue;
    }
    const k = p.k, b = p.base;
    for (let i = 0; i < n; i++) {
      let a = 0;
      for (let j = 0; j < k; j++) a += p.w[i * k + j] * vals[p.idx[i * k + j]];
      heat(a, tmp);
      // Mélange anatomique → thermique (même proportion que l'ancienne vue : 60 % au maximum),
      // éclairci pour garder le relief lisible
      const blend = 0.85;   // la rampe porte la couleur (comme la TopoMap) ; 15 % de cortex neutre
      arr[i * 3]     = b[0] * (1 - blend) + tmp[0] * blend;
      arr[i * 3 + 1] = b[1] * (1 - blend) + tmp[1] * blend;
      arr[i * 3 + 2] = b[2] * (1 - blend) + tmp[2] * blend;
    }
    col.needsUpdate = true;
    p.painted = true;
  }
}

// ── Boucle d'animation ─────────────────────────────────────────────────────
let pulse = 0, lastRecolor = 0;
const v3 = new THREE.Vector3(), toCam = new THREE.Vector3();
function animate(t) {
  requestAnimationFrame(animate);
  pulse += 0.08 * 0.6;
  // Lissage des valeurs vers la dernière trame (les trames arrivent à ~3 Hz)
  for (const e of elec) e.value += (normPower(e.ch) - e.value) * 0.08;
  if (t - lastRecolor > 90) { recolor(); lastRecolor = t; }

  const showE = $('chkElec').checked, showL = $('chkLabels').checked;
  elecGroup.visible = showE;
  const w = stage.clientWidth, h = stage.clientHeight;
  elec.forEach((e, i) => {
    const pk = 1 + 0.3 * Math.sin(pulse + i * 0.45) * e.value;
    e.mesh.scale.setScalar((0.026 + e.value * 0.03) * pk);
    heat(e.value, tmp);
    e.mesh.material.color.setRGB(tmp[0], tmp[1], tmp[2]);
    e.mesh.material.emissive.setRGB(tmp[0] * 0.35, tmp[1] * 0.35, tmp[2] * 0.35);
    // Étiquette visible si l'électrode fait face à la caméra
    toCam.subVectors(camera.position, e.pos);
    const facing = toCam.dot(e.normal) > 0;
    if (showE && showL && facing) {
      v3.copy(e.pos).project(camera);
      e.label.style.display = 'block';
      e.label.style.left = ((v3.x + 1) / 2 * w) + 'px';
      e.label.style.top = ((1 - v3.y) / 2 * h) + 'px';
      heat(e.value, tmp);
    } else e.label.style.display = 'none';
  });

  controls.autoRotate = $('chkRotate').checked;
  controls.update();
  renderer.render(scene, camera);
}

// ── Vues prédéfinies ───────────────────────────────────────────────────────
// Gauche / droite : côtés du sujet (sa gauche est en +X) ; Dessus : nez en haut (convention 10-20)
const VIEWS = { front: [0, 0.35, 3.3], top: [0, 3.4, -0.01], left: [3.3, 0.3, 0], right: [-3.3, 0.3, 0], back: [0, 0.35, -3.3] };
document.querySelectorAll('[data-view]').forEach(b => b.addEventListener('click', () => {
  camera.position.set(...VIEWS[b.dataset.view]);
  controls.target.set(0, 0, 0);
  controls.update();
}));
$('chkHeat').addEventListener('change', recolor);

// Changement de thème : fond, couleurs du cortex et rampe sont relus dans la palette
Theme.onChange(() => {
  renderer.setClearColor(Theme.color('BgCanvas'));
  for (const p of parts) { p.base = tokenColor(p.token); p.painted = null; }
  recolor();
});

// ── Modèle OBJ ─────────────────────────────────────────────────────────────
function loadObjTransform() {
  try { return Object.assign({ turns: 0, zUp: false }, JSON.parse(localStorage.getItem('biosynth.brain3d') || '{}')); }
  catch { return { turns: 0, zUp: false }; }
}
function saveObjTransform() { try { localStorage.setItem('biosynth.brain3d', JSON.stringify(objTransform)); } catch { /* rien */ } }

let objText = null;
function showMsg(text, ms = 6000) {
  const m = $('msg'); m.textContent = text; m.hidden = false;
  clearTimeout(showMsg.t); showMsg.t = setTimeout(() => m.hidden = true, ms);
}
function useObj(text, label) {
  try { setModel(buildFromObj(text, label)); objText = text; }
  catch (e) { showMsg('Modèle OBJ illisible : ' + e.message); setModel(buildProcedural()); }
}
$('btnObj').addEventListener('click', () => $('fileObj').click());
$('fileObj').addEventListener('change', async () => {
  const f = $('fileObj').files[0];
  if (!f) return;
  showMsg('Chargement de ' + f.name + '…', 2000);
  useObj(await f.text(), f.name);
  $('fileObj').value = '';
});
$('btnTurn').addEventListener('click', () => { objTransform.turns = (objTransform.turns + 1) % 4; saveObjTransform(); if (objText) useObj(objText, modelLabel); });
$('btnUp').addEventListener('click', () => { objTransform.zUp = !objTransform.zUp; saveObjTransform(); if (objText) useObj(objText, modelLabel); });
$('btnProc').addEventListener('click', () => { objText = null; setModel(buildProcedural()); });

// ── Données temps réel ─────────────────────────────────────────────────────
function connect() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws?kind=topo`);
  const conn = $('conn');
  ws.onopen = () => { conn.textContent = '● connecté'; conn.style.color = Theme.color('Good'); };
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.type !== 'topo') return;
    const changed = !topo || topo.labels.length !== m.topo.labels.length || topo.labels.some((l, i) => l !== m.topo.labels[i]);
    topo = m.topo;
    if (changed) layoutElectrodes();
  };
  ws.onclose = () => { conn.textContent = '● déconnecté'; conn.style.color = Theme.color('Critical'); setTimeout(connect, 1500); };
}

(async function init() {
  resize();
  const meta = await fetch('/api/meta').then(r => r.json());
  electrodes = meta.electrodes || {};
  let loaded = false;
  if (meta.brainModel) {
    try {
      const res = await fetch('/api/brain-model');
      if (res.ok) { useObj(await res.text(), meta.brainModel.split(/[\\/]/).pop()); loaded = true; }
    } catch { /* repli procédural */ }
  }
  if (!loaded) setModel(buildProcedural());
  document.querySelector('[data-view="front"]').click();
  camera.position.set(2.0, 1.3, 2.6); controls.update();
  connect();
  requestAnimationFrame(animate);
})();
