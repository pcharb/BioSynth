using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using HelixToolkit.Wpf;

namespace BioSynth
{
    /// <summary>
    /// Visualisation 3D — charge Brain_Model.obj (Brain_Part_01..06).
    /// Mapping anatomique :
    ///   Brain_Part_04 → Hémisphère DROIT   (6 zones EEG)
    ///   Brain_Part_06 → Hémisphère GAUCHE  (6 zones EEG)
    ///   Brain_Part_02 → Cervelet
    ///   Brain_Part_05 → Corps calleux / structures internes
    ///   Brain_Part_03 → Tronc cérébral
    ///   Brain_Part_01 → Fissure / membrane
    ///
    /// Électrodes placées en coordonnées brain-space :
    ///   brainX =  elecX * 1.04
    ///   brainY =  elecZ * 0.832
    ///   brainZ = -elecY * 0.852
    /// </summary>
    public class BrainView3DWindow : Window
    {
        // ── Dépendances ───────────────────────────────────────────────────────
        private readonly BrainZoneController _zones;
        private int _channelCount;

        // ── Scène ─────────────────────────────────────────────────────────────
        private readonly HelixViewport3D _viewport;
        private readonly ModelVisual3D   _brainRoot     = new();
        private readonly ModelVisual3D   _electrodeRoot = new();
        private readonly ModelVisual3D   _labelRoot     = new();

        // ── Matériaux des lobes (pour heatmap temps réel) ─────────────────────
        // clé = nom de partie, valeur = DiffuseMaterial modifiable
        private readonly Dictionary<string, DiffuseMaterial>  _lobeMats  = new();
        // Matériaux émissifs pour halo de chaleur
        private readonly Dictionary<string, EmissiveMaterial> _lobeMatsE = new();

        // ── Électrodes ────────────────────────────────────────────────────────
        private readonly List<SphereVisual3D>        _spheres = new();
        private readonly List<BillboardTextVisual3D> _labels  = new();

        // ── Animation ─────────────────────────────────────────────────────────
        private readonly DispatcherTimer _timer;
        private double _pulsePhase = 0;

        // ── UI ────────────────────────────────────────────────────────────────
        private ComboBox _cbBand    = null!;
        private CheckBox _chkLabels = null!;
        private CheckBox _chkHeat   = null!;

