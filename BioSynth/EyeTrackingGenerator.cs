using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BioSynth
{
    // ─── Configuration Eye Tracking ───────────────────────────────────────────

    public class EyeTrackingConfig
    {
        public int SampleRate { get; set; } = 120;
        public int ScreenWidth { get; set; } = 1920;
        public int ScreenHeight { get; set; } = 1080;
        public OutputMode OutputMode { get; set; } = OutputMode.UdpStream;
        public DataFormat DataFormat { get; set; } = DataFormat.CSV;
        public string FilePath { get; set; } = "eyetracking_data.csv";
        public string StreamHost { get; set; } = "127.0.0.1";
        public int StreamPort { get; set; } = 9998;
        public bool SimulateSaccades { get; set; } = true;
        public bool SimulateBlinks { get; set; } = true;
        public bool SimulatePupilDilation { get; set; } = true;
        public double NoiseLevel { get; set; } = 0.3;
    }

    // ─── Sample Eye Tracking ──────────────────────────────────────────────────

    public class EyeSample
    {
        public long   Timestamp        { get; set; }
        public double GazeX            { get; set; }
        public double GazeY            { get; set; }
        public double GazeXNorm        { get; set; }
        public double GazeYNorm        { get; set; }
        public double PupilLeft        { get; set; }
        public double PupilRight       { get; set; }
        public double ConfidenceLeft   { get; set; }
        public double ConfidenceRight  { get; set; }
        public bool   IsBlinking       { get; set; }
        public string EventType        { get; set; } = "fixation";
        public double VelocityDeg      { get; set; }
    }

    public enum EyeEventType { Fixation, Saccade, Blink, MicroSaccade }

    // ─── Sérialisation binaire partagée ──────────────────────────────────────
    // Layout : int64 ts | float32×8 | byte×2 | float32 = 46 octets

    internal static class EyeBinaryHelper
    {
        internal static void Write(BinaryWriter bw, EyeSample s)
        {
            bw.Write(s.Timestamp);
            bw.Write((float)s.GazeX);
            bw.Write((float)s.GazeY);
            bw.Write((float)s.GazeXNorm);
            bw.Write((float)s.GazeYNorm);
            bw.Write((float)s.PupilLeft);
            bw.Write((float)s.PupilRight);
            bw.Write((float)s.ConfidenceLeft);
            bw.Write((float)s.ConfidenceRight);
            bw.Write(s.IsBlinking ? (byte)1 : (byte)0);
            bw.Write((byte)(s.EventType switch
            {
                "saccade"      => 1,
                "microsaccade" => 2,
                "blink"        => 3,
                _              => 0
            }));
            bw.Write((float)s.VelocityDeg);
        }
    }

    // ─── Interface de sortie ──────────────────────────────────────────────────

    public interface IEyeOutput : IDisposable
    {
        void WriteHeader();
        void WriteSample(EyeSample sample);
    }

    // ─── Sortie fichier ───────────────────────────────────────────────────────

    public class EyeFileOutput : IEyeOutput
    {
        private readonly StreamWriter?     _csv;
        private readonly BinaryWriter?     _bin;
        private readonly EyeTrackingConfig _cfg;

        public EyeFileOutput(EyeTrackingConfig cfg)
        {
            _cfg = cfg;
            if (cfg.DataFormat == DataFormat.CSV)
                _csv = new StreamWriter(cfg.FilePath, false, Encoding.UTF8);
            else
                _bin = new BinaryWriter(File.Open(cfg.FilePath, FileMode.Create));
        }

        public void WriteHeader()
        {
            _csv?.WriteLine(
                "Timestamp_us,GazeX,GazeY,GazeXnorm,GazeYnorm," +
                "PupilL_mm,PupilR_mm,ConfL,ConfR,Blink,EventType,Velocity_dps");
        }

        public void WriteSample(EyeSample s)
        {
            if (_cfg.DataFormat == DataFormat.CSV && _csv != null)
            {
                _csv.WriteLine(
                    $"{s.Timestamp},{s.GazeX},{s.GazeY},{s.GazeXNorm},{s.GazeYNorm}," +
                    $"{s.PupilLeft},{s.PupilRight},{s.ConfidenceLeft},{s.ConfidenceRight}," +
                    $"{(s.IsBlinking ? 1 : 0)},{s.EventType},{s.VelocityDeg}");
            }
            else if (_bin != null)
            {
                EyeBinaryHelper.Write(_bin, s);
            }
        }

        public void Dispose()
        {
            _csv?.Flush(); _csv?.Dispose();
            _bin?.Flush(); _bin?.Dispose();
        }
    }

    // ─── Sortie TCP ───────────────────────────────────────────────────────────

    public class EyeTcpOutput : IEyeOutput
    {
        private readonly TcpListener       _listener;
        private TcpClient?                 _client;
        private NetworkStream?             _stream;
        private readonly EyeTrackingConfig _cfg;

        public EyeTcpOutput(EyeTrackingConfig cfg)
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
                $"{{\"type\":\"et_header\",\"rate\":{_cfg.SampleRate}," +
                $"\"w\":{_cfg.ScreenWidth},\"h\":{_cfg.ScreenHeight}}}\n");
            try { _stream.Write(h, 0, h.Length); } catch { }
        }

        public void WriteSample(EyeSample s)
        {
            if (_stream == null) return;
            try
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
                EyeBinaryHelper.Write(bw, s);
                bw.Flush();
                var d = ms.ToArray();
                _stream.Write(d, 0, d.Length);
            }
            catch { }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _listener.Stop();
        }
    }

    // ─── Sortie UDP ───────────────────────────────────────────────────────────

    public class EyeUdpOutput : IEyeOutput
    {
        private readonly UdpClient  _udp;
        private readonly IPEndPoint _ep;

        public EyeUdpOutput(EyeTrackingConfig cfg)
        {
            _udp = new UdpClient();
            _ep  = new IPEndPoint(IPAddress.Parse(cfg.StreamHost), cfg.StreamPort);
        }

        public void WriteHeader() { }

        public void WriteSample(EyeSample s)
        {
            try
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
                EyeBinaryHelper.Write(bw, s);
                bw.Flush();
                var d = ms.ToArray();
                _udp.Send(d, d.Length, _ep);
            }
            catch { }
        }

        public void Dispose() { _udp.Dispose(); }
    }

    // ─── Générateur Eye Tracking ──────────────────────────────────────────────

    public class EyeTrackingGenerator : IDisposable
    {
        private readonly EyeTrackingConfig _cfg;
        private CancellationTokenSource?   _cts;
        private Task?                      _task;
        private readonly Random            _rng = new();

        public event Action<EyeSample>? SampleGenerated;
        public event Action<string>?    StatusChanged;
        public bool IsRunning             { get; private set; }
        public long TotalSamplesGenerated { get; private set; }

        // ── État oculomoteur ──────────────────────────────────────────────────
        private double       _gazeX, _gazeY;
        private double       _targetX, _targetY;
        private EyeEventType _event       = EyeEventType.Fixation;
        private double       _evtTimer    = 0;
        private double       _evtDuration = 0.4;
        private double       _sacFX, _sacFY, _sacTX, _sacTY;
        private bool         _blinking    = false;
        private double       _blinkTimer  = 0;
        private double       _nextBlink   = 3.0;
        private double       _pupilPhase  = 0;
        private double       _driftPX     = 0;
        private double       _driftPY     = 0;
        private double       _prevGX      = 0;
        private double       _prevGY      = 0;

        public EyeTrackingGenerator(EyeTrackingConfig cfg)
        {
            _cfg     = cfg;
            _gazeX   = cfg.ScreenWidth  / 2.0;
            _gazeY   = cfg.ScreenHeight / 2.0;
            _targetX = _gazeX;
            _targetY = _gazeY;
        }

        public void Start()
        {
            if (IsRunning) return;
            TotalSamplesGenerated = 0;
            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => Loop(_cts.Token));
            IsRunning = true;
            StatusChanged?.Invoke("Eye Tracking démarré.");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try { _task?.Wait(2000); }
            catch (AggregateException) { }
            catch (OperationCanceledException) { }
            finally { IsRunning = false; }
            StatusChanged?.Invoke("Eye Tracking arrêté.");
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
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(tgt - now), ct)
                                  .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }

                var s = Synthesize(dt);
                s.Timestamp = (long)(idx * dt * 1_000_000);
                output.WriteSample(s);
                SampleGenerated?.Invoke(s);
                TotalSamplesGenerated++;
                idx++;
            }
        }

        // ── Synthèse d'un sample ──────────────────────────────────────────────

        private EyeSample Synthesize(double dt)
        {
            _prevGX = _gazeX;
            _prevGY = _gazeY;

            // 1 — Clignements
            _blinkTimer += dt;
            if (_cfg.SimulateBlinks && _blinkTimer >= _nextBlink)
            {
                _blinking   = true;
                _blinkTimer = 0;
                _nextBlink  = 3.0 + _rng.NextDouble() * 6.0;
            }
            if (_blinking && _blinkTimer > 0.15)
                _blinking = false;

            // 2 — FSM saccade / fixation
            _evtTimer += dt;
            if (!_blinking)
            {
                if (_event == EyeEventType.Fixation && _evtTimer >= _evtDuration)
                {
                    if (_cfg.SimulateSaccades && _rng.NextDouble() < 0.75)
                    {
                        _sacFX = _gazeX; _sacFY = _gazeY;
                        _sacTX = CentredTarget(_cfg.ScreenWidth,  50);
                        _sacTY = CentredTarget(_cfg.ScreenHeight, 50);
                        double deg = Dist(_sacFX, _sacFY, _sacTX, _sacTY) / 40.0;
                        _evtDuration = Math.Max(0.02, deg * 0.0022 + 0.012);
                        _event = EyeEventType.Saccade;
                    }
                    else
                    {
                        _sacFX = _gazeX; _sacFY = _gazeY;
                        _sacTX = _gazeX + Gauss() * 12;
                        _sacTY = _gazeY + Gauss() * 12;
                        _evtDuration = 0.012 + _rng.NextDouble() * 0.010;
                        _event = EyeEventType.MicroSaccade;
                    }
                    _evtTimer = 0;
                }
                else if ((_event == EyeEventType.Saccade || _event == EyeEventType.MicroSaccade)
                          && _evtTimer >= _evtDuration)
                {
                    _gazeX = _sacTX; _gazeY = _sacTY;
                    _targetX = _gazeX; _targetY = _gazeY;
                    _event       = EyeEventType.Fixation;
                    _evtDuration = 0.12 + _rng.NextDouble() * 0.85;
                    _evtTimer    = 0;
                    _driftPX     = _rng.NextDouble() * Math.PI * 2;
                    _driftPY     = _rng.NextDouble() * Math.PI * 2;
                }
            }

            // 3 — Position du regard
            double gx = _gazeX, gy = _gazeY;
            if (!_blinking)
            {
                if (_event == EyeEventType.Saccade || _event == EyeEventType.MicroSaccade)
                {
                    double p = Sigmoid(_evtTimer / Math.Max(_evtDuration, 1e-9));
                    gx = _sacFX + (_sacTX - _sacFX) * p;
                    gy = _sacFY + (_sacTY - _sacFY) * p;
                }
                else
                {
                    _driftPX += dt * (0.28 + _rng.NextDouble() * 0.42);
                    _driftPY += dt * (0.22 + _rng.NextDouble() * 0.36);
                    gx = _targetX + Math.Sin(_driftPX) * 7 + Math.Sin(_driftPX * 3.1) * 2;
                    gy = _targetY + Math.Cos(_driftPY) * 7 + Math.Cos(_driftPY * 2.7) * 2;
                }
            }

            gx = Math.Clamp(gx + Gauss() * _cfg.NoiseLevel * 2.5, 0, _cfg.ScreenWidth);
            gy = Math.Clamp(gy + Gauss() * _cfg.NoiseLevel * 2.5, 0, _cfg.ScreenHeight);
            _gazeX = gx;
            _gazeY = gy;

            // 4 — Pupille
            _pupilPhase += dt * 0.08;
            double baseP = 3.5 + Math.Sin(_pupilPhase) * 0.18;
            if (_cfg.SimulatePupilDilation && _event == EyeEventType.Saccade)
                baseP += 0.15;
            double pL = Math.Clamp(baseP + Gauss() * 0.04 * _cfg.NoiseLevel, 1.5, 8.0);
            double pR = Math.Clamp(baseP + 0.05 + Gauss() * 0.04 * _cfg.NoiseLevel, 1.5, 8.0);

            // 5 — Confiance / validité
            double conf = _blinking ? 0.0 : Math.Clamp(1.0 - _cfg.NoiseLevel * 0.3, 0.5, 1.0);

            // 6 — Vitesse angulaire
            double dx  = (_gazeX - _prevGX) / dt;
            double dy  = (_gazeY - _prevGY) / dt;
            double vel = Math.Sqrt(dx * dx + dy * dy) / 40.0;

            // 7 — Label événement
            string lbl = _blinking                        ? "blink"
                       : _event == EyeEventType.Saccade      ? "saccade"
                       : _event == EyeEventType.MicroSaccade ? "microsaccade"
                       : "fixation";

            return new EyeSample
            {
                GazeX          = Math.Round(gx,   2),
                GazeY          = Math.Round(gy,   2),
                GazeXNorm      = Math.Round(gx / _cfg.ScreenWidth,  4),
                GazeYNorm      = Math.Round(gy / _cfg.ScreenHeight, 4),
                PupilLeft      = Math.Round(pL,   3),
                PupilRight     = Math.Round(pR,   3),
                ConfidenceLeft = Math.Round(conf, 3),
                ConfidenceRight= Math.Round(conf, 3),
                IsBlinking     = _blinking,
                EventType      = lbl,
                VelocityDeg    = Math.Round(vel,  1),
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static double Sigmoid(double t)
            => 1.0 / (1.0 + Math.Exp(-12.0 * (Math.Clamp(t, 0, 1) - 0.5)));

        private double CentredTarget(int max, int margin)
        {
            double v;
            do { v = max / 2.0 + Gauss() * (max / 4.0); }
            while (v < margin || v > max - margin);
            return v;
        }

        private static double Dist(double x1, double y1, double x2, double y2)
            => Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private IEyeOutput CreateOutput() => _cfg.OutputMode switch
        {
            OutputMode.File      => new EyeFileOutput(_cfg),
            OutputMode.TcpStream => new EyeTcpOutput(_cfg),
            OutputMode.UdpStream => new EyeUdpOutput(_cfg),
            _                    => new EyeFileOutput(_cfg)
        };

        public void Dispose() { Stop(); _cts?.Dispose(); }
    }
}
