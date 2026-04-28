using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BioSynth
{
    /// <summary>
    /// Carte topographique 2D des électrodes EEG (vue coronale du dessus).
    /// Rendu WPF Canvas pur — pas de dépendance externe.
    ///
    /// Affiche :
    ///   - Contour de la tête (ellipse + nez + oreilles)
    ///   - Heatmap d'interpolation bilinéaire par zone
    ///   - Électrodes colorées par puissance de signal (palette thermique)
    ///   - Labels des canaux
    ///   - Légende de l'échelle de couleur
    /// </summary>
    public class EEGTopoMap : UserControl
    {
        // ── Dépendances ───────────────────────────────────────────────────────
        private BrainZoneController? _zones;
        private int                  _channelCount = 8;
        private int                  _sampleRate   = 256;

        // ── Données temps réel ────────────────────────────────────────────────
        // Buffer RMS par canal — mis à jour depuis le thread de génération
        private readonly double[] _power = new double[64];

        // ── UI ────────────────────────────────────────────────────────────────
        private readonly Canvas   _canvas;
        private CheckBox?         _chkLabels;
        private CheckBox?         _chkHeatmap;
        private CheckBox?         _chkContour;
        private TextBlock?        _lblBand;

        // ── Éléments graphiques (recyclés à chaque redraw) ───────────────────
        // Heatmap : grille de rectangles colorés
        private const int HEAT_RES = 28;  // résolution de la grille d'interpolation
        private readonly Rectangle[,] _heatCells = new Rectangle[HEAT_RES, HEAT_RES];

        // Points électrodes
        private readonly List<Ellipse>   _electroDots   = new();
        private readonly List<Ellipse>   _electroHalos  = new();
        private readonly List<TextBlock> _electroLabels = new();

        // ── Animation ─────────────────────────────────────────────────────────
        private readonly DispatcherTimer _renderTimer;
        private double _pulsePhase = 0;

        // ── Options ───────────────────────────────────────────────────────────
        public bool ShowLabels  { get; set; } = true;
        public bool ShowHeatmap { get; set; } = true;
        public bool ShowContour { get; set; } = true;
        public string SelectedBand { get; set; } = "Total";

        // ── Couleurs ──────────────────────────────────────────────────────────
        private static readonly Color BgColor = Color.FromRgb(8, 12, 24);

        // ════════════════════════════════════════════════════════════════════
        public EEGTopoMap()
        {
            Background = new SolidColorBrush(BgColor);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            Content = root;

            // Barre d'options
            var topBar = new Border { Background = new SolidColorBrush(Color.FromRgb(13, 18, 30)) };
            topBar.Child = BuildOptionsBar();
            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // Canvas principal
            _canvas = new Canvas { Background = new SolidColorBrush(BgColor), ClipToBounds = true };
            _canvas.SizeChanged += (s, e) => { BuildHeatmapGrid(); Redraw(); };
            Grid.SetRow(_canvas, 1);
            root.Children.Add(_canvas);

            // Barre de légende
            var legend = BuildLegendBar();
            Grid.SetRow(legend, 2);
            root.Children.Add(legend);

            // Timer 20 fps
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _renderTimer.Tick += (s, e) => { _pulsePhase += 0.10; Redraw(); };
            _renderTimer.Start();
        }

        // ── Configuration externe ─────────────────────────────────────────────
        public void Configure(BrainZoneController zones, int channelCount, int sampleRate)
        {
            _zones        = zones;
            _channelCount = channelCount;
            _sampleRate   = sampleRate;
        }

        /// <summary>
        /// Met à jour la puissance RMS d'un canal.
        /// Appelé depuis le thread UI (DrainEegQueue).
        /// </summary>
        public void UpdateChannelPower(int ch, double rmsValue)
        {
            if (ch >= 0 && ch < _power.Length)
                _power[ch] = rmsValue;
        }

        /// <summary>Met à jour tous les canaux d'un coup.</summary>
        public void UpdateAllPower(double[] powers)
        {
            int n = Math.Min(powers.Length, _power.Length);
            Array.Copy(powers, _power, n);
        }

        // ════════════════════════════════════════════════════════════════════
        // DESSIN PRINCIPAL
        // ════════════════════════════════════════════════════════════════════

        private void Redraw()
        {
            double W = _canvas.ActualWidth;
            double H = _canvas.ActualHeight;
            if (W < 60 || H < 60) return;

            double cx = W / 2;
            double cy = H / 2;
            double R  = Math.Min(W, H) * 0.42;  // rayon de la tête

            _canvas.Children.Clear();

            // 1. Heatmap d'interpolation
            if (_chkHeatmap?.IsChecked == true)
                DrawHeatmap(cx, cy, R);

            // 2. Contour de la tête
            if (_chkContour?.IsChecked == true)
                DrawHeadContour(cx, cy, R);

            // 3. Électrodes
            DrawElectrodes(cx, cy, R);

            // 4. Indicateur de bande et orientation
            DrawOverlay(W, H);
        }

        // ── Heatmap ───────────────────────────────────────────────────────────

        private void DrawHeatmap(double cx, double cy, double R)
        {
            int n = _channelCount;
            if (n == 0) return;

            double W = _canvas.ActualWidth;
            double H = _canvas.ActualHeight;
            double step = Math.Min(W, H) * 0.84 / HEAT_RES;

            double gridX0 = cx - (HEAT_RES / 2.0) * step;
            double gridY0 = cy - (HEAT_RES / 2.0) * step;

            for (int row = 0; row < HEAT_RES; row++)
            {
                for (int col = 0; col < HEAT_RES; col++)
                {
                    // Position normalisée dans [-1, 1]
                    double nx = (col + 0.5) / HEAT_RES * 2 - 1;
                    double ny = (row + 0.5) / HEAT_RES * 2 - 1;

                    // Uniquement dans le cercle de la tête
                    if (nx * nx + ny * ny > 1.0)
                        continue;

                    // Interpolation Shepard (IDW) des puissances des électrodes
                    double val = InterpolateIDW(nx, ny, n);

                    var col2 = HeatColor(val);
                    byte alpha = (byte)(180 * (1 - nx * nx - ny * ny) + 40);  // fondu aux bords

                    double px = gridX0 + col * step;
                    double py = gridY0 + row * step;

                    var rect = new Rectangle
                    {
                        Width  = step + 1,
                        Height = step + 1,
                        Fill   = new SolidColorBrush(Color.FromArgb(alpha, col2.R, col2.G, col2.B))
                    };
                    Canvas.SetLeft(rect, px);
                    Canvas.SetTop(rect,  py);
                    _canvas.Children.Add(rect);
                }
            }
        }

        /// <summary>
        /// Interpolation Inverse Distance Weighting (Shepard).
        /// Retourne une valeur normalisée [0,1] au point (nx, ny).
        /// </summary>
        private double InterpolateIDW(double nx, double ny, int n)
        {
            double sumW = 0, sumWV = 0;
            double p = 3.0;  // puissance IDW

            for (int ch = 0; ch < n; ch++)
            {
                string name = ChannelNames.GetChannelName(ch, n);
                if (!BrainZoneController.ElectrodePositions.TryGetValue(name, out var pos3))
                    continue;

                // Projeter en 2D : axial (haut) → X=pos3.X, Y=-pos3.Y
                double ex = pos3.X;
                double ey = -pos3.Y;

                double d2 = (nx - ex) * (nx - ex) + (ny - ey) * (ny - ey);
                if (d2 < 1e-6) return NormPower(ch);

                double w = 1.0 / Math.Pow(d2, p / 2.0);
                sumW  += w;
                sumWV += w * NormPower(ch);
            }

            return sumW > 0 ? Math.Clamp(sumWV / sumW, 0, 1) : 0;
        }

        private double NormPower(int ch)
        {
            if (_zones == null) return 0.2;
            double raw  = ch < _power.Length ? _power[ch] : 0;
            double zone = _zones.Zone(BrainZoneController.RegionOf(
                ChannelNames.GetChannelName(ch, _channelCount))).Activation;
            // Combiner signal brut + activation de zone
            return Math.Clamp(raw * 5.0 * 0.7 + zone * 0.3, 0, 1);
        }

        // ── Contour de la tête ────────────────────────────────────────────────

        private void DrawHeadContour(double cx, double cy, double R)
        {
            // Ellipse principale (tête)
            var head = new Ellipse
            {
                Width  = R * 2,
                Height = R * 2.05,
                Stroke = new SolidColorBrush(Color.FromRgb(120, 140, 180)),
                StrokeThickness = 2.0,
                Fill = new SolidColorBrush(Colors.Transparent)
            };
            Canvas.SetLeft(head, cx - R);
            Canvas.SetTop(head,  cy - R * 1.025);
            _canvas.Children.Add(head);

            // Nez (triangle vers le haut)
            var nose = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(cx - R * 0.08, cy - R * 0.98),
                    new Point(cx,             cy - R * 1.18),
                    new Point(cx + R * 0.08, cy - R * 0.98),
                },
                Stroke = new SolidColorBrush(Color.FromRgb(120, 140, 180)),
                StrokeThickness = 2.0,
                Fill = new SolidColorBrush(BgColor),
                StrokeLineJoin = PenLineJoin.Round
            };
            _canvas.Children.Add(nose);

            // Oreille gauche
            DrawEar(cx - R * 1.00, cy + R * 0.08, R * 0.10, R * 0.18, true);
            // Oreille droite
            DrawEar(cx + R * 1.00, cy + R * 0.08, R * 0.10, R * 0.18, false);

            // Croix centrale (Cz de référence)
            double crossSize = R * 0.04;
            AddLine(cx - crossSize, cy, cx + crossSize, cy, Color.FromArgb(80, 120, 140, 180), 1);
            AddLine(cx, cy - crossSize, cx, cy + crossSize, Color.FromArgb(80, 120, 140, 180), 1);

            // Label orientation
            var lbl = new TextBlock
            {
                Text = "AVANT",
                Foreground = new SolidColorBrush(Color.FromArgb(80, 100, 120, 160)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Canvas.SetLeft(lbl, cx - 18);
            Canvas.SetTop(lbl,  cy - R * 1.22);
            _canvas.Children.Add(lbl);
        }

        private void DrawEar(double x, double y, double rx, double ry, bool left)
        {
            var ear = new Ellipse
            {
                Width  = rx * 2,
                Height = ry * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(120, 140, 180)),
                StrokeThickness = 1.8,
                Fill = new SolidColorBrush(BgColor)
            };
            Canvas.SetLeft(ear, x - rx);
            Canvas.SetTop(ear,  y - ry);
            _canvas.Children.Add(ear);
        }

        // ── Électrodes ────────────────────────────────────────────────────────

        private void DrawElectrodes(double cx, double cy, double R)
        {
            bool showLabels = _chkLabels?.IsChecked == true;

            for (int ch = 0; ch < _channelCount; ch++)
            {
                string name = ChannelNames.GetChannelName(ch, _channelCount);
                if (!BrainZoneController.ElectrodePositions.TryGetValue(name, out var pos3))
                    continue;

                // Projection axiale : X→X, Y inversé→Y
                double ex = pos3.X;
                double ey = -pos3.Y;

                double px = cx + ex * R;
                double py = cy + ey * R;

                double power = NormPower(ch);
                double pulse = 1.0 + 0.28 * Math.Sin(_pulsePhase + ch * 0.42) * power;
                double dotR  = (4.5 + power * 5.0) * pulse;

                var col = HeatColor(power);

                // Halo pour électrodes actives
                if (power > 0.25)
                {
                    double hr = dotR * 2.6;
                    var halo = new Ellipse
                    {
                        Width  = hr * 2,
                        Height = hr * 2,
                        Fill   = new RadialGradientBrush(
                            Color.FromArgb((byte)(70 * power), col.R, col.G, col.B),
                            Colors.Transparent)
                    };
                    Canvas.SetLeft(halo, px - hr);
                    Canvas.SetTop(halo,  py - hr);
                    _canvas.Children.Add(halo);
                }

                // Disque électrode
                var dot = new Ellipse
                {
                    Width  = dotR * 2,
                    Height = dotR * 2,
                    Fill   = new SolidColorBrush(col),
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 220, 220, 240)),
                    StrokeThickness = 1.0
                };
                Canvas.SetLeft(dot, px - dotR);
                Canvas.SetTop(dot,  py - dotR);
                _canvas.Children.Add(dot);

                // Label
                if (showLabels)
                {
                    var lbl = new TextBlock
                    {
                        Text       = name,
                        Foreground = new SolidColorBrush(Color.FromArgb(220, col.R, col.G, col.B)),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize   = 7.5,
                        FontWeight = FontWeights.Bold
                    };
                    Canvas.SetLeft(lbl, px + dotR + 1.5);
                    Canvas.SetTop(lbl,  py - 6);
                    _canvas.Children.Add(lbl);
                }
            }
        }

        // ── Overlay infos ─────────────────────────────────────────────────────

        private void DrawOverlay(double W, double H)
        {
            var info = new TextBlock
            {
                Text       = $"{_channelCount} canaux  •  {_sampleRate} Hz  •  Bande : {SelectedBand}",
                Foreground = new SolidColorBrush(Color.FromArgb(100, 100, 116, 139)),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 8
            };
            Canvas.SetLeft(info, 6);
            Canvas.SetTop(info,  H - 16);
            _canvas.Children.Add(info);
        }

        // ── Heatmap grid init ─────────────────────────────────────────────────

        private void BuildHeatmapGrid()
        {
            // La grille est construite dynamiquement dans DrawHeatmap — rien à pré-créer
        }

        // ════════════════════════════════════════════════════════════════════
        // CONSTRUCTION UI
        // ════════════════════════════════════════════════════════════════════

        private UIElement BuildOptionsBar()
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            sp.Children.Add(new TextBlock
            {
                Text = "⬡ TOPOMAP EEG",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            });

            _chkLabels = Ck("Labels", true);
            _chkHeatmap = Ck("Heatmap", true);
            _chkContour = Ck("Contour", true);
            sp.Children.Add(_chkLabels);
            sp.Children.Add(_chkHeatmap);
            sp.Children.Add(_chkContour);

            _lblBand = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(140, 100, 116, 139)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(_lblBand);

            return sp;
        }

        private UIElement BuildLegendBar()
        {
            var g = new Grid { Background = new SolidColorBrush(Color.FromRgb(10, 14, 26)) };
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            g.Children.Add(sp);

            sp.Children.Add(new TextBlock { Text = "Faible", Foreground = new SolidColorBrush(Color.FromRgb(0, 80, 220)), FontFamily = new FontFamily("Consolas"), FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });

            var grad = new Rectangle { Width = 120, Height = 10, Margin = new Thickness(0, 0, 5, 0) };
            var lb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(0,   80, 220), 0.00));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(0,  200, 255), 0.25));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(0,  255, 100), 0.50));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(255, 200,   0), 0.75));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(255,  40,   0), 1.00));
            grad.Fill = lb;
            sp.Children.Add(grad);

            sp.Children.Add(new TextBlock { Text = "Élevée", Foreground = new SolidColorBrush(Color.FromRgb(255, 60, 0)), FontFamily = new FontFamily("Consolas"), FontSize = 8, VerticalAlignment = VerticalAlignment.Center });

            return g;
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════

        private void AddLine(double x1, double y1, double x2, double y2, Color col, double thick)
        {
            _canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush(col),
                StrokeThickness = thick
            });
        }

        private static CheckBox Ck(string t, bool v) => new()
        {
            Content = t, IsChecked = v,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        private static Color HeatColor(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t switch
            {
                <= 0.25 => Lerp(Color.FromRgb(0,  80, 220), Color.FromRgb(0,  200, 255), t / 0.25),
                <= 0.50 => Lerp(Color.FromRgb(0, 200, 255), Color.FromRgb(0,  255, 100), (t-0.25)/0.25),
                <= 0.75 => Lerp(Color.FromRgb(0, 255, 100), Color.FromRgb(255, 200,   0), (t-0.50)/0.25),
                _       => Lerp(Color.FromRgb(255, 200,   0), Color.FromRgb(255,  40,   0), (t-0.75)/0.25),
            };
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }
    }
}
