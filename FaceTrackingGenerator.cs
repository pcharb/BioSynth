using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BioSynth
{
    // ════════════════════════════════════════════════════════════════════
    // Enums
    // ════════════════════════════════════════════════════════════════════

    public enum FaceStreamMode
    {
        FaceLandmarks,   // 68 points + pose + AU
        EmotionOnly      // 7 scores émotionnels + dominante
    }

    public enum EmotionLabel
    {
        Neutral = 0,
        Happy,
        Sad,
        Angry,
        Surprised,
        Fearful,
        Disgusted
    }

    // ════════════════════════════════════════════════════════════════════
    // Configuration
    // ════════════════════════════════════════════════════════════════════

    public class FaceTrackingConfig
    {
        public int            SampleRate  { get; set; } = 30;              // Hz (webcam typique)
        public FaceStreamMode StreamMode  { get; set; } = FaceStreamMode.FaceLandmarks;
        public OutputMode     OutputMode  { get; set; } = OutputMode.UdpStream;
        public DataFormat     DataFormat  { get; set; } = DataFormat.CSV;
        public string         FilePath    { get; set; } = "face_data.csv";
        public string         StreamHost  { get; set; } = "127.0.0.1";
        public int            StreamPort  { get; set; } = 9997;
        public double         NoiseLevel  { get; set; } = 0.2;
        public bool           SimulateMicro { get; set; } = true;         // micro-expressions
        public bool           SimulateHeadMove { get; set; } = true;      // mouvement de tête
        public double         EmotionChangeSec { get; set; } = 4.0;       // durée moyenne par état
    }

    // ════════════════════════════════════════════════════════════════════
    // Samples
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sample complet : 68 landmarks + pose 6-DOF + Action Units + détection
    /// </summary>
    public class FaceSample
    {
        public long     Timestamp   { get; set; }          // µs

        // ── Pose tête (6 DOF) ──────────────────────────────────────────
        public double   PoseX       { get; set; }          // translation X mm
        public double   PoseY       { get; set; }          // translation Y mm
        public double   PoseZ       { get; set; }          // profondeur mm
        public double   RotPitch    { get; set; }          // rotation X °
        public double   RotYaw      { get; set; }          // rotation Y °
        public double   RotRoll     { get; set; }          // rotation Z °

        // ── Landmarks 2D (68 points normalisés [0,1]) ─────────────────
        // Stockés en deux tableaux parallèles pour faciliter l'accès
        public float[]  LandmarkX   { get; set; } = new float[68];
        public float[]  LandmarkY   { get; set; } = new float[68];

        // ── Action Units (Facial Action Coding System) ─────────────────
        // AU01 Inner Brow Raise, AU02 Outer Brow Raise, AU04 Brow Lowerer
        // AU05 Upper Lid Raise, AU06 Cheek Raise, AU07 Lid Tightener
        // AU09 Nose Wrinkle, AU10 Upper Lip Raise, AU12 Lip Corner Puller
        // AU14 Dimpler, AU15 Lip Corner Depressor, AU17 Chin Raiser
        // AU20 Lip Stretcher, AU23 Lip Tightener, AU25 Lips Part
        // AU26 Jaw Drop, AU28 Lip Suck, AU45 Blink
        public float[]  ActionUnits { get; set; } = new float[18]; // intensité [0,1]

        // ── Détection ──────────────────────────────────────────────────
        public bool     FaceDetected { get; set; } = true;
        public double   Confidence   { get; set; } = 0.97;

        // ── Émotion dominante (calculée depuis AU) ────────────────────
        public EmotionLabel DominantEmotion { get; set; } = EmotionLabel.Neutral;
        public float[]  EmotionScores { get; set; } = new float[7]; // [0,1] par classe
    }

    /// <summary>
    /// Sample émotions seulement — plus compact, idéal pour BCI / UE5
    /// </summary>
    public class EmotionSample
    {
        public long         Timestamp       { get; set; }
        public EmotionLabel Dominant        { get; set; }
        public float[]      Scores          { get; set; } = new float[7];
        public float        Arousal         { get; set; }  // activation [0,1]
        public float        Valence         { get; set; }  // positif/négatif [-1,1]
        public float        Confidence      { get; set; }
        public bool         FaceDetected    { get; set; } = true;
    }

    // ════════════════════════════════════════════════════════════════════
    // Sérialisation binaire
    // ════════════════════════════════════════════════════════════════════

    internal static class FaceBinaryHelper
    {
        // Landmarks + pose + AU : int64 + 6×f32 + 136×f32 + 18×f32 + 1×f32 + 1×byte + 7×f32
        // = 8 + 24 + 544 + 72 + 4 + 1 + 28 = 681 octets
        internal static void WriteFace(BinaryWriter bw, FaceSample s)
        {
            bw.Write(s.Timestamp);
            bw.Write((float)s.PoseX);  bw.Write((float)s.PoseY);  bw.Write((float)s.PoseZ);
            bw.Write((float)s.RotPitch); bw.Write((float)s.RotYaw); bw.Write((float)s.RotRoll);
            for (int i = 0; i < 68; i++) { bw.Write(s.LandmarkX[i]); bw.Write(s.LandmarkY[i]); }
            for (int i = 0; i < 18; i++) bw.Write(s.ActionUnits[i]);
            bw.Write((float)s.Confidence);
            bw.Write((byte)s.DominantEmotion);
            for (int i = 0; i < 7; i++) bw.Write(s.EmotionScores[i]);
        }

        // Émotion compacte : int64 + 7×f32 + f32 + f32 + f32 + byte = 8 + 28 + 12 + 1 = 49 octets
        internal static void WriteEmotion(BinaryWriter bw, EmotionSample s)
        {
            bw.Write(s.Timestamp);
            for (int i = 0; i < 7; i++) bw.Write(s.Scores[i]);
            bw.Write(s.Arousal);
            bw.Write(s.Valence);
            bw.Write(s.Confidence);
            bw.Write((byte)s.Dominant);
        }

        // Sérialisation JSON (pour mode CSV ou debug)
        internal static string ToJson(EmotionSample s)
        {
            var names = new[] { "neutral","happy","sad","angry","surprised","fearful","disgusted" };
            var sb = new StringBuilder();
            sb.Append($"{{\"ts\":{s.Timestamp},\"dominant\":\"{s.Dominant.ToString().ToLower()}\"");
            sb.Append($",\"arousal\":{s.Arousal:F3},\"valence\":{s.Valence:F3},\"conf\":{s.Confidence:F3}");
            sb.Append(",\"scores\":{");
            for (int i = 0; i < 7; i++)
                sb.Append($"\"{names[i]}\":{s.Scores[i]:F3}{(i < 6 ? "," : "")}");
            sb.Append("}}");
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Interfaces et sorties
    // ════════════════════════════════════════════════════════════════════

    public interface IFaceOutput : IDisposable
    {
        void WriteHeader();
        void WriteFaceSample(FaceSample s);
        void WriteEmotionSample(EmotionSample s);
    }

    // ── Fichier ───────────────────────────────────────────────────────

    public class FaceFileOutput : IFaceOutput
    {
        private readonly StreamWriter?     _csv;
        private readonly BinaryWriter?     _bin;
        private readonly FaceTrackingConfig _cfg;

        public FaceFileOutput(FaceTrackingConfig cfg)
        {
            _cfg = cfg;
            if (cfg.DataFormat == DataFormat.CSV)
                _csv = new StreamWriter(cfg.FilePath, false, Encoding.UTF8);
            else
                _bin = new BinaryWriter(File.Open(cfg.FilePath, FileMode.Create));
        }

        public void WriteHeader()
        {
            if (_cfg.StreamMode == FaceStreamMode.EmotionOnly)
            {
                _csv?.WriteLine("Timestamp_us,Dominant,Arousal,Valence,Conf,Neutral,Happy,Sad,Angry,Surprised,Fearful,Disgusted");
            }
            else
            {
                var sb = new StringBuilder("Timestamp_us,PoseX,PoseY,PoseZ,Pitch,Yaw,Roll");
                for (int i = 0; i < 68; i++) sb.Append($",LX{i},LY{i}");
                for (int i = 0; i < 18; i++) sb.Append($",AU{i}");
                sb.Append(",Conf,DomEmotion");
                _csv?.WriteLine(sb);
            }
        }

        public void WriteFaceSample(FaceSample s)
        {
            if (_bin != null) { FaceBinaryHelper.WriteFace(_bin, s); return; }
            if (_csv == null) return;
            var sb = new StringBuilder(
                $"{s.Timestamp},{s.PoseX:F1},{s.PoseY:F1},{s.PoseZ:F1}," +
                $"{s.RotPitch:F2},{s.RotYaw:F2},{s.RotRoll:F2}");
            for (int i = 0; i < 68; i++) sb.Append($",{s.LandmarkX[i]:F4},{s.LandmarkY[i]:F4}");
            for (int i = 0; i < 18; i++) sb.Append($",{s.ActionUnits[i]:F3}");
            sb.Append($",{s.Confidence:F3},{(int)s.DominantEmotion}");
            _csv.WriteLine(sb);
        }

        public void WriteEmotionSample(EmotionSample s)
        {
            if (_bin != null) { FaceBinaryHelper.WriteEmotion(_bin, s); return; }
            _csv?.WriteLine(
                $"{s.Timestamp},{s.Dominant},{s.Arousal:F3},{s.Valence:F3},{s.Confidence:F3}," +
                $"{s.Scores[0]:F3},{s.Scores[1]:F3},{s.Scores[2]:F3},{s.Scores[3]:F3}," +
                $"{s.Scores[4]:F3},{s.Scores[5]:F3},{s.Scores[6]:F3}");
        }

        public void Dispose() { _csv?.Flush(); _csv?.Dispose(); _bin?.Flush(); _bin?.Dispose(); }
    }

    // ── UDP ───────────────────────────────────────────────────────────

    public class FaceUdpOutput : IFaceOutput
    {
        private readonly UdpClient  _udp;
        private readonly IPEndPoint _ep;
        private readonly FaceTrackingConfig _cfg;

        public FaceUdpOutput(FaceTrackingConfig cfg)
        {
            _cfg = cfg;
            _udp = new UdpClient();
            _ep  = new IPEndPoint(IPAddress.Parse(cfg.StreamHost), cfg.StreamPort);
        }

        public void WriteHeader() { }

        public void WriteFaceSample(FaceSample s)
        {
            try
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
                FaceBinaryHelper.WriteFace(bw, s);
                bw.Flush();
                var d = ms.ToArray();
                _udp.Send(d, d.Length, _ep);
            }
            catch { }
        }

        public void WriteEmotionSample(EmotionSample s)
        {
            try
            {
                // En mode emotion, on envoie JSON (lisible par UE5 / Unity directement)
                var json = Encoding.UTF8.GetBytes(FaceBinaryHelper.ToJson(s) + "\n");
                _udp.Send(json, json.Length, _ep);
            }
            catch { }
        }

        public void Dispose() { _udp.Dispose(); }
    }

    // ── TCP ───────────────────────────────────────────────────────────

    public class FaceTcpOutput : IFaceOutput
    {
        private readonly TcpListener        _listener;
        private TcpClient?                  _client;
        private NetworkStream?              _stream;
        private readonly FaceTrackingConfig _cfg;

        public FaceTcpOutput(FaceTrackingConfig cfg)
        {
            _cfg      = cfg;
            _listener = new TcpListener(IPAddress.Parse(cfg.StreamHost), cfg.StreamPort);
            _listener.Start();
            _listener.BeginAcceptTcpClient(ar =>
            {
                try { _client = _listener.EndAcceptTcpClient(ar); _stream = _client.GetStream(); }
                catch { }
            }, null);
        }

        public void WriteHeader()
        {
            if (_stream == null) return;
            var h = Encoding.UTF8.GetBytes(
                $"{{\"type\":\"face_header\",\"mode\":\"{_cfg.StreamMode}\"," +
                $"\"rate\":{_cfg.SampleRate}}}\n");
            try { _stream.Write(h, 0, h.Length); } catch { }
        }

        public void WriteFaceSample(FaceSample s)   => WriteToStream(w => FaceBinaryHelper.WriteFace(w, s));
        public void WriteEmotionSample(EmotionSample s) => WriteToStream(w => FaceBinaryHelper.WriteEmotion(w, s));

        private void WriteToStream(Action<BinaryWriter> write)
        {
            if (_stream == null) return;
            try
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
                write(bw);
                bw.Flush();
                var d = ms.ToArray();
                _stream.Write(d, 0, d.Length);
            }
            catch { }
        }

        public void Dispose() { _stream?.Dispose(); _client?.Dispose(); _listener.Stop(); }
    }

    // ════════════════════════════════════════════════════════════════════
    // Générateur principal
    // ════════════════════════════════════════════════════════════════════

    public class FaceTrackingGenerator : IDisposable
    {
        private readonly FaceTrackingConfig _cfg;
        private CancellationTokenSource?    _cts;
        private Task?                       _task;
        private readonly Random             _rng = new();

        public event Action<FaceSample>?    FaceSampleGenerated;
        public event Action<EmotionSample>? EmotionSampleGenerated;
        public event Action<string>?        StatusChanged;

        public bool IsRunning             { get; private set; }
        public long TotalSamplesGenerated { get; private set; }

        // ── État interne du modèle de visage ─────────────────────────────

        // Pose tête — mouvement lent + respiration
        private double _poseX = 0, _poseY = 0, _poseZ = 650;
        private double _pitch = 0, _yaw = 0, _roll = 0;
        private double _headPX = 0, _headPY = 0, _headPZ = 0; // phases oscillation
        private double _targetPitch = 0, _targetYaw = 0, _targetRoll = 0;
        private double _headEventTimer  = 0;
        private double _headEventDur    = 3.0;

        // Landmarks de base (configuration neutre)
        private float[,] _baseLandmarks = new float[68, 2];

        // Action Units : état courant + cible + vitesse de transition
        private float[] _auCurrent  = new float[18];
        private float[] _auTarget   = new float[18];
        private float[] _auSpeed    = new float[18];

        // Machine à états émotionnels
        private EmotionLabel _currentEmotion  = EmotionLabel.Neutral;
        private EmotionLabel _targetEmotion   = EmotionLabel.Neutral;
        private float[]      _emotionCurrent  = new float[7];
        private float[]      _emotionTarget   = new float[7];
        private double       _emotionTimer    = 0;
        private double       _emotionDur      = 4.0;
        private double       _transitionTimer = 0;
        private const double TRANSITION_DUR   = 0.8; // secondes de blend entre émotions

        // Micro-expressions
        private double _microTimer   = 0;
        private double _nextMicro    = 5.0;
        private bool   _inMicro      = false;
        private double _microPhase   = 0;
        private EmotionLabel _microEmotion = EmotionLabel.Happy;

        // Respiration (affecte légèrement les AU)
        private double _breathPhase = 0;

        // Correspondance émotion → Action Units (indices AU, intensités cibles)
        // Basé sur le FACS d'Ekman
        private static readonly (int au, float intensity)[][] EmotionAU =
        {
            // Neutral (0) — tout relâché
            Array.Empty<(int,float)>(),
            // Happy (1) — AU06 + AU12
            new[] { (5, 0.4f), (11, 0.7f) },
            // Sad (2) — AU01 + AU04 + AU15 + AU17
            new[] { (0, 0.5f), (3, 0.4f), (14, 0.5f), (16, 0.4f) },
            // Angry (3) — AU04 + AU05 + AU07 + AU23 + AU24
            new[] { (3, 0.6f), (4, 0.3f), (6, 0.5f), (13, 0.4f) },
            // Surprised (4) — AU01 + AU02 + AU05 + AU26
            new[] { (0, 0.7f), (1, 0.7f), (4, 0.6f), (15, 0.8f) },
            // Fearful (5) — AU01 + AU02 + AU04 + AU05 + AU20 + AU26
            new[] { (0, 0.6f), (1, 0.5f), (3, 0.4f), (4, 0.5f), (12, 0.5f), (15, 0.6f) },
            // Disgusted (6) — AU09 + AU15 + AU16 + AU17
            new[] { (8, 0.6f), (14, 0.4f), (15, 0.3f), (16, 0.3f) },
        };

        // Arousal et valence par émotion (modèle circomplexe)
        private static readonly (float arousal, float valence)[] EmotionAV =
        {
            (0.2f,  0.0f),  // Neutral
            (0.7f,  0.8f),  // Happy
            (0.3f, -0.6f),  // Sad
            (0.8f, -0.7f),  // Angry
            (0.9f,  0.1f),  // Surprised
            (0.9f, -0.8f),  // Fearful
            (0.5f, -0.7f),  // Disgusted
        };

        public FaceTrackingGenerator(FaceTrackingConfig cfg)
        {
            _cfg = cfg;
            BuildBaseLandmarks();
            _emotionCurrent[(int)EmotionLabel.Neutral] = 1.0f;
            _emotionTarget[(int)EmotionLabel.Neutral]  = 1.0f;
        }

        public void Start()
        {
            if (IsRunning) return;
            TotalSamplesGenerated = 0;
            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => Loop(_cts.Token));
            IsRunning = true;
            StatusChanged?.Invoke("Face Tracking démarré.");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try { _task?.Wait(2000); }
            catch (AggregateException) { }
            catch (OperationCanceledException) { }
            finally { IsRunning = false; }
            StatusChanged?.Invoke("Face Tracking arrêté.");
        }

        private async Task Loop(CancellationToken ct)
        {
            double dt         = 1.0 / _cfg.SampleRate;
            double intervalMs = 1000.0 / _cfg.SampleRate;
            var    sw         = System.Diagnostics.Stopwatch.StartNew();
            long   idx        = 0;

            using var output = CreateOutput();
            output.WriteHeader();

            while (!ct.IsCancellationRequested)
            {
                double tgt = idx * intervalMs;
                double now = sw.Elapsed.TotalMilliseconds;
                if (now < tgt)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(tgt - now), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                var (face, emotion) = Synthesize(dt);
                face.Timestamp    = (long)(idx * dt * 1_000_000);
                emotion.Timestamp = face.Timestamp;

                if (_cfg.StreamMode == FaceStreamMode.EmotionOnly)
                {
                    output.WriteEmotionSample(emotion);
                    EmotionSampleGenerated?.Invoke(emotion);
                }
                else
                {
                    output.WriteFaceSample(face);
                    FaceSampleGenerated?.Invoke(face);
                    // Émettre aussi les émotions (calculées en interne)
                    EmotionSampleGenerated?.Invoke(emotion);
                }

                TotalSamplesGenerated++;
                idx++;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Synthèse d'un sample
        // ═════════════════════════════════════════════════════════════════

        private (FaceSample face, EmotionSample emotion) Synthesize(double dt)
        {
            _breathPhase += dt * 0.27; // ~16 resp/min

            // 1 ── Machine à états émotionnels ────────────────────────────
            _emotionTimer += dt;
            if (_emotionTimer >= _emotionDur)
            {
                // Choisir la prochaine émotion (distribution pondérée vers Neutral)
                _targetEmotion    = PickNextEmotion();
                _emotionDur       = _cfg.EmotionChangeSec * (0.6 + _rng.NextDouble() * 0.8);
                _emotionTimer     = 0;
                _transitionTimer  = 0;
                SetEmotionTarget(_targetEmotion);
                SetAuTarget(_targetEmotion);
            }

            // Transition douce entre états émotionnels
            _transitionTimer = Math.Min(_transitionTimer + dt, TRANSITION_DUR);
            float tBlend = (float)(_transitionTimer / TRANSITION_DUR);
            for (int i = 0; i < 7; i++)
                _emotionCurrent[i] += (_emotionTarget[i] - _emotionCurrent[i]) * tBlend * 0.15f;
            Normalize(_emotionCurrent);
            _currentEmotion = DominantEmotion(_emotionCurrent);

            // 2 ── Micro-expressions ───────────────────────────────────────
            if (_cfg.SimulateMicro)
            {
                _microTimer += dt;
                if (!_inMicro && _microTimer >= _nextMicro)
                {
                    _inMicro     = true;
                    _microPhase  = 0;
                    _microEmotion= (EmotionLabel)_rng.Next(1, 7);
                    _nextMicro   = 8.0 + _rng.NextDouble() * 15.0;
                    _microTimer  = 0;
                }
                if (_inMicro)
                {
                    _microPhase += dt / 0.18; // micro-expression ~180ms
                    if (_microPhase >= 1.0) _inMicro = false;
                    float mAmp = (float)(Math.Sin(_microPhase * Math.PI) * 0.5);
                    BlendAuMicro(_microEmotion, mAmp);
                }
            }

            // 3 ── Transition AU ───────────────────────────────────────────
            for (int i = 0; i < 18; i++)
            {
                _auCurrent[i] += (_auTarget[i] - _auCurrent[i]) * _auSpeed[i] * (float)dt;
                _auCurrent[i]  = Math.Clamp(_auCurrent[i] + Gauss() * 0.008f * (float)_cfg.NoiseLevel, 0, 1);
            }
            // Blink AU45 automatique
            _auCurrent[17] = (float)Math.Max(0, _auCurrent[17]);

            // 4 ── Pose tête ───────────────────────────────────────────────
            if (_cfg.SimulateHeadMove) UpdateHeadPose(dt);

            // 5 ── Landmarks ───────────────────────────────────────────────
            var landmarks = ComputeLandmarks();

            // 6 ── Arousal / Valence ───────────────────────────────────────
            var (arousalBase, valenceBase) = EmotionAV[(int)_currentEmotion];
            float arousal = Math.Clamp(arousalBase + Gauss() * 0.05f, 0, 1);
            float valence = Math.Clamp(valenceBase + Gauss() * 0.05f, -1, 1);

            // 7 ── Assemblage FaceSample ───────────────────────────────────
            var face = new FaceSample
            {
                PoseX          = Math.Round(_poseX, 1),
                PoseY          = Math.Round(_poseY, 1),
                PoseZ          = Math.Round(_poseZ, 1),
                RotPitch       = Math.Round(_pitch, 2),
                RotYaw         = Math.Round(_yaw,   2),
                RotRoll        = Math.Round(_roll,  2),
                LandmarkX      = landmarks.lx,
                LandmarkY      = landmarks.ly,
                ActionUnits    = (float[])_auCurrent.Clone(),
                FaceDetected   = true,
                Confidence     = Math.Round(0.95 + Gauss() * 0.02 * _cfg.NoiseLevel, 3),
                DominantEmotion= _currentEmotion,
                EmotionScores  = (float[])_emotionCurrent.Clone(),
            };

            // 8 ── EmotionSample ───────────────────────────────────────────
            var emo = new EmotionSample
            {
                Dominant     = _currentEmotion,
                Scores       = (float[])_emotionCurrent.Clone(),
                Arousal      = arousal,
                Valence      = valence,
                Confidence   = (float)face.Confidence,
                FaceDetected = true,
            };

            return (face, emo);
        }

        // ── Pose de tête ─────────────────────────────────────────────────

        private void UpdateHeadPose(double dt)
        {
            _headPX += dt * 0.08; _headPY += dt * 0.11; _headPZ += dt * 0.07;

            // Oscillations lentes de fond (respiration + attention)
            double baseYaw   = Math.Sin(_headPX) * 4.0;
            double basePitch = Math.Sin(_headPY) * 2.5 + Math.Sin(_breathPhase) * 0.8;
            double baseRoll  = Math.Sin(_headPZ) * 1.5;

            // Événements de mouvement de tête planifiés
            _headEventTimer += dt;
            if (_headEventTimer >= _headEventDur)
            {
                _targetYaw   = Gauss() * 12.0;
                _targetPitch = Gauss() * 6.0;
                _targetRoll  = Gauss() * 4.0;
                _headEventDur  = 2.0 + _rng.NextDouble() * 5.0;
                _headEventTimer= 0;
            }

            double lerpK = 0.05 * dt * _cfg.SampleRate;
            _yaw   = Lerp(_yaw,   _targetYaw   + baseYaw,   lerpK);
            _pitch = Lerp(_pitch, _targetPitch + basePitch, lerpK);
            _roll  = Lerp(_roll,  _targetRoll  + baseRoll,  lerpK);

            _poseX = Math.Sin(_headPX * 0.3) * 5.0 + Gauss() * _cfg.NoiseLevel * 0.5;
            _poseY = Math.Sin(_headPY * 0.25) * 3.0 + Gauss() * _cfg.NoiseLevel * 0.5;
            _poseZ = 650 + Math.Sin(_headPZ * 0.15) * 15 + Math.Sin(_breathPhase * 0.5) * 3;
        }

        // ── Landmarks ────────────────────────────────────────────────────

        private void BuildBaseLandmarks()
        {
            // Configuration neutre approximant le modèle dlib 68 points
            // Normalisé [0,1] dans l'image 640×480
            // Contour visage (0-16)
            float cx = 0.5f, cy = 0.5f;
            for (int i = 0; i <= 16; i++)
            {
                double t = (i / 16.0) * Math.PI;
                _baseLandmarks[i, 0] = cx + (float)(Math.Sin(t) * 0.22 * (i < 8 ? -1 : 1));
                _baseLandmarks[i, 1] = cy + (float)(Math.Cos(t) * 0.32) - 0.02f;
            }
            // Sourcil gauche (17-21) et droit (22-26)
            for (int i = 0; i < 5; i++)
            {
                _baseLandmarks[17 + i, 0] = 0.32f + i * 0.04f;
                _baseLandmarks[17 + i, 1] = 0.33f;
                _baseLandmarks[22 + i, 0] = 0.52f + i * 0.04f;
                _baseLandmarks[22 + i, 1] = 0.33f;
            }
            // Nez (27-35)
            for (int i = 0; i < 9; i++)
            {
                _baseLandmarks[27 + i, 0] = cx + (i < 4 ? 0 : (float)((i - 6) * 0.025));
                _baseLandmarks[27 + i, 1] = 0.42f + i * 0.025f;
            }
            // Œil gauche (36-41) et droit (42-47)
            for (int i = 0; i < 6; i++)
            {
                double a = i / 6.0 * 2 * Math.PI;
                _baseLandmarks[36 + i, 0] = 0.36f + (float)(Math.Cos(a) * 0.06);
                _baseLandmarks[36 + i, 1] = 0.40f + (float)(Math.Sin(a) * 0.025);
                _baseLandmarks[42 + i, 0] = 0.64f + (float)(Math.Cos(a) * 0.06);
                _baseLandmarks[42 + i, 1] = 0.40f + (float)(Math.Sin(a) * 0.025);
            }
            // Bouche extérieure (48-59) et intérieure (60-67)
            for (int i = 0; i < 12; i++)
            {
                double a = i / 12.0 * 2 * Math.PI;
                _baseLandmarks[48 + i, 0] = cx + (float)(Math.Cos(a) * 0.10);
                _baseLandmarks[48 + i, 1] = 0.68f + (float)(Math.Sin(a) * 0.04);
            }
            for (int i = 0; i < 8; i++)
            {
                double a = i / 8.0 * 2 * Math.PI;
                _baseLandmarks[60 + i, 0] = cx + (float)(Math.Cos(a) * 0.06);
                _baseLandmarks[60 + i, 1] = 0.68f + (float)(Math.Sin(a) * 0.025);
            }
        }

        private (float[] lx, float[] ly) ComputeLandmarks()
        {
            var lx = new float[68];
            var ly = new float[68];

            // Rotation de la tête simplifiée + déformation par AU
            float sinYaw   = (float)Math.Sin(_yaw   * Math.PI / 180.0);
            float sinPitch = (float)Math.Sin(_pitch * Math.PI / 180.0);
            float sinRoll  = (float)Math.Sin(_roll  * Math.PI / 180.0);
            float cosRoll  = (float)Math.Cos(_roll  * Math.PI / 180.0);

            // Sourire : AU12 écarte les commissures (pts 48, 54)
            float smileOffset = _auCurrent[11] * 0.025f;
            // Lèvres ouvertes : AU25/26 déplace la lèvre inf (pts 57, 58, 59)
            float jawOpen = _auCurrent[15] * 0.030f;
            // Haussement sourcils : AU01+02 monte pts 17-26
            float browRaise = (_auCurrent[0] + _auCurrent[1]) * 0.012f;
            // Froncement : AU04 rapproche les sourcils
            float browFrown = _auCurrent[3] * 0.008f;
            // Plissement yeux : AU06 lève la joue (pts 36-47)
            float eyeSquint = _auCurrent[5] * 0.008f;

            for (int i = 0; i < 68; i++)
            {
                float bx = _baseLandmarks[i, 0] - 0.5f;
                float by = _baseLandmarks[i, 1] - 0.5f;

                // Rotation roll
                float rx = bx * cosRoll - by * sinRoll;
                float ry = bx * sinRoll + by * cosRoll;

                // Perspective yaw (compression horizontale d'un côté)
                rx *= (1.0f - sinYaw * 0.3f * Math.Sign(rx));
                // Perspective pitch (compression verticale)
                ry *= (1.0f - sinPitch * 0.2f * Math.Sign(ry));

                float ox = rx + 0.5f;
                float oy = ry + 0.5f;

                // Déformations par AU
                if (i >= 17 && i <= 26)        { oy -= browRaise; ox += (i < 22 ? -1 : 1) * browFrown; }
                if ((i >= 48 && i <= 54) || i == 60 || i == 64) { ox += (i < 52 ? -smileOffset : smileOffset); }
                if (i >= 55 && i <= 59)        { oy += jawOpen; }
                if (i >= 36 && i <= 47)        { oy += eyeSquint; }

                // Bruit de mesure
                ox += Gauss() * 0.002f * (float)_cfg.NoiseLevel;
                oy += Gauss() * 0.002f * (float)_cfg.NoiseLevel;

                lx[i] = Math.Clamp(ox, 0, 1);
                ly[i] = Math.Clamp(oy, 0, 1);
            }
            return (lx, ly);
        }

        // ── Émotions ─────────────────────────────────────────────────────

        private EmotionLabel PickNextEmotion()
        {
            // Distribution : 40% neutral, 60% réparti sur les autres
            double r = _rng.NextDouble();
            if (r < 0.40) return EmotionLabel.Neutral;
            int idx = _rng.Next(1, 7);
            return (EmotionLabel)idx;
        }

        private void SetEmotionTarget(EmotionLabel emo)
        {
            for (int i = 0; i < 7; i++) _emotionTarget[i] = 0;
            _emotionTarget[(int)emo] = 1.0f;
        }

        private void SetAuTarget(EmotionLabel emo)
        {
            for (int i = 0; i < 18; i++) { _auTarget[i] = 0; _auSpeed[i] = 1.5f + (float)_rng.NextDouble() * 2.0f; }
            foreach (var (au, intensity) in EmotionAU[(int)emo])
                _auTarget[au] = intensity + (float)(Gauss() * 0.08);
        }

        private void BlendAuMicro(EmotionLabel emo, float amp)
        {
            foreach (var (au, intensity) in EmotionAU[(int)emo])
                _auCurrent[au] = Math.Clamp(_auCurrent[au] + intensity * amp, 0, 1);
        }

        private static EmotionLabel DominantEmotion(float[] scores)
        {
            int best = 0;
            for (int i = 1; i < 7; i++)
                if (scores[i] > scores[best]) best = i;
            return (EmotionLabel)best;
        }

        private static void Normalize(float[] v)
        {
            float sum = 0; foreach (var x in v) sum += x;
            if (sum < 1e-6f) { v[0] = 1; return; }
            for (int i = 0; i < v.Length; i++) v[i] /= sum;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);

        private float Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }

        private IFaceOutput CreateOutput() => _cfg.OutputMode switch
        {
            OutputMode.File      => new FaceFileOutput(_cfg),
            OutputMode.TcpStream => new FaceTcpOutput(_cfg),
            _                    => new FaceUdpOutput(_cfg)
        };

        public void Dispose() { Stop(); _cts?.Dispose(); }
    }
}
