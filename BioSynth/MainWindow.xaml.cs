using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BioSynth
{
    public partial class MainWindow : Window
    {
        // ── EEG ────────────────────────────────────────────────────────────────
        private EEGGenerator? _generator;
        private bool          _eegRunning      = false;
        private int           _channelCount    = 8;
        private int           _displayChannels = 4;
        private int           _sampleRate      = 256;

        private readonly ConcurrentQueue<EEGSample> _eegQueue = new();
        private const int MAX_QUEUE   = 2048;
        private const int BUFFER_SIZE = 512;

        private readonly List<double[]>  _chanBuffers  = new();
        private readonly List<int>       _chanHead     = new();
        private readonly List<Canvas>    _chanCanvases = new();
        private readonly List<Polyline>  _chanPolylines= new();
        private readonly List<TextBlock> _chanLabels   = new();

        // ── Zones cérébrales ─────────────────────────────────────────────────
        private readonly BrainZoneController _brainZones = new();
        private TopoMapWindow?               _topoMapWindow;
        // Buffer puissance par canal pour la vue 3D (mis à jour à 10 Hz)
        private readonly double[]            _channelPowerBuf = new double[64];
        private int                          _powerUpdateTick = 0;

        // ── Eye Tracking ──────────────────────────────────────────────────────
        private EyeTrackingGenerator? _etGenerator;
        private bool                  _etRunning   = false;

        private readonly ConcurrentQueue<EyeSample> _etQueue = new();
        private const int ET_BUFFER = 256;

        private readonly double[] _pupilBufL = new double[ET_BUFFER];
        private readonly double[] _pupilBufR = new double[ET_BUFFER];
        private int                _pupilHead = 0;

        private double _trailX1, _trailY1;
        private double _trailX2, _trailY2;
        private double _trailX3, _trailY3;

        private int    _fixCount   = 0;
        private int    _sacCount   = 0;
        private int    _blinkCount = 0;
        private double _velSum     = 0;
        private int    _velCount   = 0;
        private string _lastEvt    = "fixation";

        // ── Face Tracking ─────────────────────────────────────────────────────
        private FaceTrackingGenerator? _ftGenerator;
        private bool                   _ftRunning = false;

        private readonly ConcurrentQueue<EmotionSample> _ftEmoQueue  = new();
        private readonly ConcurrentQueue<FaceSample>    _ftFaceQueue = new();

        // Couleurs des 7 émotions
        private static readonly Color[] EmotionColors =
        {
            Color.FromRgb(139,  92, 246),  // Neutral  — violet
            Color.FromRgb(251, 191,  36),  // Happy    — jaune
            Color.FromRgb(  0, 212, 255),  // Sad      — cyan
            Color.FromRgb(255,  68,  68),  // Angry    — rouge
            Color.FromRgb(255, 165,   0),  // Surprised— orange
            Color.FromRgb(236,  72, 153),  // Fearful  — rose
            Color.FromRgb( 34, 197,  94),  // Disgusted— vert
        };
        private static readonly string[] EmotionNames =
            { "NEUTRAL","HAPPY","SAD","ANGRY","SURPRISED","FEARFUL","DISGUSTED" };

        // Barres émotion (créées dynamiquement une fois)
        private readonly List<ProgressBar> _emoBars = new();
        private bool _emoBarsBuilt = false;

        // Landmarks dots (recycled list of Ellipses on FaceCanvas)
        private readonly List<Ellipse> _landmarkDots = new();
        private bool _dotsBuilt = false;

        // Stats FT cumulées
        private int    _ftTransCount  = 0;
        private float  _ftArousalSum  = 0;
        private float  _ftValenceSum  = 0;
        private int    _ftStatCount   = 0;
        private EmotionLabel _ftLastEmo = EmotionLabel.Neutral;

        // ── LSL Inlet (source externe IA) ────────────────────────────────────
        private EEGLslInlet?               _lslInlet;
        private bool                       _lslInletRunning = false;
        private List<LslStreamInfo>        _discoveredStreams = new();

        // ── Streams LSL ───────────────────────────────────────────────────────
        private EEGLslStream?          _eegLsl;
        private EyeTrackingLslStream?  _etLsl;
        private FaceTrackingLslStream? _ftLsl;
        private bool                   _lslAvailable = false;

        // ── Replay de données ────────────────────────────────────────────────
        private EEGDataReplay? _replay;
        private bool           _replayRunning = false;

        // ── Timers ────────────────────────────────────────────────────────────
        private readonly DispatcherTimer _renderTimer;
        private readonly DispatcherTimer _statsTimer;

        private static readonly Color[] ChanColors =
        {
            Color.FromRgb(  0, 255, 136),
            Color.FromRgb(  0, 212, 255),
            Color.FromRgb(139,  92, 246),
            Color.FromRgb(251, 191,  36),
            Color.FromRgb(255,  68,  68),
            Color.FromRgb(255, 165,   0),
            Color.FromRgb(236,  72, 153),
            Color.FromRgb( 34, 197,  94),
        };

        // ═════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();

            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _renderTimer.Tick += OnRenderTick;

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += OnStatsTick;

            InitDisplayChannels(4);
            _renderTimer.Start();
            // Tester la disponibilité de liblsl.dll
            _lslAvailable = Lsl.IsAvailable();
            UpdateLslStatus();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Init graphiques EEG
        private void InitDisplayChannels(int count)
        {
            if (EEGPlotsPanel == null) return;
            _displayChannels = count;
            _chanBuffers.Clear(); _chanHead.Clear();
            _chanCanvases.Clear(); _chanPolylines.Clear(); _chanLabels.Clear();
            EEGPlotsPanel.Items.Clear();

            var panel = new StackPanel { Background = Brushes.Transparent };
            for (int i = 0; i < count; i++)
            {
                _chanBuffers.Add(new double[BUFFER_SIZE]);
                _chanHead.Add(0);

                var border = new Border
                {
                    Background      = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(30, 58, 95)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Height          = 88,
                };
                var canvas = new Canvas { Background = Brushes.Transparent };
                border.Child = canvas;
                var col = ChanColors[i % ChanColors.Length];

                var lbl = new TextBlock
                {
                    Text             = ChannelNames.GetChannelName(i, _channelCount),
                    Foreground       = new SolidColorBrush(col),
                    FontFamily       = new FontFamily("Consolas"),
                    FontSize         = 9,
                    FontWeight       = FontWeights.Bold,
                    Margin           = new Thickness(6, 3, 0, 0),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, 0);
                canvas.Children.Add(lbl);
                _chanLabels.Add(lbl);

                canvas.Children.Add(new Line
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(30, 58, 95)),
                    StrokeThickness = 0.5,
                    X1 = 0, Y1 = 44, X2 = 9999, Y2 = 44,
                    IsHitTestVisible = false
                });

                var poly = new Polyline
                {
                    Stroke           = new SolidColorBrush(col),
                    StrokeThickness  = 1.2,
                    StrokeLineJoin   = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                canvas.Children.Add(poly);
                _chanCanvases.Add(canvas);
                _chanPolylines.Add(poly);
                panel.Children.Add(border);
            }
            EEGPlotsPanel.Items.Add(new ContentControl { Content = panel });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Callbacks depuis threads background (queue uniquement, jamais d'UI)
        private void OnEegSample(EEGSample s)
        {
            if (_eegQueue.Count < MAX_QUEUE) _eegQueue.Enqueue(s);
        }

        private void OnEtSample(EyeSample s)
        {
            // Push vers LSL Eye Tracking
            _etLsl?.Push(s);
            if (_etQueue.Count < 500) _etQueue.Enqueue(s);
        }

        private void OnFtFaceSample(FaceSample s)
        {
            // Push vers LSL Face Tracking (pose)
            _ftLsl?.PushFace(s);
            if (_ftFaceQueue.Count < 60) _ftFaceQueue.Enqueue(s);
        }

        private void OnFtEmotionSample(EmotionSample s)
        {
            // Push vers LSL Face Tracking (émotions)
            _ftLsl?.PushEmotion(s);
            if (_ftEmoQueue.Count < 60) _ftEmoQueue.Enqueue(s);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Tick rendu ~30 fps
        private void OnRenderTick(object? sender, EventArgs e)
        {
            DrainEegQueue();
            DrainEtQueue();
            DrainFtQueues();
            RedrawEeg();
            RedrawEt();
            RedrawFt();
            UpdateSpectralBars();
            UpdateStatusDots();
        }

        private void DrainEegQueue()
        {
            int n = 0;
            while (_eegQueue.TryDequeue(out var s) && n < 256)
            {
                for (int ch = 0; ch < Math.Min(s.Channels.Length, _chanBuffers.Count); ch++)
                {
                    int h = _chanHead[ch];
                    _chanBuffers[ch][h] = s.Channels[ch];
                    _chanHead[ch] = (h + 1) % BUFFER_SIZE;
                    // Accumulation RMS par canal pour la vue 3D
                    if (ch < _channelPowerBuf.Length)
                        _channelPowerBuf[ch] = _channelPowerBuf[ch] * 0.95
                                             + Math.Abs(s.Channels[ch]) * 0.05 / 200.0;
                }
                // Push vers LSL EEG
                _eegLsl?.Push(s);
                n++;
            }
            // Pusher vers LSL EEG si ouvert
            // (déjà fait par événement, rien à faire ici)

            // Transmettre la puissance à BrainZoneController et TopoMap tous les ~10 ticks
            if (++_powerUpdateTick >= 10)
            {
                _powerUpdateTick = 0;
                var powerSnapshot = (double[])_channelPowerBuf.Clone();
                _brainZones.UpdateChannelPower(powerSnapshot);
                // Mettre à jour la TopoMap si sa fenêtre est ouverte
                if (_topoMapWindow != null && _topoMapWindow.IsVisible)
                    _topoMapWindow.TopoMap.UpdateAllPower(powerSnapshot);
            }
        }

        private void DrainEtQueue()
        {
            while (_etQueue.TryDequeue(out var s))
            {
                _trailX3 = _trailX2; _trailY3 = _trailY2;
                _trailX2 = _trailX1; _trailY2 = _trailY1;
                _trailX1 = s.GazeXNorm; _trailY1 = s.GazeYNorm;

                _pupilBufL[_pupilHead] = s.PupilLeft;
                _pupilBufR[_pupilHead] = s.PupilRight;
                _pupilHead = (_pupilHead + 1) % ET_BUFFER;

                // Données numériques (on prend le dernier sample de la queue)
                TxtGazeX.Text         = $"{s.GazeX:F0} px";
                TxtGazeY.Text         = $"{s.GazeY:F0} px";
                TxtPupilL.Text        = $"{s.PupilLeft:F2}";
                TxtPupilR.Text        = $"{s.PupilRight:F2}";
                TxtEtVelocity.Text    = $"{s.VelocityDeg:F0}";
                TxtEtEventDetail.Text = s.EventType.ToUpper();
                TxtEtEvent.Text       = s.EventType switch
                {
                    "saccade"      => "SACC",
                    "microsaccade" => "µSACC",
                    "blink"        => "BLINK",
                    _              => "FIX"
                };
                TxtEtEventDetail.Foreground = s.EventType switch
                {
                    "saccade"      => new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                    "microsaccade" => new SolidColorBrush(Color.FromRgb(255, 165,  0)),
                    "blink"        => new SolidColorBrush(Color.FromRgb(236,  72,153)),
                    _              => new SolidColorBrush(Color.FromRgb(  0, 255,136))
                };

                if (s.EventType == "fixation")   _fixCount++;
                else if (s.EventType == "saccade") { _sacCount++; _velSum += s.VelocityDeg; _velCount++; }
                else if (s.EventType == "blink")   _blinkCount++;
                _lastEvt = s.EventType;
            }
        }

        private void RedrawEeg()
        {
            for (int i = 0; i < _chanPolylines.Count; i++)
            {
                var canvas = _chanCanvases[i];
                double w     = canvas.ActualWidth  > 10 ? canvas.ActualWidth  : 800;
                double h     = canvas.ActualHeight > 10 ? canvas.ActualHeight : 88;
                double midY  = h / 2.0;
                double scale = h / 600.0;
                double xStep = w / (BUFFER_SIZE - 1);
                int    head  = _chanHead[i];
                var    buf   = _chanBuffers[i];
                var    pts   = new PointCollection(BUFFER_SIZE);
                for (int j = 0; j < BUFFER_SIZE; j++)
                {
                    int idx = (head + j) % BUFFER_SIZE;
                    pts.Add(new Point(j * xStep, Math.Clamp(midY - buf[idx] * scale, 0, h)));
                }
                _chanPolylines[i].Points = pts;
            }
        }

        private void RedrawEt()
        {
            double mapW = GazeMapCanvas.ActualWidth  > 10 ? GazeMapCanvas.ActualWidth  : 304;
            double mapH = GazeMapCanvas.ActualHeight > 10 ? GazeMapCanvas.ActualHeight : 160;

            void PlaceEllipse(Ellipse el, double nx, double ny)
            {
                Canvas.SetLeft(el, nx * mapW - el.Width  / 2);
                Canvas.SetTop(el,  ny * mapH - el.Height / 2);
            }

            PlaceEllipse(GazeDot,    _trailX1, _trailY1);
            PlaceEllipse(GazeTrail1, _trailX2, _trailY2);
            PlaceEllipse(GazeTrail2, _trailX3, _trailY3);
            PlaceEllipse(GazeTrail3, _trailX3, _trailY3);

            GazeDot.Fill = _lastEvt switch
            {
                "blink"        => new SolidColorBrush(Color.FromRgb(236,  72, 153)),
                "saccade"      => new SolidColorBrush(Color.FromRgb(251, 191,  36)),
                "microsaccade" => new SolidColorBrush(Color.FromRgb(255, 165,   0)),
                _              => new SolidColorBrush(Color.FromRgb(  0, 212, 255))
            };

            double cw = PupilCanvas.ActualWidth  > 10 ? PupilCanvas.ActualWidth  : 288;
            double ch = PupilCanvas.ActualHeight > 10 ? PupilCanvas.ActualHeight : 80;
            const double PMIN = 1.5, PMAX = 6.5;
            var ptsL = new PointCollection(ET_BUFFER);
            var ptsR = new PointCollection(ET_BUFFER);
            double step = cw / (ET_BUFFER - 1);
            for (int j = 0; j < ET_BUFFER; j++)
            {
                int idx = (_pupilHead + j) % ET_BUFFER;
                double nL = 1.0 - (_pupilBufL[idx] - PMIN) / (PMAX - PMIN);
                double nR = 1.0 - (_pupilBufR[idx] - PMIN) / (PMAX - PMIN);
                ptsL.Add(new Point(j * step, Math.Clamp(nL * ch, 0, ch)));
                ptsR.Add(new Point(j * step, Math.Clamp(nR * ch, 0, ch)));
            }
            PupilLineL.Points = ptsL;
            PupilLineR.Points = ptsR;
        }

        private void DrainFtQueues()
        {
            // Émotion
            while (_ftEmoQueue.TryDequeue(out var emo))
            {
                int domIdx = (int)emo.Dominant;
                string name = EmotionNames[domIdx];
                Color  col  = EmotionColors[domIdx];
                var    brush= new SolidColorBrush(col);

                TxtFtEmoLabel.Text       = name;
                TxtFtEmoLabel.Foreground = brush;
                TxtFtArousal.Text        = $"{emo.Arousal:F2}";
                TxtFtValence.Text        = $"{(emo.Valence >= 0 ? "+" : "")}{emo.Valence:F2}";
                FtEmoBadge.BorderBrush   = brush;

                if (emo.Dominant != _ftLastEmo) { _ftTransCount++; _ftLastEmo = emo.Dominant; }
                _ftArousalSum += emo.Arousal; _ftValenceSum += emo.Valence; _ftStatCount++;

                // Mise à jour barres émotion
                EnsureEmoBarsBuilt();
                for (int i = 0; i < 7 && i < _emoBars.Count; i++)
                    _emoBars[i].Value = emo.Scores[i] * 100;

                // Stats bas de page
                TxtFtDomEmo.Text       = name;
                TxtFtDomEmo.Foreground = brush;
            }

            // Landmarks face
            while (_ftFaceQueue.TryDequeue(out var face))
            {
                TxtFtPitch.Text = $"{face.RotPitch:F1}°";
                TxtFtYaw.Text   = $"{face.RotYaw:F1}°";
                TxtFtRoll.Text  = $"{face.RotRoll:F1}°";
                TxtFtZ.Text     = $"{face.PoseZ:F0}mm";
                TxtFtConf.Text  = $"{face.Confidence:P0}";
                // Micro-expression détectée si une AU non-neutre dépasse 0.35
                bool micro = false;
                for (int i = 0; i < 18; i++) if (face.ActionUnits[i] > 0.35f) { micro = true; break; }
                TxtFtMicro.Text = micro ? "OUI" : "NON";

                // Landmarks
                EnsureLandmarkDotsBuilt();
                double cw = FaceCanvas.ActualWidth  > 10 ? FaceCanvas.ActualWidth  : 274;
                double ch = FaceCanvas.ActualHeight > 10 ? FaceCanvas.ActualHeight : 160;
                for (int i = 0; i < 68; i++)
                {
                    double x = face.LandmarkX[i] * cw;
                    double y = face.LandmarkY[i] * ch;
                    Canvas.SetLeft(_landmarkDots[i], x - 1.5);
                    Canvas.SetTop (_landmarkDots[i], y - 1.5);
                }
            }
        }

        private void RedrawFt()
        {
            if (_ftStatCount > 0)
            {
                TxtFtAvgArousal.Text  = $"{_ftArousalSum / _ftStatCount:F2}";
                TxtFtAvgValence.Text  = $"{(_ftValenceSum / _ftStatCount >= 0 ? "+" : "")}{_ftValenceSum / _ftStatCount:F2}";
                TxtFtTransCount.Text  = _ftTransCount.ToString("N0");
            }
        }

        private void EnsureEmoBarsBuilt()
        {
            if (_emoBarsBuilt) return;
            _emoBarsBuilt = true;
            for (int i = 0; i < 7; i++)
            {
                int idx = i; // capture
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock
                {
                    Text       = EmotionNames[i],
                    Foreground = new SolidColorBrush(EmotionColors[i]),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lbl, 0);

                var pb = new ProgressBar
                {
                    Height          = 8,
                    Maximum         = 100,
                    Value           = i == 0 ? 100 : 0,
                    Background      = new SolidColorBrush(Color.FromRgb(26, 32, 53)),
                    Foreground      = new SolidColorBrush(EmotionColors[i]),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(pb, 1);

                row.Children.Add(lbl);
                row.Children.Add(pb);
                _emoBars.Add(pb);
                FtEmoBarsPanel.Items.Add(new ContentControl { Content = row });
            }
        }

        private void EnsureLandmarkDotsBuilt()
        {
            if (_dotsBuilt) return;
            _dotsBuilt = true;

            // Couleurs par région anatomique
            Color RegionColor(int i) => i switch
            {
                <= 16 => Color.FromRgb( 71,  85, 105),  // contour
                <= 26 => Color.FromRgb(251, 191,  36),  // sourcils
                <= 35 => Color.FromRgb(  0, 212, 255),  // nez
                <= 47 => Color.FromRgb(  0, 255, 136),  // yeux
                _     => Color.FromRgb(236,  72, 153),  // bouche
            };

            for (int i = 0; i < 68; i++)
            {
                var dot = new Ellipse
                {
                    Width  = 3,
                    Height = 3,
                    Fill   = new SolidColorBrush(RegionColor(i)),
                    Opacity= 0.85,
                    IsHitTestVisible = false
                };
                FaceCanvas.Children.Add(dot);
                _landmarkDots.Add(dot);
            }
        }

        private void UpdateSpectralBars()
        {
            if (_chanBuffers.Count == 0) return;
            var buf = _chanBuffers[0];
            double sum2 = 0;
            foreach (var v in buf) sum2 += v * v;
            double rms = Math.Sqrt(sum2 / BUFFER_SIZE);
            if (rms < 1) return;
            PbDelta.Value = Math.Min(100, rms * 0.35);
            PbTheta.Value = Math.Min(100, rms * 0.20);
            PbAlpha.Value = Math.Min(100, rms * 0.30);
            PbBeta.Value  = Math.Min(100, rms * 0.10);
            PbGamma.Value = Math.Min(100, rms * 0.05);
        }

        private void UpdateStatusDots()
        {
            bool half = DateTime.Now.Millisecond < 500;
            if (_eegRunning) RecDotEeg.Opacity = half ? 1.0 : 0.15;
            if (_etRunning)  RecDotEt.Opacity  = half ? 1.0 : 0.15;

            TxtEegActiveLabel.Text = _eegRunning ? "OUI" : "NON";
            TxtEegActiveLabel.Foreground = _eegRunning
                ? new SolidColorBrush(Color.FromRgb(0, 255, 136))
                : new SolidColorBrush(Color.FromRgb(255, 68, 68));

            TxtEtActiveLabel.Text = _etRunning ? "OUI" : "NON";
            TxtEtActiveLabel.Foreground = _etRunning
                ? new SolidColorBrush(Color.FromRgb(0, 212, 255))
                : new SolidColorBrush(Color.FromRgb(255, 68, 68));

            TxtFtActiveLabel.Text = _ftRunning ? "OUI" : "NON";
            TxtFtActiveLabel.Foreground = _ftRunning
                ? new SolidColorBrush(Color.FromRgb(139, 92, 246))
                : new SolidColorBrush(Color.FromRgb(255, 68, 68));

            if (_ftRunning) RecDotFt.Opacity = half ? 1.0 : 0.15;

            TxtSyncLabel.Text = (_eegRunning && _etRunning && _ftRunning) ? "SYNC3"
                              : (_eegRunning && _etRunning) || (_eegRunning && _ftRunning) || (_etRunning && _ftRunning) ? "SYNC2"
                              : "—";
            TxtSyncLabel.Foreground = TxtSyncLabel.Text == "SYNC3"
                ? new SolidColorBrush(Color.FromRgb(139, 92, 246))
                : TxtSyncLabel.Text == "SYNC2"
                ? new SolidColorBrush(Color.FromRgb(0, 212, 255))
                : new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }

        // ═════════════════════════════════════════════════════════════════════
        // Timer stats 1 Hz
        private void OnStatsTick(object? sender, EventArgs e)
        {
            if (_generator != null)
            {
                long s = _generator.TotalSamplesGenerated;
                TxtSampleCount.Text = s.ToString("N0");
                TxtDuration.Text    = $"{(_sampleRate > 0 ? s / (double)_sampleRate : 0):F1}s";
            }
            if (_etGenerator != null)
                TxtEtSampleCount.Text = _etGenerator.TotalSamplesGenerated.ToString("N0");

            if (_ftGenerator != null)
                TxtFtSampleCount.Text = _ftGenerator.TotalSamplesGenerated.ToString("N0");

            // Stats replay
            if (_replay != null && _replayRunning)
            {
                TxtReplayFrame.Text   = _replay.CurrentFrame.ToString("N0");
                TxtReplayPct.Text     = $"{_replay.ProgressPct:F1}%";
                if (PbReplayProgress != null) PbReplayProgress.Value = _replay.ProgressPct;
                if (TxtReplayProgress != null) TxtReplayProgress.Text = $"{_replay.ProgressPct:F1}%";
                TxtReplayActiveLabel.Text = "OUI";
                TxtReplayActiveLabel.Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 255));
            }
            else
            {
                TxtReplayActiveLabel.Text = "NON";
                TxtReplayActiveLabel.Foreground = new SolidColorBrush(Color.FromRgb(255, 68, 68));
            }
            TxtFixationCount.Text = _fixCount.ToString("N0");
            TxtSaccadeCount.Text  = _sacCount.ToString("N0");
            TxtBlinkCount.Text    = _blinkCount.ToString("N0");
            TxtAvgVelocity.Text   = _velCount > 0 ? $"{_velSum / _velCount:F0}" : "0";
        }

        // ═════════════════════════════════════════════════════════════════════
        // EEG start / stop
        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_eegRunning) StopEeg(); else StartEeg();
        }

        private void StartEeg()
        {
            // Mode Replay : lecture d'un fichier au lieu de générer
            if (RbReplay?.IsChecked == true)
            {
                StartReplay();
                return;
            }
            // Mode LSL Inlet : recevoir depuis une source externe (IA)
            if (RbLslInlet?.IsChecked == true)
            {
                StartLslInlet();
                return;
            }

            while (_eegQueue.TryDequeue(out _)) { }
            for (int i = 0; i < _chanBuffers.Count; i++)
            {
                Array.Clear(_chanBuffers[i], 0, BUFFER_SIZE);
                _chanHead[i] = 0;
            }
            var cfg = BuildEegConfig();
            _generator = new EEGGenerator(cfg)
            {
                ZoneController = _brainZones
            };
            TxtSourceMode.Text = "GÉNÉRATION";
            // Mettre à jour la fenêtre TopoMap si ouverte
            _topoMapWindow?.Reconfigure(_brainZones, _channelCount, _sampleRate);
            // Ouvrir stream LSL EEG si activé
            OpenEegLsl();
            _generator.SampleGenerated += OnEegSample;
            _generator.StatusChanged   += msg => Dispatcher.BeginInvoke(() => TxtStatus.Text = msg);
            _eegRunning = true;
            _generator.Start();
            _statsTimer.Start();

            BtnStartStop.Content    = "■  ARRÊTER EEG";
            BtnStartStop.Background = new SolidColorBrush(Color.FromRgb(180, 30, 30));
            BtnStartStop.Foreground = Brushes.White;
            TxtRecLabel.Text        = "● EEG REC";
            TxtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(0, 255, 136));
            RecDotEeg.Opacity       = 1;
            SetEegControlsEnabled(false);
        }

        private void StopEeg()
        {
            _eegRunning = false;
            _generator?.Stop();
            _generator  = null;
            // Arrêter le replay si actif
            if (_replayRunning) StopReplay();
            // Arrêter le LSL Inlet si actif
            if (_lslInletRunning) StopLslInlet();
            // Fermer stream LSL EEG
            _eegLsl?.Dispose();
            _eegLsl = null;
            UpdateLslStatus();

            BtnStartStop.Content    = "▶  DÉMARRER EEG";
            BtnStartStop.Background = new SolidColorBrush(Color.FromRgb(0, 196, 106));
            BtnStartStop.Foreground = new SolidColorBrush(Color.FromRgb(10, 14, 26));
            TxtRecLabel.Text        = "EEG IDLE";
            TxtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            RecDotEeg.Opacity       = 0;
            TxtStatus.Text          = "EEG arrêté.";
            SetEegControlsEnabled(true);
            if (!_etRunning) _statsTimer.Stop();
        }

        // ═════════════════════════════════════════════════════════════════════
        // REPLAY start / stop
        private void StartReplay()
        {
            string path = TxtReplayPath?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("Sélectionner un fichier EEG valide avant de démarrer.",
                    "Replay", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            while (_eegQueue.TryDequeue(out _)) { }
            for (int i = 0; i < _chanBuffers.Count; i++)
            {
                Array.Clear(_chanBuffers[i], 0, BUFFER_SIZE);
                _chanHead[i] = 0;
            }

            double speed = 1.0;
            if (CbReplaySpeed.SelectedItem is ComboBoxItem si)
                double.TryParse(si.Tag?.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speed);

            _replay = new EEGDataReplay
            {
                FilePath = path,
                Speed    = speed,
                Loop     = ChkReplayLoop?.IsChecked == true,
            };

            _replay.SampleReady += sample =>
            {
                // Même pipeline que le générateur
                if (_eegQueue.Count < MAX_QUEUE) _eegQueue.Enqueue(sample);
            };

            _replay.StatusChanged += msg => Dispatcher.BeginInvoke(() =>
            {
                TxtStatus.Text = msg;
            });

            _replay.PlaybackFinished += () => Dispatcher.BeginInvoke(() =>
            {
                if (!(_replay?.Loop == true)) StopEeg();
            });

            // Adapter le nombre de canaux et le samplerate à celui du fichier
            _replay.Start();
            _channelCount = _replay.ChannelCount;
            _sampleRate   = _replay.SampleRate;

            // Re-initialiser les buffers d'affichage pour le bon nombre de canaux
            int displayCh = Math.Min(_displayChannels, _channelCount);
            SlDisplayChannels.Maximum = _channelCount;
            InitDisplayChannels(displayCh);
            _topoMapWindow?.Reconfigure(_brainZones, _channelCount, _sampleRate);

            _replayRunning = true;
            _eegRunning    = true;
            _statsTimer.Start();

            BtnStartStop.Content    = "■  ARRÊTER";
            BtnStartStop.Background = new SolidColorBrush(Color.FromRgb(180, 30, 30));
            BtnStartStop.Foreground = Brushes.White;
            TxtRecLabel.Text        = "● REPLAY";
            TxtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(0, 212, 255));
            RecDotEeg.Opacity       = 1;
            TxtSourceMode.Text      = "REPLAY";
            if (BtnReplayPause != null) BtnReplayPause.Visibility = Visibility.Visible;
            SetEegControlsEnabled(false);
        }

        private void StopReplay()
        {
            _replay?.Stop();
            _replay?.Dispose();
            _replay        = null;
            _replayRunning = false;
            if (BtnReplayPause != null) BtnReplayPause.Visibility = Visibility.Collapsed;
        }

        // ═════════════════════════════════════════════════════════════════════
        // ET start / stop
        private void BtnEtStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_etRunning) StopEt(); else StartEt();
        }

        private void StartEt()
        {
            Array.Clear(_pupilBufL, 0, ET_BUFFER);
            Array.Clear(_pupilBufR, 0, ET_BUFFER);
            _pupilHead  = 0;
            _fixCount   = 0; _sacCount   = 0;
            _blinkCount = 0; _velSum     = 0; _velCount = 0;
            while (_etQueue.TryDequeue(out _)) { }

            var cfg = BuildEtConfig();
            _etGenerator = new EyeTrackingGenerator(cfg);
            _etGenerator.SampleGenerated += OnEtSample;
            _etGenerator.StatusChanged   += msg => Dispatcher.BeginInvoke(() => TxtEtStatus.Text = msg);
            OpenEtLsl();
            _etRunning = true;
            _etGenerator.Start();
            _statsTimer.Start();

            BtnEtStartStop.Content    = "■  ARRÊTER ET";
            BtnEtStartStop.Background = new SolidColorBrush(Color.FromRgb(12, 74, 110));
            BtnEtStartStop.Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 255));
            TxtEtRecLabel.Text        = "● ET REC";
            TxtEtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(0, 212, 255));
            RecDotEt.Opacity          = 1;
            SetEtControlsEnabled(false);
        }

        private void StopEt()
        {
            _etRunning = false;
            _etGenerator?.Stop();
            _etGenerator  = null;
            _etLsl?.Dispose(); _etLsl = null;
            UpdateLslStatus();

            BtnEtStartStop.Content    = "▶  DÉMARRER ET";
            BtnEtStartStop.Background = new SolidColorBrush(Color.FromRgb(14, 90, 130));
            BtnEtStartStop.Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 255));
            TxtEtRecLabel.Text        = "ET IDLE";
            TxtEtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            RecDotEt.Opacity          = 0;
            TxtEtStatus.Text          = "ET arrêté.";
            SetEtControlsEnabled(true);
            if (!_eegRunning) _statsTimer.Stop();
        }

        // ═════════════════════════════════════════════════════════════════════
        // FT start / stop
        private void BtnFtStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_ftRunning) StopFt(); else StartFt();
        }

        private void StartFt()
        {
            while (_ftFaceQueue.TryDequeue(out _)) { }
            while (_ftEmoQueue.TryDequeue(out _))  { }
            _ftTransCount = 0; _ftArousalSum = 0; _ftValenceSum = 0; _ftStatCount = 0;
            _ftLastEmo = EmotionLabel.Neutral;

            var cfg = BuildFtConfig();
            _ftGenerator = new FaceTrackingGenerator(cfg);
            _ftGenerator.FaceSampleGenerated    += OnFtFaceSample;
            _ftGenerator.EmotionSampleGenerated += OnFtEmotionSample;
            _ftGenerator.StatusChanged += msg => Dispatcher.BeginInvoke(() => TxtFtStatus.Text = msg);
            OpenFtLsl();
            _ftRunning = true;
            _ftGenerator.Start();
            _statsTimer.Start();

            string modeTxt = cfg.StreamMode == FaceStreamMode.EmotionOnly ? "ÉMOTION" : "FACE";
            BtnFtStartStop.Content    = "■  ARRÊTER FT";
            BtnFtStartStop.Background = new SolidColorBrush(Color.FromRgb(60, 20, 100));
            BtnFtStartStop.Foreground = new SolidColorBrush(Color.FromRgb(139, 92, 246));
            TxtFtRecLabel.Text        = "● FT REC";
            TxtFtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(139, 92, 246));
            TxtFtModeLabel.Text       = modeTxt;
            RecDotFt.Opacity          = 1;
            SetFtControlsEnabled(false);
        }

        private void StopFt()
        {
            _ftRunning = false;
            _ftGenerator?.Stop();
            _ftGenerator  = null;
            _ftLsl?.Dispose(); _ftLsl = null;
            UpdateLslStatus();

            BtnFtStartStop.Content    = "▶  DÉMARRER FT";
            BtnFtStartStop.Background = new SolidColorBrush(Color.FromRgb(59, 31, 106));
            BtnFtStartStop.Foreground = new SolidColorBrush(Color.FromRgb(139, 92, 246));
            TxtFtRecLabel.Text        = "FT IDLE";
            TxtFtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            RecDotFt.Opacity          = 0;
            TxtFtStatus.Text          = "FT arrêté.";
            SetFtControlsEnabled(true);
            if (!_eegRunning && !_etRunning) _statsTimer.Stop();
        }
        private EEGConfig BuildEegConfig()
        {
            _channelCount = int.Parse(((ComboBoxItem)CbChannels.SelectedItem).Tag?.ToString() ?? "8");
            _sampleRate   = int.TryParse(TxtSampleRate.Text, out int sr) && sr > 0 ? sr : 256;
            var outTag = ((ComboBoxItem)CbOutputMode.SelectedItem).Tag?.ToString() ?? "File";
            var output = outTag switch { "TCP" => OutputMode.TcpStream, "UDP" => OutputMode.UdpStream, _ => OutputMode.File };
            var fmtTag = ((ComboBoxItem)CbFormat.SelectedItem).Tag?.ToString() ?? "CSV";
            var format = fmtTag switch { "Binary" => DataFormat.BinaryFloat32, "BDF" => DataFormat.BDF, _ => DataFormat.CSV };
            TxtChannelInfo.Text = $"  {_channelCount}CH · {_sampleRate}Hz";
            return new EEGConfig
            {
                ChannelCount = _channelCount,
                SampleRate   = _sampleRate,
                OutputMode   = output,
                DataFormat   = format,
                FilePath     = TxtFilePath.Text,
                StreamHost   = TxtHost.Text,
                StreamPort   = int.TryParse(TxtPort.Text, out int p) ? p : 9999,
                AddArtifacts = ChkArtifacts.IsChecked ?? true,
                NoiseLevel   = SlNoise.Value
            };
        }

        private EyeTrackingConfig BuildEtConfig()
        {
            var outTag = ((ComboBoxItem)CbEtOutputMode.SelectedItem).Tag?.ToString() ?? "UDP";
            var output = outTag switch { "TCP" => OutputMode.TcpStream, "File" => OutputMode.File, _ => OutputMode.UdpStream };
            int rate = 120;
            if (CbEtSampleRate.SelectedItem is ComboBoxItem ri)
                int.TryParse(ri.Tag?.ToString(), out rate);
            int sw = int.TryParse(TxtScreenW.Text, out int sw2) ? sw2 : 1920;
            int sh = int.TryParse(TxtScreenH.Text, out int sh2) ? sh2 : 1080;
            return new EyeTrackingConfig
            {
                SampleRate            = rate,
                ScreenWidth           = sw,
                ScreenHeight          = sh,
                OutputMode            = output,
                FilePath              = TxtEtFilePath?.Text ?? "eyetracking_data.csv",
                StreamHost            = TxtEtHost?.Text     ?? "127.0.0.1",
                StreamPort            = int.TryParse(TxtEtPort?.Text, out int ep) ? ep : 9998,
                SimulateSaccades      = ChkEtSaccades.IsChecked ?? true,
                SimulateBlinks        = ChkEtBlinks.IsChecked   ?? true,
                SimulatePupilDilation = ChkEtPupil.IsChecked    ?? true,
                NoiseLevel            = SlEtNoise.Value
            };
        }

        private FaceTrackingConfig BuildFtConfig()
        {
            var outTag = ((ComboBoxItem)CbFtOutputMode.SelectedItem).Tag?.ToString() ?? "UDP";
            var output = outTag switch { "TCP" => OutputMode.TcpStream, "File" => OutputMode.File, _ => OutputMode.UdpStream };

            var modeTag = ((ComboBoxItem)CbFtMode.SelectedItem).Tag?.ToString() ?? "Face";
            var mode    = modeTag == "Emotion" ? FaceStreamMode.EmotionOnly : FaceStreamMode.FaceLandmarks;

            int rate = 30;
            if (CbFtSampleRate.SelectedItem is ComboBoxItem ri)
                int.TryParse(ri.Tag?.ToString(), out rate);

            return new FaceTrackingConfig
            {
                SampleRate       = rate,
                StreamMode       = mode,
                OutputMode       = output,
                FilePath         = TxtFtFilePath?.Text  ?? "face_data.csv",
                StreamHost       = TxtFtHost?.Text      ?? "127.0.0.1",
                StreamPort       = int.TryParse(TxtFtPort?.Text, out int fp) ? fp : 9997,
                NoiseLevel       = SlFtNoise.Value,
                SimulateMicro    = ChkFtMicro.IsChecked    ?? true,
                SimulateHeadMove = ChkFtHeadMove.IsChecked ?? true,
                EmotionChangeSec = SlFtEmoDur.Value,
            };
        }
        private void SetEegControlsEnabled(bool e)
        {
            CbChannels.IsEnabled    = e; TxtSampleRate.IsEnabled = e;
            CbOutputMode.IsEnabled  = e; CbFormat.IsEnabled      = e;
            TxtFilePath.IsEnabled   = e; TxtHost.IsEnabled        = e;
            TxtPort.IsEnabled       = e; SlNoise.IsEnabled        = e;
            ChkArtifacts.IsEnabled  = e;
        }

        private void SetEtControlsEnabled(bool e)
        {
            CbEtSampleRate.IsEnabled = e; CbEtOutputMode.IsEnabled = e;
            TxtScreenW.IsEnabled     = e; TxtScreenH.IsEnabled      = e;
            SlEtNoise.IsEnabled      = e; ChkEtSaccades.IsEnabled   = e;
            ChkEtBlinks.IsEnabled    = e; ChkEtPupil.IsEnabled      = e;
            if (TxtEtHost     != null) TxtEtHost.IsEnabled     = e;
            if (TxtEtPort     != null) TxtEtPort.IsEnabled     = e;
            if (TxtEtFilePath != null) TxtEtFilePath.IsEnabled = e;
        }

        private void SetFtControlsEnabled(bool e)
        {
            CbFtMode.IsEnabled       = e; CbFtSampleRate.IsEnabled = e;
            CbFtOutputMode.IsEnabled = e; SlFtNoise.IsEnabled      = e;
            SlFtEmoDur.IsEnabled     = e; ChkFtMicro.IsEnabled     = e;
            ChkFtHeadMove.IsEnabled  = e;
            if (TxtFtHost     != null) TxtFtHost.IsEnabled     = e;
            if (TxtFtPort     != null) TxtFtPort.IsEnabled     = e;
            if (TxtFtFilePath != null) TxtFtFilePath.IsEnabled = e;
        }
        private void CbChannels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EEGPlotsPanel == null) return;
            if (CbChannels.SelectedItem is not ComboBoxItem item) return;
            _channelCount = int.Parse(item.Tag?.ToString() ?? "8");
            SlDisplayChannels.Maximum = _channelCount;
            if (SlDisplayChannels.Value > _channelCount) SlDisplayChannels.Value = _channelCount;
            InitDisplayChannels(Math.Min(_displayChannels, _channelCount));
            TxtChannelInfo?.SetCurrentValue(TextBlock.TextProperty, $"  {_channelCount}CH · {_sampleRate}Hz");
            // Synchroniser la TopoMap avec le nouveau nombre de canaux
            _topoMapWindow?.Reconfigure(_brainZones, _channelCount, _sampleRate);
        }

        private void CbOutputMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PanelFile == null || PanelStream == null) return;
            if (CbOutputMode.SelectedItem is not ComboBoxItem item) return;
            bool isFile = item.Tag?.ToString() == "File";
            PanelFile.Visibility   = isFile ? Visibility.Visible   : Visibility.Collapsed;
            PanelStream.Visibility = isFile ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SlNoise_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtNoiseVal != null) TxtNoiseVal.Text = SlNoise.Value.ToString("F2");
        }

        private void SlDisplayChannels_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (EEGPlotsPanel == null) return;
            int val = (int)SlDisplayChannels.Value;
            if (TxtDisplayChannels != null)
                TxtDisplayChannels.Text = $"{val} canal{(val > 1 ? "x" : "")} affiché{(val > 1 ? "s" : "")}";
            if (!_eegRunning) InitDisplayChannels(Math.Min(val, _channelCount));
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|Binaire (*.bin)|*.bin|BDF (*.bdf)|*.bdf", FileName = "eeg_data.csv" };
            if (dlg.ShowDialog() == true) TxtFilePath.Text = dlg.FileName;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Handlers UI — Eye Tracking
        private void CbEtSampleRate_Changed(object sender, SelectionChangedEventArgs e) { }

        private void CbEtOutputMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PanelEtFile == null || PanelEtStream == null) return;
            if (CbEtOutputMode.SelectedItem is not ComboBoxItem item) return;
            bool isFile = item.Tag?.ToString() == "File";
            PanelEtFile.Visibility   = isFile ? Visibility.Visible   : Visibility.Collapsed;
            PanelEtStream.Visibility = isFile ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SlEtNoise_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtEtNoiseVal != null) TxtEtNoiseVal.Text = SlEtNoise.Value.ToString("F2");
        }

        private void BtnBrowseEt_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|Binaire (*.bin)|*.bin", FileName = "eyetracking_data.csv" };
            if (dlg.ShowDialog() == true && TxtEtFilePath != null) TxtEtFilePath.Text = dlg.FileName;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Handlers UI — Face Tracking
        private void CbFtMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (TxtFtModeLabel == null) return;
            var tag = (CbFtMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Face";
            TxtFtModeLabel.Text = tag == "Emotion" ? "ÉMO" : "FACE";
        }

        private void CbFtOutputMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PanelFtFile == null || PanelFtStream == null) return;
            if (CbFtOutputMode.SelectedItem is not ComboBoxItem item) return;
            bool isFile = item.Tag?.ToString() == "File";
            PanelFtFile.Visibility   = isFile ? Visibility.Visible   : Visibility.Collapsed;
            PanelFtStream.Visibility = isFile ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SlFtNoise_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtFtNoiseVal != null) TxtFtNoiseVal.Text = SlFtNoise.Value.ToString("F2");
        }

        private void SlFtEmoDur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtFtEmoDurVal != null) TxtFtEmoDurVal.Text = SlFtEmoDur.Value.ToString("F1");
        }

        private void BtnBrowseFt_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter   = "CSV (*.csv)|*.csv|Binaire (*.bin)|*.bin",
                FileName = "face_data.csv"
            };
            if (dlg.ShowDialog() == true && TxtFtFilePath != null)
                TxtFtFilePath.Text = dlg.FileName;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Handlers — Zones cérébrales + Vue 3D

        private void SlZone_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Lire tous les sliders et mettre à jour le contrôleur
            _brainZones.Zone(BrainRegion.Frontal).Activation       = SlZoneFrontal?.Value    ?? 0.5;
            _brainZones.Zone(BrainRegion.Prefrontal).Activation    = SlZonePrefrontal?.Value ?? 0.5;
            _brainZones.Zone(BrainRegion.Central).Activation       = SlZoneCentral?.Value    ?? 0.5;
            _brainZones.Zone(BrainRegion.Temporal).Activation      = SlZoneTemporal?.Value   ?? 0.5;
            _brainZones.Zone(BrainRegion.Parietal).Activation      = SlZoneParietal?.Value   ?? 0.5;
            _brainZones.Zone(BrainRegion.Occipital).Activation     = SlZoneOccipital?.Value  ?? 0.5;
            _brainZones.Zone(BrainRegion.FrontoCentral).Activation = SlZoneFC?.Value         ?? 0.5;

            // Mettre à jour les labels
            if (TxtZoneFrontal    != null) TxtZoneFrontal.Text    = $"{SlZoneFrontal?.Value:F2}";
            if (TxtZonePrefrontal != null) TxtZonePrefrontal.Text = $"{SlZonePrefrontal?.Value:F2}";
            if (TxtZoneCentral    != null) TxtZoneCentral.Text    = $"{SlZoneCentral?.Value:F2}";
            if (TxtZoneTemporal   != null) TxtZoneTemporal.Text   = $"{SlZoneTemporal?.Value:F2}";
            if (TxtZoneParietal   != null) TxtZoneParietal.Text   = $"{SlZoneParietal?.Value:F2}";
            if (TxtZoneOccipital  != null) TxtZoneOccipital.Text  = $"{SlZoneOccipital?.Value:F2}";
            if (TxtZoneFC         != null) TxtZoneFC.Text         = $"{SlZoneFC?.Value:F2}";
        }

        private void BtnApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            var tag = ((CbBrainPreset.SelectedItem as ComboBoxItem)?.Tag?.ToString()) ?? "Repos";
            _brainZones.ApplyPreset(tag);

            // Synchroniser les sliders avec les nouvelles valeurs
            if (SlZoneFrontal    != null) SlZoneFrontal.Value    = _brainZones.Zone(BrainRegion.Frontal).Activation;
            if (SlZonePrefrontal != null) SlZonePrefrontal.Value = _brainZones.Zone(BrainRegion.Prefrontal).Activation;
            if (SlZoneCentral    != null) SlZoneCentral.Value    = _brainZones.Zone(BrainRegion.Central).Activation;
            if (SlZoneTemporal   != null) SlZoneTemporal.Value   = _brainZones.Zone(BrainRegion.Temporal).Activation;
            if (SlZoneParietal   != null) SlZoneParietal.Value   = _brainZones.Zone(BrainRegion.Parietal).Activation;
            if (SlZoneOccipital  != null) SlZoneOccipital.Value  = _brainZones.Zone(BrainRegion.Occipital).Activation;
            if (SlZoneFC         != null) SlZoneFC.Value         = _brainZones.Zone(BrainRegion.FrontoCentral).Activation;
        }

        private void BtnOpenTopoMap_Click(object sender, RoutedEventArgs e)
        {
            if (_topoMapWindow != null && _topoMapWindow.IsVisible)
            {
                // Toujours resynchroniser même si déjà ouverte
                _topoMapWindow.Reconfigure(_brainZones, _channelCount, _sampleRate);
                _topoMapWindow.Activate();
                return;
            }
            _topoMapWindow = new TopoMapWindow(_brainZones, _channelCount, _sampleRate);
            _topoMapWindow.Show();
        }

        // ─── Source de données ───────────────────────────────────────────────

        private void RbSource_Changed(object sender, RoutedEventArgs e)
        {
            bool isReplay   = RbReplay?.IsChecked    == true;
            bool isLslInlet = RbLslInlet?.IsChecked  == true;

            if (PanelReplay    != null) PanelReplay.Visibility    = isReplay    ? Visibility.Visible : Visibility.Collapsed;
            if (PanelLslInlet  != null) PanelLslInlet.Visibility  = isLslInlet  ? Visibility.Visible : Visibility.Collapsed;
            if (TxtSourceMode  != null) TxtSourceMode.Text        = isReplay    ? "REPLAY"
                                                                   : isLslInlet ? "LSL IA"
                                                                   :              "GÉNÉRATION";
        }

        private void BtnBrowseReplay_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Sélectionner un fichier EEG",
                Filter = "Fichiers EEG (*.csv;*.bin)|*.csv;*.bin|CSV (*.csv)|*.csv|Binaire (*.bin)|*.bin|Tous (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtReplayPath.Text = dlg.FileName;
                InspectReplayFile(dlg.FileName);
            }
        }

        private void InspectReplayFile(string path)
        {
            var tmpReplay = new EEGDataReplay { FilePath = path };
            var (ok, info, ch, sr, frames) = tmpReplay.Inspect();
            TxtReplayInfo.Text = ok
                ? $"✓ {info}"
                : $"✗ {info}";
            TxtReplayInfo.Foreground = new SolidColorBrush(
                ok ? Color.FromRgb(0, 200, 100) : Color.FromRgb(255, 80, 80));
        }

        private void CbReplaySpeed_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_replay != null && CbReplaySpeed.SelectedItem is ComboBoxItem item)
            {
                if (double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double spd))
                    _replay.Speed = spd;
            }
        }

        private void BtnReplayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_replay == null) return;
            if (_replay.IsPaused)
            {
                _replay.Resume();
                BtnReplayPause.Content = "⏸";
            }
            else
            {
                _replay.Pause();
                BtnReplayPause.Content = "▶";
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // LSL — Open / Close / Push / Status
        // ═════════════════════════════════════════════════════════════════════

        private void OpenEegLsl()
        {
            if (!_lslAvailable) return;
            if (ChkLslEeg?.IsChecked != true) return;
            try
            {
                _eegLsl?.Dispose();
                string name = TxtLslNameEeg?.Text?.Trim() is { Length: > 0 } n ? n : "BioSynth_EEG";
                _eegLsl = new EEGLslStream(_channelCount, _sampleRate, name);
                UpdateLslStatus();
            }
            catch (Exception ex) { TxtStatus.Text = $"LSL EEG erreur : {ex.Message}"; }
        }

        private void OpenEtLsl()
        {
            if (!_lslAvailable) return;
            if (ChkLslEt?.IsChecked != true) return;
            try
            {
                _etLsl?.Dispose();
                string name = TxtLslNameEt?.Text?.Trim() is { Length: > 0 } n ? n : "BioSynth_EyeTracking";
                _etLsl = new EyeTrackingLslStream(120, name);
                UpdateLslStatus();
            }
            catch (Exception ex) { TxtEtStatus.Text = $"LSL ET erreur : {ex.Message}"; }
        }

        private void OpenFtLsl()
        {
            if (!_lslAvailable) return;
            if (ChkLslFt?.IsChecked != true) return;
            try
            {
                _ftLsl?.Dispose();
                string emoName  = TxtLslNameFt?.Text?.Trim() is { Length: > 0 } n ? n + "_Emotions" : "BioSynth_Emotions";
                string poseName = TxtLslNameFt?.Text?.Trim() is { Length: > 0 } n2 ? n2 + "_HeadPose" : "BioSynth_Face";
                _ftLsl = new FaceTrackingLslStream(30, emoName, poseName);
                UpdateLslStatus();
            }
            catch (Exception ex) { TxtFtStatus.Text = $"LSL FT erreur : {ex.Message}"; }
        }

        private void UpdateLslStatus()
        {
            if (TxtLslStatus == null) return;
            if (!_lslAvailable)
            {
                TxtLslStatus.Text = "⚠ lsl.dll introuvable — voir README_LSL.txt";
                TxtLslStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                return;
            }
            var active = new System.Collections.Generic.List<string>();
            if (_eegLsl?.IsOpen == true) active.Add("EEG");
            if (_etLsl?.IsOpen  == true) active.Add("ET");
            if (_ftLsl?.IsOpen  == true) active.Add("FT");
            TxtLslStatus.Text = active.Count > 0
                ? $"● LSL actif : {string.Join(", ", active)}"
                : "○ LSL — cocher pour activer";
            TxtLslStatus.Foreground = new SolidColorBrush(active.Count > 0
                ? Color.FromRgb(0, 255, 136)
                : Color.FromRgb(100, 116, 139));
        }

        private void ChkLslEeg_Changed(object sender, RoutedEventArgs e)
        {
            if (_eegRunning && ChkLslEeg?.IsChecked == true) OpenEegLsl();
            else if (ChkLslEeg?.IsChecked == false) { _eegLsl?.Dispose(); _eegLsl = null; UpdateLslStatus(); }
        }

        private void ChkLslEt_Changed(object sender, RoutedEventArgs e)
        {
            if (_etRunning && ChkLslEt?.IsChecked == true) OpenEtLsl();
            else if (ChkLslEt?.IsChecked == false) { _etLsl?.Dispose(); _etLsl = null; UpdateLslStatus(); }
        }

        private void ChkLslFt_Changed(object sender, RoutedEventArgs e)
        {
            if (_ftRunning && ChkLslFt?.IsChecked == true) OpenFtLsl();
            else if (ChkLslFt?.IsChecked == false) { _ftLsl?.Dispose(); _ftLsl = null; UpdateLslStatus(); }
        }

                // ═════════════════════════════════════════════════════════════════════
        // LSL INLET — Source externe (IA ou autre logiciel)
        // ═════════════════════════════════════════════════════════════════════

        private void BtnDiscoverLsl_Click(object sender, RoutedEventArgs e)
        {
            if (LstLslStreams == null) return;
            LstLslStreams.Items.Clear();
            TxtLslInletInfo.Text    = "Recherche en cours...";
            TxtLslInletStatus.Text  = "○ Recherche des flux LSL...";

            // Lancer la découverte en tâche de fond (timeout 2 s)
            System.Threading.Tasks.Task.Run(() =>
            {
                var streams = EEGLslInlet.DiscoverStreams(2.0);
                Dispatcher.BeginInvoke(() =>
                {
                    _discoveredStreams = streams;
                    LstLslStreams.Items.Clear();
                    if (streams.Count == 0)
                    {
                        TxtLslInletInfo.Text   = "Aucun flux EEG trouvé sur le réseau.";
                        TxtLslInletStatus.Text = "○ Non connecté";
                    }
                    else
                    {
                        foreach (var s in streams)
                            LstLslStreams.Items.Add(s.ToString());
                        TxtLslInletInfo.Text   = $"{streams.Count} flux trouvé(s). Sélectionner pour se connecter.";
                        TxtLslInletStatus.Text = "○ Sélectionner un flux";
                        if (streams.Count == 1) LstLslStreams.SelectedIndex = 0;
                    }
                });
            });
        }

        private void LstLslStreams_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = LstLslStreams?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= _discoveredStreams.Count) return;
            var s = _discoveredStreams[idx];
            TxtLslInletInfo.Text = $"✓ {s.Name} — {s.ChannelCount} canaux · {s.SampleRate} Hz · Type: {s.Type}";
        }

        private void StartLslInlet()
        {
            int idx = LstLslStreams?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= _discoveredStreams.Count)
            {
                MessageBox.Show(
                    "Cliquer sur « Découvrir les flux LSL » puis sélectionner un flux dans la liste.",
                    "LSL Inlet", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var streamInfo = _discoveredStreams[idx];

            while (_eegQueue.TryDequeue(out _)) { }
            for (int i = 0; i < _chanBuffers.Count; i++)
            {
                Array.Clear(_chanBuffers[i], 0, BUFFER_SIZE);
                _chanHead[i] = 0;
            }

            _lslInlet = new EEGLslInlet();

            if (!_lslInlet.Connect(streamInfo))
            {
                MessageBox.Show(
                    "Impossible de se connecter au flux LSL sélectionné. Vérifier que la source est toujours active.",
                    "LSL Inlet", MessageBoxButton.OK, MessageBoxImage.Error);
                _lslInlet.Dispose();
                _lslInlet = null;
                return;
            }

            // Adapter le pipeline au flux entrant
            _channelCount = _lslInlet.ChannelCount;
            _sampleRate   = _lslInlet.SampleRate;

            _lslInlet.SampleReady += sample =>
            {
                if (_eegQueue.Count < MAX_QUEUE) _eegQueue.Enqueue(sample);
            };

            _lslInlet.StatusChanged += msg => Dispatcher.BeginInvoke(() =>
            {
                TxtStatus.Text = msg;
                TxtLslInletStatus.Text = msg;
            });

            _lslInlet.Start();

            // Ré-initialiser l'affichage
            int displayCh = Math.Min(_displayChannels, _channelCount);
            SlDisplayChannels.Maximum = _channelCount;
            InitDisplayChannels(displayCh);
            _topoMapWindow?.Reconfigure(_brainZones, _channelCount, _sampleRate);

            _lslInletRunning = true;
            _eegRunning      = true;
            _statsTimer.Start();

            BtnStartStop.Content    = "■  ARRÊTER";
            BtnStartStop.Background = new SolidColorBrush(Color.FromRgb(180, 30, 30));
            BtnStartStop.Foreground = Brushes.White;
            TxtRecLabel.Text        = "● LSL IA";
            TxtRecLabel.Foreground  = new SolidColorBrush(Color.FromRgb(167, 139, 250));
            RecDotEeg.Opacity       = 1;
            TxtSourceMode.Text      = "LSL IA";
            SetEegControlsEnabled(false);
        }

        private void StopLslInlet()
        {
            _lslInlet?.Stop();
            _lslInlet?.Dispose();
            _lslInlet        = null;
            _lslInletRunning = false;
            if (TxtLslInletStatus != null)
                TxtLslInletStatus.Text = "○ Non connecté";
        }

                private void TxtSampleRate_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        protected override void OnClosed(EventArgs e)
        {
            _renderTimer.Stop();
            _statsTimer.Stop();
            StopEeg();
            StopReplay();
            StopEt();
            StopFt();
            _topoMapWindow?.Close();
            _lslInlet?.Dispose();
            _eegLsl?.Dispose();
            _etLsl?.Dispose();
            _ftLsl?.Dispose();
            base.OnClosed(e);
        }
    }
}
