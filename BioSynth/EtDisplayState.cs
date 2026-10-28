namespace BioSynth.App;

/// <summary>
/// État d'affichage oculométrique, repris de MainWindow (DrainEtQueue, SetEtRawMode,
/// BuildRawGazeModel, EstimateGazeFromRaw) : traînée du regard, compteurs d'événements,
/// textes du panneau, et mode « colonnes brutes » pour les replays sans GazeX/GazeY.
/// </summary>
internal sealed class EtDisplayState
{
    public const int RAW_MAX = 8;

    // Traînée : 3 dernières positions normalisées [0,1]
    private double _x1 = 0.5, _y1 = 0.5, _x2 = 0.5, _y2 = 0.5, _x3 = 0.5, _y3 = 0.5;
    public double[][] Trail => new[] { new[] { R(_x1), R(_y1) }, new[] { R(_x2), R(_y2) }, new[] { R(_x3), R(_y3) } };
    private static double R(double v) => Math.Round(v, 4);

    public int    Fix, Sac, Blink, VelCount;
    public double VelSum;
    public string LastEvent { get; private set; } = "fixation";

    public object Text { get; private set; } = new { gx = "—", gy = "—", pl = "—", pr = "—", vel = "—", evt = "—", detail = "—" };

    // ── Mode brut ──────────────────────────────────────────────────────────
    public bool     RawMode  { get; private set; }
    public string[] RawNames { get; private set; } = Array.Empty<string>();
    private (double x, double y)[]? _anchors;
    private bool   _axisMode;
    private double _minX = double.MaxValue, _maxX = double.MinValue, _minY = double.MaxValue, _maxY = double.MinValue;

    public void Reset()
    {
        _x1 = _y1 = _x2 = _y2 = _x3 = _y3 = 0.5;
        Fix = Sac = Blink = VelCount = 0; VelSum = 0;
        LastEvent = "fixation";
        Text = new { gx = "—", gy = "—", pl = "—", pr = "—", vel = "—", evt = "—", detail = "—" };
    }

    public void SetRawMode(string[]? names)
    {
        RawMode = names != null && names.Length > 0;
        if (!RawMode) { RawNames = Array.Empty<string>(); _anchors = null; return; }
        RawNames = names!.Take(RAW_MAX).ToArray();
        _x1 = _x2 = _x3 = 0.5; _y1 = _y2 = _y3 = 0.5;
        BuildRawGazeModel(names!);
    }

    public void AcceptStandard(EyeSample s)
    {
        Push(s.GazeXNorm, s.GazeYNorm);
        Text = new
        {
            gx = $"{s.GazeX:F0} px", gy = $"{s.GazeY:F0} px",
            pl = $"{s.PupilLeft:F2}", pr = $"{s.PupilRight:F2}",
            vel = $"{s.VelocityDeg:F0}",
            evt = s.EventType switch { "saccade" => "SACC", "microsaccade" => "µSACC", "blink" => "BLINK", _ => "FIX" },
            detail = s.EventType.ToUpperInvariant(),
        };
        if (s.EventType == "fixation") Fix++;
        else if (s.EventType == "saccade") { Sac++; VelSum += s.VelocityDeg; VelCount++; }
        else if (s.EventType == "blink") Blink++;
        LastEvent = s.EventType;
    }

    /// <summary>Retourne la ligne (≤ 8 colonnes, NaN → 0) à tracer et met à jour le point de regard estimé.</summary>
    public float[] AcceptRaw(double[] raw)
    {
        int n = Math.Min(raw.Length, RawNames.Length);
        var row = new float[n];
        for (int c = 0; c < n; c++) row[c] = double.IsFinite(raw[c]) ? (float)Math.Round(raw[c], 4) : 0f;

        var est = EstimateGazeFromRaw(raw, out string nearest);
        if (est.HasValue) Push(est.Value.x, est.Value.y);
        Text = new
        {
            gx = n > 0 ? $"{raw[0]:F2}" : "—", gy = n > 1 ? $"{raw[1]:F2}" : "—",
            pl = n > 2 ? $"{raw[2]:F2}" : "—", pr = n > 3 ? $"{raw[3]:F2}" : "—",
            vel = "—", evt = "RAW",
            detail = nearest.Length > 0 ? nearest.ToUpperInvariant() : "BRUT",
        };
        LastEvent = "fixation";
        return row;
    }

