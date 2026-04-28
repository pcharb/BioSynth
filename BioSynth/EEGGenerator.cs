using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BioSynth
{
    /// <summary>
    /// Mode de sortie des données EEG générées.
    /// </summary>
    public enum OutputMode
    {
        File,
        TcpStream,
        UdpStream
    }

    /// <summary>
    /// Format des données de sortie.
    /// </summary>
    public enum DataFormat
    {
        CSV,
        BinaryFloat32,
        BDF  // BioSemi Data Format simplifié
    }

    /// <summary>
    /// Configuration de la génération EEG.
    /// </summary>
    public class EEGConfig
    {
        public int ChannelCount { get; set; } = 8;           // 8, 16, 32 ou 64
        public int SampleRate { get; set; } = 256;           // Hz
        public OutputMode OutputMode { get; set; } = OutputMode.File;
        public DataFormat DataFormat { get; set; } = DataFormat.CSV;
        public string FilePath { get; set; } = "eeg_data.csv";
        public string StreamHost { get; set; } = "127.0.0.1";
        public int StreamPort { get; set; } = 9999;
        public bool AddArtifacts { get; set; } = true;       // Clignements yeux, mouvement
        public double NoiseLevel { get; set; } = 0.5;        // 0.0 - 1.0
    }

    /// <summary>
    /// Un paquet de samples EEG (un point dans le temps, N canaux).
    /// </summary>
    public class EEGSample
    {
        public long Timestamp { get; set; }   // microsecondes depuis démarrage
        public double[] Channels { get; set; }
        public EEGSample(int channelCount)
        {
            Channels = new double[channelCount];
        }
    }

    /// <summary>
    /// Générateur de signaux EEG synthétiques réalistes.
    /// Simule alpha, beta, theta, delta, gamma + artifacts oculaires/musculaires.
    /// </summary>
    public class EEGGenerator : IDisposable
    {
        private readonly EEGConfig _config;
        private CancellationTokenSource? _cts;
        private Task? _generatorTask;
        private readonly Random _rng = new();

        // État interne de phase pour chaque canal × bande
        private double[][]? _phases;

        // Fréquences caractéristiques des bandes EEG
        private static readonly (string Name, double FreqMin, double FreqMax, double Amplitude)[] Bands =
        {
            ("Delta",  0.5,  4.0,  80.0),
            ("Theta",  4.0,  8.0,  40.0),
            ("Alpha",  8.0, 13.0, 100.0),
            ("Beta",  13.0, 30.0,  20.0),
            ("Gamma", 30.0, 80.0,   5.0),
        };

        // Événements
        public event Action<EEGSample>? SampleGenerated;
        public event Action<string>? StatusChanged;
        public bool IsRunning { get; private set; }

        // Compteurs
        public long TotalSamplesGenerated { get; private set; }

        // Contrôleur de zones cérébrales (optionnel)
        public BrainZoneController? ZoneController { get; set; }

        public EEGGenerator(EEGConfig config)
        {
            _config = config;
        }

        /// <summary>Démarre la génération.</summary>
        public void Start()
        {
            if (IsRunning) return;
            InitPhases();
            TotalSamplesGenerated = 0;
            _cts = new CancellationTokenSource();
            _generatorTask = Task.Run(() => GenerateLoop(_cts.Token));
            IsRunning = true;
            StatusChanged?.Invoke("Génération démarrée.");
        }

        /// <summary>Arrête la génération.</summary>
        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try { _generatorTask?.Wait(2000); }
            catch (AggregateException) { }   // TaskCanceledException attendu
            catch (OperationCanceledException) { }
            finally { IsRunning = false; }
            StatusChanged?.Invoke("Génération arrêtée.");
        }

        private void InitPhases()
        {
            _phases = new double[_config.ChannelCount][];
            for (int ch = 0; ch < _config.ChannelCount; ch++)
            {
                _phases[ch] = new double[Bands.Length];
                for (int b = 0; b < Bands.Length; b++)
                    _phases[ch][b] = _rng.NextDouble() * 2 * Math.PI;
            }
        }

        private async Task GenerateLoop(CancellationToken ct)
        {
            var intervalMs = 1000.0 / _config.SampleRate;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var output = CreateOutput();
            WriteHeader(output);

            long sampleIndex = 0;
            double artifactTimer = 0;
            double nextArtifactTime = GetNextArtifactTime();

            while (!ct.IsCancellationRequested)
            {
                var targetTime = sampleIndex * intervalMs;
                var elapsed = sw.Elapsed.TotalMilliseconds;

                if (elapsed < targetTime)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(targetTime - elapsed), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }

                double t = sampleIndex / (double)_config.SampleRate;

                // Artifact ?
                bool isArtifact = false;
                double artifactStrength = 0;
                if (_config.AddArtifacts)
                {
                    artifactTimer = t;
                    if (t >= nextArtifactTime && t < nextArtifactTime + 0.15)
                    {
                        isArtifact = true;
                        artifactStrength = Math.Sin(Math.PI * (t - nextArtifactTime) / 0.15);
                    }
                    else if (t >= nextArtifactTime + 0.15)
                    {
                        nextArtifactTime = t + GetNextArtifactTime();
                    }
                }

                var sample = new EEGSample(_config.ChannelCount)
                {
                    Timestamp = (long)(t * 1_000_000)
                };

                for (int ch = 0; ch < _config.ChannelCount; ch++)
                {
                    double signal = 0;

                    // Multiplicateurs de zone cérébrale
                    double[] zoneMult = ZoneController?.GetMultipliers(ch, _config.ChannelCount)
                                       ?? new double[] { 1, 1, 1, 1, 1 };

                    for (int b = 0; b < Bands.Length; b++)
                    {
                        var (_, fMin, fMax, amp) = Bands[b];
                        double freq = fMin + (fMax - fMin) * ChannelFreqModifier(ch, b);

                        // Modulation d'amplitude + multiplicateur de zone
                        double ampMod = amp * (0.8 + 0.2 * Math.Sin(2 * Math.PI * 0.05 * t + ch))
                                            * zoneMult[b];

                        _phases![ch][b] += 2 * Math.PI * freq / _config.SampleRate;
                        signal += ampMod * Math.Sin(_phases[ch][b]);
                    }

                    // Bruit blanc
                    signal += GaussianNoise() * _config.NoiseLevel * 30;

                    // Artifact oculaire (fort sur canaux frontaux Fp1/Fp2)
                    if (isArtifact && ch < 2)
                        signal += artifactStrength * 300;

                    // Artifact musculaire (sur canaux temporaux)
                    if (isArtifact && ch >= _config.ChannelCount / 2)
                        signal += GaussianNoise() * artifactStrength * 50;

                    // Converti en µV
                    sample.Channels[ch] = signal;
                }

                WriteData(output, sample);
                SampleGenerated?.Invoke(sample);
                TotalSamplesGenerated++;
                sampleIndex++;
            }

            StatusChanged?.Invoke($"Terminé — {TotalSamplesGenerated} samples générés.");
        }

        private double ChannelFreqModifier(int ch, int band)
        {
            // Chaque canal a des fréquences légèrement différentes pour réalisme
            return ((ch * 7 + band * 3) % 10) / 10.0;
        }

        private double GaussianNoise()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private double GetNextArtifactTime() => 3 + _rng.NextDouble() * 7; // toutes les 3-10s

        // ─── Sortie ───────────────────────────────────────────────────────────

        private IEEGOutput CreateOutput() => _config.OutputMode switch
        {
            OutputMode.File => new FileOutput(_config),
            OutputMode.TcpStream => new TcpStreamOutput(_config),
            OutputMode.UdpStream => new UdpStreamOutput(_config),
            _ => new FileOutput(_config)
        };

        private static void WriteHeader(IEEGOutput output) => output.WriteHeader();
        private static void WriteData(IEEGOutput output, EEGSample s) => output.WriteSample(s);

        public void Dispose() { Stop(); _cts?.Dispose(); }
    }

    // ─── Interfaces de sortie ──────────────────────────────────────────────────

    public interface IEEGOutput : IDisposable
    {
        void WriteHeader();
        void WriteSample(EEGSample sample);
    }

    public class FileOutput : IEEGOutput
    {
        private readonly EEGConfig _config;
        private StreamWriter? _writer;
        private BinaryWriter? _binWriter;

        public FileOutput(EEGConfig config)
        {
            _config = config;
            if (config.DataFormat == DataFormat.CSV)
                _writer = new StreamWriter(config.FilePath, false, Encoding.UTF8);
            else
                _binWriter = new BinaryWriter(File.Open(config.FilePath, FileMode.Create));
        }

        public void WriteHeader()
        {
            if (_config.DataFormat == DataFormat.CSV && _writer != null)
            {
                var headers = new List<string> { "Timestamp_us" };
                for (int i = 0; i < _config.ChannelCount; i++)
                    headers.Add(ChannelNames.GetChannelName(i, _config.ChannelCount));
                _writer.WriteLine(string.Join(",", headers));
            }
            else if (_config.DataFormat == DataFormat.BDF && _binWriter != null)
            {
                WriteBDFHeader(_binWriter, _config);
            }
        }

        public void WriteSample(EEGSample sample)
        {
            if (_config.DataFormat == DataFormat.CSV && _writer != null)
            {
                var sb = new StringBuilder();
                sb.Append(sample.Timestamp);
                foreach (var v in sample.Channels)
                { sb.Append(','); sb.Append(v.ToString("F4")); }
                _writer.WriteLine(sb);
            }
            else if (_config.DataFormat == DataFormat.BinaryFloat32 && _binWriter != null)
            {
                _binWriter.Write(sample.Timestamp);
                foreach (var v in sample.Channels)
                    _binWriter.Write((float)v);
            }
        }

        private static void WriteBDFHeader(BinaryWriter w, EEGConfig cfg)
        {
            // En-tête BDF simplifié (256 octets)
            w.Write(Encoding.ASCII.GetBytes("0       ".PadRight(8)));
            w.Write(Encoding.ASCII.GetBytes("EEG Simulator".PadRight(80)));
            w.Write(Encoding.ASCII.GetBytes(DateTime.Now.ToString("dd.MM.yyHH.mm.ss")));
            w.Write(Encoding.ASCII.GetBytes("256     "));
            w.Write(Encoding.ASCII.GetBytes("BDF+C   "));
            w.Write(Encoding.ASCII.GetBytes((-1).ToString().PadRight(8)));
            w.Write(Encoding.ASCII.GetBytes("1       "));
            w.Write(Encoding.ASCII.GetBytes(cfg.ChannelCount.ToString().PadRight(4)));
        }

        public void Dispose() { _writer?.Flush(); _writer?.Dispose(); _binWriter?.Flush(); _binWriter?.Dispose(); }
    }

    public class TcpStreamOutput : IEEGOutput
    {
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly EEGConfig _config;

        public TcpStreamOutput(EEGConfig config)
        {
            _config = config;
            _listener = new TcpListener(IPAddress.Parse(config.StreamHost), config.StreamPort);
            _listener.Start();
            // Attente connexion entrante (non-bloquant, best-effort)
            _listener.BeginAcceptTcpClient(ar =>
            {
                try { _client = _listener!.EndAcceptTcpClient(ar); _stream = _client.GetStream(); }
                catch { }
            }, null);
        }

        public void WriteHeader()
        {
            // En-tête JSON envoyé une fois
            if (_stream == null) return;
            var header = $"{{\"type\":\"header\",\"channels\":{_config.ChannelCount},\"sampleRate\":{_config.SampleRate}}}\n";
            var bytes = Encoding.UTF8.GetBytes(header);
            try { _stream.Write(bytes, 0, bytes.Length); } catch { }
        }

        public void WriteSample(EEGSample sample)
        {
            if (_stream == null) return;
            try
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
                bw.Write(sample.Timestamp);
                foreach (var v in sample.Channels) bw.Write((float)v);
                bw.Flush();
                var data = ms.ToArray();
                _stream.Write(data, 0, data.Length);
            }
            catch { }
        }

        public void Dispose() { _stream?.Dispose(); _client?.Dispose(); _listener?.Stop(); }
    }

    public class UdpStreamOutput : IEEGOutput
    {
        private readonly UdpClient _udp;
        private readonly IPEndPoint _endpoint;
        private readonly EEGConfig _config;

        public UdpStreamOutput(EEGConfig config)
        {
            _config = config;
            _udp = new UdpClient();
            _endpoint = new IPEndPoint(IPAddress.Parse(config.StreamHost), config.StreamPort);
        }

        public void WriteHeader() { }

        public void WriteSample(EEGSample sample)
        {
            try
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
                bw.Write(sample.Timestamp);
                foreach (var v in sample.Channels) bw.Write((float)v);
                bw.Flush();
                var data = ms.ToArray();
                _udp.Send(data, data.Length, _endpoint);
            }
            catch { }
        }

        public void Dispose() { _udp.Dispose(); }
    }

    // ─── Noms de canaux EEG standard (système 10-20) ─────────────────────────

    public static class ChannelNames
    {
        public static string GetChannelName(int index, int total)
        {
            string[] names8  = { "Fp1","Fp2","C3","C4","P3","P4","O1","O2" };
            string[] names16 = { "Fp1","Fp2","F7","F3","Fz","F4","F8","T7","C3","Cz","C4","T8","P3","Pz","P4","Oz" };
            string[] names32 = {
                "Fp1","Fp2","AF3","AF4","F7","F3","Fz","F4","F8","FC5","FC1","FC2","FC6",
                "T7","C3","Cz","C4","T8","CP5","CP1","CP2","CP6","P7","P3","Pz","P4","P8",
                "PO7","PO3","POz","PO4","PO8"
            };
            string[] names64 = {
                "Fp1","Fpz","Fp2","AF9","AF7","AF3","AFz","AF4","AF8","AF10",
                "F9","F7","F5","F3","F1","Fz","F2","F4","F6","F8","F10",
                "FT9","FT7","FC5","FC3","FC1","FCz","FC2","FC4","FC6","FT8","FT10",
                "T9","T7","C5","C3","C1","Cz","C2","C4","C6","T8","T10",
                "TP9","TP7","CP5","CP3","CP1","CPz","CP2","CP4","CP6","TP8","TP10",
                "P9","P7","P5","P3","P1","Pz","P2","P4","P6","P8","P10"
            };
            var arr = total switch
            {
                8  => names8,
                16 => names16,
                32 => names32,
                _  => names64
            };
            return index < arr.Length ? arr[index] : $"Ch{index + 1}";
        }
    }
}
