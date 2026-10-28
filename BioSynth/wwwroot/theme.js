// Thème clair / sombre, partagé par l'interface et la TopoMap.
// « Auto » suit le réglage du système (prefers-color-scheme, donc GNOME/KDE sous Linux),
// comme l'ancienne version WPF suivait le réglage Windows. Le choix est mémorisé dans le navigateur.
(function () {
  const KEY = 'biosynth.theme';
  const mq = window.matchMedia('(prefers-color-scheme: dark)');
  const listeners = [];
  let cache = {};

  function load() { try { return localStorage.getItem(KEY) || 'Auto'; } catch { return 'Auto'; } }
  function save(v) { try { localStorage.setItem(KEY, v); } catch { /* stockage indisponible */ } }

  function apply() {
    const pref = load();
    const dark = pref === 'Dark' || (pref === 'Auto' && mq.matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    cache = {};
    listeners.forEach(fn => { try { fn(dark); } catch (e) { console.error(e); } });
  }

  window.Theme = {
    get pref() { return load(); },
    set(pref) { save(pref); apply(); },
    onChange(fn) { listeners.push(fn); },
    get dark() { return document.documentElement.dataset.theme !== 'light'; },
    /** Couleur d'une variable CSS de palette (ex. 'AccentCyan'). */
    color(name) {
      if (!(name in cache)) cache[name] = getComputedStyle(document.documentElement).getPropertyValue('--' + name).trim() || '#888';
      return cache[name];
    },
    /** Couleur d'une variable de palette en [r, g, b] (0-255). */
    rgb(name) {
      const h = this.color(name).replace('#', '');
      return [0, 2, 4].map(i => parseInt(h.substr(i, 2), 16) || 0);
    },
    /** Rampe séquentielle Seq0 (faible) → Seq4 (fort) du thème courant, t ∈ [0, 1] → [r, g, b] (0-255). */
    ramp(t, out = [0, 0, 0]) {
      if (!cache.__ramp) cache.__ramp = [0, 1, 2, 3, 4].map(i => this.rgb('Seq' + i));
      const s = cache.__ramp;
      t = Math.min(1, Math.max(0, t)) * 4;
      const k = Math.min(3, Math.floor(t)), f = t - k;
      for (let i = 0; i < 3; i++) out[i] = s[k][i] + (s[k + 1][i] - s[k][i]) * f;
      return out;
    },
  };

  mq.addEventListener('change', () => { if (load() === 'Auto') apply(); });
  window.addEventListener('storage', e => { if (e.key === KEY) apply(); });
  apply();
})();