    private void Push(double x, double y)
    {
        _x3 = _x2; _y3 = _y2;
        _x2 = _x1; _y2 = _y1;
        _x1 = double.IsFinite(x) ? x : _x1; _y1 = double.IsFinite(y) ? y : _y1;
    }

    /// <summary>
    /// 2 colonnes numériques : X et Y directs, normalisés sur leur plage observée.
    /// Sinon (angles entre le regard et des zones d'intérêt) : chaque colonne reçoit un ancrage
    /// d'après son nom (visage en haut, poitrine au milieu, bassin en bas, « devant » au centre),
    /// sinon réparti sur un cercle ; le point est la moyenne des ancrages pondérée par 1/angle².
    /// </summary>
    private void BuildRawGazeModel(string[] names)
    {
        _minX = _minY = double.MaxValue; _maxX = _maxY = double.MinValue;
        _axisMode = names.Length == 2 && !names.Any(n => n.ToLowerInvariant().Contains("angle"));
        if (_axisMode) { _anchors = null; return; }

        static (double, double)? Known(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("visage") || n.Contains("face") || n.Contains("tete") || n.Contains("tête") || n.Contains("head")) return (0.5, 0.14);
            if (n.Contains("sein") || n.Contains("poitrine") || n.Contains("chest") || n.Contains("torse") || n.Contains("torso")) return (0.5, 0.42);
            if (n.Contains("genital") || n.Contains("génital") || n.Contains("bassin") || n.Contains("pelvis") || n.Contains("hanche")) return (0.5, 0.72);
            if (n.Contains("jambe") || n.Contains("leg") || n.Contains("pied") || n.Contains("feet")) return (0.5, 0.92);
            if (n.Contains("devant") || n.Contains("front") || n.Contains("centre") || n.Contains("center") || n.Contains("ahead")) return (0.5, 0.5);
            if (n.Contains("gauche") || n.Contains("left"))  return (0.15, 0.5);
            if (n.Contains("droit")  || n.Contains("right")) return (0.85, 0.5);
            return null;
        }

        _anchors = new (double, double)[names.Length];
        var unknown = new List<int>();
        for (int i = 0; i < names.Length; i++)
        {
            var k = Known(names[i]);
            if (k.HasValue) _anchors[i] = k.Value; else unknown.Add(i);
        }
        for (int j = 0; j < unknown.Count; j++)
        {
            double a = -Math.PI / 2 + 2 * Math.PI * j / unknown.Count;
            _anchors[unknown[j]] = (0.5 + 0.38 * Math.Cos(a), 0.5 + 0.38 * Math.Sin(a));
        }
    }

    private (double x, double y)? EstimateGazeFromRaw(double[] raw, out string nearest)
    {
        nearest = "";
        if (_axisMode)
        {
            if (raw.Length < 2 || double.IsNaN(raw[0]) || double.IsNaN(raw[1])) return null;
            _minX = Math.Min(_minX, raw[0]); _maxX = Math.Max(_maxX, raw[0]);
            _minY = Math.Min(_minY, raw[1]); _maxY = Math.Max(_maxY, raw[1]);
            double rx = _maxX - _minX, ry = _maxY - _minY;
            return (rx > 1e-9 ? (raw[0] - _minX) / rx : 0.5, ry > 1e-9 ? (raw[1] - _minY) / ry : 0.5);
        }
        if (_anchors == null) return null;

        double sx = 0, sy = 0, sw = 0, best = double.MaxValue; int bestIdx = -1;
        int n = Math.Min(raw.Length, _anchors.Length);
        for (int i = 0; i < n; i++)
        {
            double a = raw[i];
            if (double.IsNaN(a) || a < 0) continue;            // -1 / NaN = donnée invalide
            double w = 1.0 / Math.Max(a * a, 0.25);
            sx += w * _anchors[i].x; sy += w * _anchors[i].y; sw += w;
            if (a < best) { best = a; bestIdx = i; }
        }
        if (sw <= 0) return null;
        if (bestIdx >= 0 && bestIdx < RawNames.Length)
            nearest = RawNames[bestIdx].Replace("AngleYeux", "").Replace("Angle", "");
        return (Math.Clamp(sx / sw, 0, 1), Math.Clamp(sy / sw, 0, 1));
    }
}