        // ── Couleurs anatomiques des 6 parties ────────────────────────────────
        // Basé sur l'analyse du mesh : Part04=hémisphère droit, Part06=gauche,
        // Part02=cervelet, Part03=tronc, Part05=corps calleux, Part01=fissure
        private static readonly Dictionary<string, (Color baseColor, BrainRegion[] regions)> PartMapping
            = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Brain_Part_04"] = (Color.FromRgb(200, 152, 164), new[] {
                BrainRegion.Frontal, BrainRegion.Central, BrainRegion.Parietal,
                BrainRegion.Temporal, BrainRegion.Occipital, BrainRegion.FrontoCentral }),
            ["Brain_Part_06"] = (Color.FromRgb(195, 148, 160), new[] {
                BrainRegion.Frontal, BrainRegion.Central, BrainRegion.Parietal,
                BrainRegion.Temporal, BrainRegion.Occipital, BrainRegion.FrontoCentral }),
            ["Brain_Part_02"] = (Color.FromRgb(158, 120, 135), new[] { BrainRegion.Occipital }),
            ["Brain_Part_05"] = (Color.FromRgb(228, 218, 198), new[] { BrainRegion.Central }),
            ["Brain_Part_03"] = (Color.FromRgb(140, 104, 116), new[] { BrainRegion.Central }),
            ["Brain_Part_01"] = (Color.FromRgb(75,  52,  62),  new[] { BrainRegion.Central }),
        };

        private static readonly Color BgColor = Color.FromRgb(5, 8, 18);

        // ═════════════════════════════════════════════════════════════════════
        public BrainView3DWindow(BrainZoneController zones, int channelCount)
        {
            _zones        = zones;
            _channelCount = channelCount;

            Title  = "Cerveau 3D — Activité EEG";
            Width  = 960; Height = 800;
            Background = new SolidColorBrush(BgColor);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            Content = root;

            // Barre
            var topBar = new Border { Background = new SolidColorBrush(Color.FromRgb(11, 16, 28)) };
            topBar.Child = BuildTopBar();
            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // ── HelixViewport3D ───────────────────────────────────────────────
            _viewport = new HelixViewport3D
            {
                Background           = new SolidColorBrush(BgColor),
                ShowCoordinateSystem = false,
                ShowViewCube         = true,
                IsHeadLightEnabled   = true,
                ModelUpDirection     = new Vector3D(0, 1, 0),
                CameraRotationMode   = CameraRotationMode.Trackball,
            };

            // Lumières : principale + ambiante + contre-jour
            _viewport.Children.Add(new SunLight());
            var ambVis = new ModelVisual3D();
            ambVis.Content = new AmbientLight(Color.FromRgb(55, 45, 65));
            _viewport.Children.Add(ambVis);
            var fillVis = new ModelVisual3D();
            fillVis.Content = new DirectionalLight(Color.FromRgb(60, 70, 100), new Vector3D(1, -0.3, 0.5));
            _viewport.Children.Add(fillVis);

            // Caméra : vue de face légèrement au-dessus
            _viewport.Camera = new PerspectiveCamera
            {
                Position      = new Point3D(0, 0.15, 3.2),
                LookDirection = new Vector3D(0, -0.05, -1),
                UpDirection   = new Vector3D(0, 1, 0),
                FieldOfView   = 42,
                NearPlaneDistance = 0.05,
                FarPlaneDistance  = 40
            };

            _viewport.Children.Add(_brainRoot);
            _viewport.Children.Add(_electrodeRoot);
            _viewport.Children.Add(_labelRoot);

            Grid.SetRow(_viewport, 1);
            root.Children.Add(_viewport);

            // Légende
            var legend = BuildLegend();
            Grid.SetRow(legend, 2);
            root.Children.Add(legend);

            // ── Charger le cerveau ────────────────────────────────────────────
            LoadBrain();
            BuildElectrodes();

            // ── Timer 20 fps ──────────────────────────────────────────────────
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _timer.Tick += (s, e) =>
            {
                _pulsePhase += 0.08;
                UpdateHeatmap();
                UpdateElectrodes();
            };
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        // ═════════════════════════════════════════════════════════════════════
        // CHARGEMENT DU BRAIN ASSET
        // ═════════════════════════════════════════════════════════════════════

        private void LoadBrain()
        {
            _brainRoot.Children.Clear();
            _lobeMats.Clear();

            // Chercher le OBJ
            var candidates = new[]
            {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "brain.obj"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brain.obj"),
                "Assets/brain.obj",
                "brain.obj",
            };

            string? objPath = candidates.FirstOrDefault(System.IO.File.Exists);

            if (objPath != null)
            {
                var result = BrainObjLoader.Load(objPath, buildPerPartMaterials: true);
                if (result.Success && result.Model != null)
                {
                    // Appliquer les couleurs anatomiques et collecter les DiffuseMaterial
                    ApplyAnatomyColors(result.Model);

                    // Le cerveau est en Y=[0, 1.12], centrer en Y=0
                    // Scale=1.52 → Y normalisé [-0.85, +0.85]
                    var tf = new Transform3DGroup();
                    tf.Children.Add(new TranslateTransform3D(0, -0.5625, 0));
                    tf.Children.Add(new ScaleTransform3D(1.52, 1.52, 1.52));
                    result.Model.Transform = tf;

                    _brainRoot.Children.Add(new ModelVisual3D { Content = result.Model });
                    return;
                }
            }

            // Fallback si pas de fichier
            AddInfoLabel("brain.obj introuvable — placer dans Assets/brain.obj");
        }

        private void ApplyAnatomyColors(Model3DGroup group)
        {
            // Le BrainObjLoader a mis le nom de groupe dans le tag via son indexation
            // On re-parcourt en appliquant les couleurs par index de partie
            int partIdx = 0;
            var partNames = new[] { "Brain_Part_01","Brain_Part_02","Brain_Part_03",
                                    "Brain_Part_04","Brain_Part_05","Brain_Part_06" };

            foreach (var model in group.Children)
            {
                if (model is not GeometryModel3D gm) continue;

                string partName = partIdx < partNames.Length ? partNames[partIdx] : "Brain_Part_01";
                partIdx++;

                Color baseColor;
                if (PartMapping.TryGetValue(partName, out var info))
                    baseColor = info.baseColor;
                else
                    baseColor = Color.FromRgb(190, 148, 158);

                // Charger texture si disponible, sinon couleur unie
                var diffMat  = new DiffuseMaterial(new SolidColorBrush(baseColor));
                var emisMat  = new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)));
                _lobeMats [partName] = diffMat;
                _lobeMatsE[partName] = emisMat;

                var matGroup = new MaterialGroup();
                matGroup.Children.Add(diffMat);
                matGroup.Children.Add(emisMat);
                if (partName != "Brain_Part_01")
                    matGroup.Children.Add(new SpecularMaterial(
                        new SolidColorBrush(Color.FromArgb(50, 255, 240, 245)), 28));

                gm.Material     = matGroup;
                gm.BackMaterial = matGroup;
            }

            // Texture désactivée : bloque les mises à jour de couleur heatmap
            // TryApplyTexture(group, partNames);
        }

        private void TryApplyTexture(Model3DGroup group, string[] partNames)
        {
            // Chercher brain_tex.jpg dans Assets/
            var texCandidates = new[]
            {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "brain_tex.jpg"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brain_tex.jpg"),
                "Assets/brain_tex.jpg",
            };

            string? texPath = texCandidates.FirstOrDefault(System.IO.File.Exists);
            if (texPath == null) return;

            try
            {
                var bitmap = new BitmapImage(new Uri(texPath, UriKind.RelativeOrAbsolute));
                var imgBrush = new ImageBrush(bitmap) { TileMode = TileMode.Tile };

                // Appliquer la texture uniquement sur les 2 hémisphères (Part_04 et Part_06)
                int partIdx = 0;
                foreach (var model in group.Children)
                {
                    if (model is not GeometryModel3D gm) { partIdx++; continue; }
                    string partName = partIdx < partNames.Length ? partNames[partIdx] : "";
                    partIdx++;

                    if (partName == "Brain_Part_04" || partName == "Brain_Part_06")
                    {
                        // Garder le DiffuseMaterial mais remplacer sa brush par la texture
                        if (_lobeMats.TryGetValue(partName, out var dm))
                        {
                            dm.Brush = imgBrush;
                        }
                    }
                }
            }
            catch { /* texture non critique */ }
        }

        private void AddInfoLabel(string text)
        {
            var vis = new BillboardTextVisual3D
            {
                Text       = text,
                Position   = new Point3D(0, -1.2, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 60)),
                FontSize   = 10,
            };
            _brainRoot.Children.Add(vis);
        }

        // ═════════════════════════════════════════════════════════════════════
        // HEATMAP — coloration lobes par activité EEG
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateHeatmap()
        {
            double pulse = 0.92 + 0.08 * Math.Sin(_pulsePhase * 0.5);

            foreach (var (partName, dm) in _lobeMats)
            {
                if (!PartMapping.TryGetValue(partName, out var info)) continue;

                // Calcul activité de la zone
                double act = 0;
                foreach (var region in info.regions)
                    act += _zones.Zone(region).Activation;
                act = (act / info.regions.Length) * pulse;

                if (_chkHeat?.IsChecked == true)
                {
                    // Diffuse : mélange anatomique → thermique
                    var heatCol  = HeatColor(act);
                    double blend = act * 0.60;
                    var    c     = info.baseColor;
                    byte   r     = (byte)(c.R * (1 - blend) + heatCol.R * blend);
                    byte   gg    = (byte)(c.G * (1 - blend) + heatCol.G * blend);
                    byte   b     = (byte)(c.B * (1 - blend) + heatCol.B * blend);
                    dm.Brush = new SolidColorBrush(Color.FromRgb(r, gg, b));

                    // Emissif : halo de chaleur additionnel (visible même dans les zones sombres)
                    if (_lobeMatsE.TryGetValue(partName, out var em))
                    {
                        byte ea = (byte)(act * 90);   // max alpha 90/255
                        em.Brush = new SolidColorBrush(Color.FromArgb(ea, heatCol.R, heatCol.G, heatCol.B));
                    }
                }
                else
                {
                    // Réinitialiser
                    dm.Brush = new SolidColorBrush(info.baseColor);
                    if (_lobeMatsE.TryGetValue(partName, out var em))
                        em.Brush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // ÉLECTRODES — alignées sur le brain asset
        // ═════════════════════════════════════════════════════════════════════

        private void BuildElectrodes()
        {
            _electrodeRoot.Children.Clear();
            _labelRoot.Children.Clear();
            _spheres.Clear();
            _labels.Clear();

            bool showLabels = _chkLabels?.IsChecked == true;

            for (int ch = 0; ch < _channelCount; ch++)
            {
                string name = ChannelNames.GetChannelName(ch, _channelCount);
                if (!BrainZoneController.ElectrodePositions.TryGetValue(name, out var p))
                    continue;

                // Transformation vers l'espace du brain asset normalisé :
                // brainX =  p.X * 1.04
                // brainY =  p.Z * 0.832   (Z élec = haut → Y brain)
                // brainZ = -p.Y * 0.852   (Y élec = avant(-) → Z brain avant(+))
                double bx =  p.X * 1.04;
                double by =  p.Z * 0.832;
                double bz = -p.Y * 0.852;

                var sphere = new SphereVisual3D
                {
                    Center   = new Point3D(bx, by, bz),
                    Radius   = 0.048,
                    Fill     = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                    PhiDiv   = 8,
                    ThetaDiv = 8,
                };
                _spheres.Add(sphere);
                _electrodeRoot.Children.Add(sphere);

                if (showLabels)
                {
                    var lbl = new BillboardTextVisual3D
                    {
                        Text       = name,
                        Position   = new Point3D(bx * 1.14, by * 1.10 + 0.02, bz * 1.14),
                        Foreground = new SolidColorBrush(Color.FromRgb(200, 230, 255)),
                        FontSize   = 8,
                    };
                    _labels.Add(lbl);
                    _labelRoot.Children.Add(lbl);
                }
            }
        }

        private void UpdateElectrodes()
        {
            int idx = 0;
            for (int ch = 0; ch < _channelCount && idx < _spheres.Count; ch++)
            {
                string name = ChannelNames.GetChannelName(ch, _channelCount);
                if (!BrainZoneController.ElectrodePositions.ContainsKey(name)) continue;

                // Puissance réelle × amplification forte (6x)
                // + puissance minimale de 0.15 pour que les électrodes soient
                //   toujours visibles même si l'EEG n'est pas encore démarré
                double rawPower = _zones.GetChannelPower(ch);
                double power    = Math.Clamp(rawPower * 6.0 + 0.15, 0.15, 1.0);

                // Zone d'appartenance → ajouter un offset de couleur par région
                var region   = BrainZoneController.RegionOf(name);
                double zoneAct = _zones.Zone(region).Activation;
                // Combiner puissance EEG + activation de zone
                double display = Math.Clamp(power * 0.7 + zoneAct * 0.3, 0, 1);

                // Pulsation plus prononcée
                double pulse = 1.0 + 0.30 * Math.Sin(_pulsePhase + ch * 0.45);
                double radius = (0.038 + display * 0.045) * pulse;

                _spheres[idx].Radius = radius;
                _spheres[idx].Fill   = new SolidColorBrush(HeatColor(display));
                idx++;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private static Color HeatColor(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t switch
            {
                <= 0.25 => Lerp(Color.FromRgb(0,  80, 220), Color.FromRgb(0, 200, 255), t/0.25),
                <= 0.50 => Lerp(Color.FromRgb(0, 200, 255), Color.FromRgb(0, 255, 100), (t-0.25)/0.25),
                <= 0.75 => Lerp(Color.FromRgb(0, 255, 100), Color.FromRgb(255, 200,  0), (t-0.50)/0.25),
                _       => Lerp(Color.FromRgb(255, 200, 0),  Color.FromRgb(255,  40,  0), (t-0.75)/0.25),
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

        // ═════════════════════════════════════════════════════════════════════
        // UI
        // ═════════════════════════════════════════════════════════════════════

        private UIElement BuildTopBar()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };

            sp.Children.Add(new TextBlock { Text = "🧠 CERVEAU 3D — EEG", Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136)), FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) });

            sp.Children.Add(Lbl("Bande :"));
            _cbBand = new ComboBox { Width = 82, Margin = new Thickness(4, 0, 16, 0) };
            foreach (var b in new[] { "Delta", "Theta", "Alpha", "Beta", "Gamma", "Total" }) _cbBand.Items.Add(b);
            _cbBand.SelectedIndex = 2;
            sp.Children.Add(_cbBand);

            _chkLabels = Ck("Labels", true);
            _chkLabels.Checked   += (s, e) => BuildElectrodes();
            _chkLabels.Unchecked += (s, e) => BuildElectrodes();
            sp.Children.Add(_chkLabels);

            _chkHeat = Ck("Heatmap", true);
            sp.Children.Add(_chkHeat);

            sp.Children.Add(new TextBlock { Text = "   Gauche: orbite  |  Droit: pan  |  Molette: zoom", Foreground = new SolidColorBrush(Color.FromArgb(100, 100, 116, 139)), FontFamily = new FontFamily("Consolas"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        private UIElement BuildLegend()
        {
            var g = new Grid { Background = new SolidColorBrush(Color.FromRgb(8, 11, 22)) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            g.Children.Add(sp);

            sp.Children.Add(new TextBlock { Text = "Activité faible", Foreground = new SolidColorBrush(Color.FromRgb(0, 80, 220)), FontFamily = new FontFamily("Consolas"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var grad = new Rectangle { Width = 150, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
            var lb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(0,   80, 220), 0.00));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(0,  200, 255), 0.25));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(0,  255, 100), 0.50));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(255, 200,   0), 0.75));
            lb.GradientStops.Add(new GradientStop(Color.FromRgb(255,  40,   0), 1.00));
            grad.Fill = lb;
            sp.Children.Add(grad);
            sp.Children.Add(new TextBlock { Text = "Activité élevée", Foreground = new SolidColorBrush(Color.FromRgb(255, 60, 0)), FontFamily = new FontFamily("Consolas"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 30, 0) });

            foreach (var (n, r2, g2, b2) in new[] {
                ("Hémisphère G",   195, 148, 160),
                ("Hémisphère D",   200, 152, 164),
                ("Cervelet",       158, 120, 135),
                ("Corps calleux",  228, 218, 198),
                ("Tronc",          140, 104, 116) })
            {
                sp.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb((byte)r2, (byte)g2, (byte)b2)), Margin = new Thickness(0, 0, 4, 0) });
                sp.Children.Add(new TextBlock { Text = n, Foreground = new SolidColorBrush(Color.FromRgb((byte)r2, (byte)g2, (byte)b2)), FontFamily = new FontFamily("Consolas"), FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            }
            return g;
        }

        private static TextBlock Lbl(string t) => new() { Text = t, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), FontFamily = new FontFamily("Consolas"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        private static CheckBox  Ck(string t, bool v) => new() { Content = t, IsChecked = v, Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };

        public void UpdateChannelCount(int n) { _channelCount = n; BuildElectrodes(); }
    }
}
