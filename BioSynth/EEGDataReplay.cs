using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BioSynth
{
    /// <summary>
    /// Lit un fichier EEG enregistré (CSV ou BIN) et émet les samples
    /// à la cadence originale, en simulant un stream temps réel.
    ///
    /// Formats supportés :
    ///   CSV : "Timestamp_us, Ch1, Ch2, ..."  (en-tête détecté automatiquement)
    ///   BIN : int64 timestamp + N × float32  (même format que l'enregistreur)
    ///
    /// Contrôle :
    ///   Start() / Stop() / Pause() / Resume()
    ///   Speed : facteur de vitesse (1.0 = temps réel, 2.0 = double vitesse)
    ///   Loop  : reboucler à la fin du fichier
    /// </summary>
    public class EEGDataReplay : IDisposable
    {
        // ── Configuration ──────────────────────────────────────────────────
        public string  FilePath  { get; set; } = "";
        public double  Speed     { get; set; } = 1.0;
        public bool    Loop      { get; set; } = false;

        // ── État ───────────────────────────────────────────────────────────
        public bool    IsRunning   { get; private set; }
        public bool    IsPaused    { get; private set; }
        public int     ChannelCount{ get; private set; }
        public int     SampleRate  { get; private set; } = 256;
        public long    TotalFrames { get; private set; }
        public long    CurrentFrame{ get; private set; }
        public double  ProgressPct => TotalFrames > 0 ? CurrentFrame * 100.0 / TotalFrames : 0;

        // ── Événements ─────────────────────────────────────────────────────
        public event Action<EEGSample>? SampleReady;
        public event Action<string>?    StatusChanged;
        public event Action?            PlaybackFinished;

        // ── Interne ────────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task?                    _task;
        private readonly SemaphoreSlim   _pauseSem = new(1, 1);

        // Données pré-chargées en mémoire (pour fichiers < 100 MB)
        private List<EEGSample>? _frames;
        private bool             _isStreaming = false;  // lecture streaming si fichier > 100 MB

        // ════════════════════════════════════════════════════════════════════
        // CHARGEMENT
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Inspecte le fichier et retourne les métadonnées sans charger tout en mémoire.
        /// Lancer avant Start() pour afficher les infos à l'utilisateur.
        /// </summary>
        public (bool ok, string info, int channels, int sampleRate, long frames) Inspect()
        {
            if (!File.Exists(FilePath))
                return (false, $"Fichier non trouvé : {FilePath}", 0, 0, 0);

            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            try
            {
                if (ext == ".csv" || ext == ".txt")
                    return InspectCsv();
                else if (ext == ".bin" || ext == ".eeg")
                    return InspectBin();
                else
                    return (false, $"Format non supporté : {ext}. Utiliser .csv ou .bin", 0, 0, 0);
            }
            catch (Exception ex)
            {
                return (false, $"Erreur lecture : {ex.Message}", 0, 0, 0);
            }
        }

        private (bool, string, int, int, long) InspectCsv()
        {
            using var sr = new StreamReader(FilePath, Encoding.UTF8);
            string? header = sr.ReadLine();
            if (header == null) return (false, "Fichier vide", 0, 0, 0);

            // Détecter si c'est un vrai en-tête ou une ligne de données
            var cols = header.Split(',');
            int channels = 0;
            bool hasHeader = false;

            if (cols[0].Trim().ToLower().Contains("time"))
            {
                hasHeader = true;
                channels  = cols.Length - 1;
            }
            else
            {
                // Essayer de parser comme données — compter les colonnes
                channels = cols.Length - 1;  // première colonne = timestamp
            }

            long frames = 0;
            while (sr.ReadLine() != null) frames++;
            if (!hasHeader) frames++;  // compter la première ligne

            // Estimer le sample rate depuis les 2 premiers timestamps
            long sr2 = EstimateSampleRateCsv(hasHeader);

            string info = $"CSV — {channels} canaux, ~{sr2} Hz, {frames} frames, {frames / Math.Max(sr2, 1)} s";
            return (true, info, channels, (int)sr2, frames);
        }

        private (bool, string, int, int, long) InspectBin()
        {
            long fileSize = new FileInfo(FilePath).Length;
            // Lire les premiers octets pour détecter le nombre de canaux
            // Format : int64 + N × float32
            // On teste N = 1..64 pour trouver celui qui divise proprement la taille
            using var br = new BinaryReader(File.OpenRead(FilePath));
            long ts1 = br.ReadInt64();
            // Lire un float pour estimer
            // Taille d'un frame = 8 + N*4
            // On teste depuis les métadonnées si disponibles, sinon on force 8 canaux
            int channels = 8;
            for (int n = 1; n <= 64; n++)
            {
                long frameSize = 8 + n * 4;
                if (fileSize % frameSize == 0) { channels = n; break; }
            }

            long frames   = fileSize / (8 + channels * 4);
            int  sampleRate = 256;  // défaut si pas d'info

            // Estimer le SR depuis les timestamps
            if (frames > 1)
            {
                var br2 = new BinaryReader(File.OpenRead(FilePath));
                long t1 = br2.ReadInt64();
                br2.BaseStream.Seek(8 + channels * 4, SeekOrigin.Begin);
                long t2 = br2.ReadInt64();
                br2.Dispose();
                long dtUs = t2 - t1;
                if (dtUs > 0) sampleRate = (int)(1_000_000.0 / dtUs);
            }

            string info = $"BIN — {channels} canaux, {sampleRate} Hz, {frames} frames, {frames / Math.Max(sampleRate, 1)} s";
            return (true, info, channels, sampleRate, frames);
        }

        private long EstimateSampleRateCsv(bool skipHeader)
        {
            try
            {
                using var sr = new StreamReader(FilePath);
                if (skipHeader) sr.ReadLine();
                string? l1 = sr.ReadLine();
                string? l2 = sr.ReadLine();
                if (l1 == null || l2 == null) return 256;
                long t1 = long.Parse(l1.Split(',')[0].Trim());
                long t2 = long.Parse(l2.Split(',')[0].Trim());
                long dtUs = t2 - t1;
                return dtUs > 0 ? Math.Clamp(1_000_000L / dtUs, 1, 10000) : 256;
            }
            catch { return 256; }
        }

        // ════════════════════════════════════════════════════════════════════
        // CONTRÔLE PLAYBACK
        // ════════════════════════════════════════════════════════════════════

        public void Start()
        {
            if (IsRunning) Stop();

            var (ok, info, ch, sr, frames) = Inspect();
            if (!ok)
            {
                StatusChanged?.Invoke($"Erreur : {info}");
                return;
            }

            ChannelCount = ch;
            SampleRate   = sr;
            TotalFrames  = frames;
            CurrentFrame = 0;

            // Pré-charger si < 80 MB
            long fileSize = new FileInfo(FilePath).Length;
            if (fileSize < 80 * 1024 * 1024)
            {
                var ext = Path.GetExtension(FilePath).ToLowerInvariant();
                _frames = ext is ".csv" or ".txt" ? LoadCsv() : LoadBin();
                TotalFrames = _frames?.Count ?? 0;
                _isStreaming = false;
            }
            else
            {
                _frames = null;
                _isStreaming = true;
            }

            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => PlayLoop(_cts.Token));
            IsRunning = true;
            StatusChanged?.Invoke($"Lecture : {Path.GetFileName(FilePath)} — {ChannelCount} ch, {SampleRate} Hz");
        }

        public void Stop()
        {
            _cts?.Cancel();
            if (IsPaused) { IsPaused = false; _pauseSem.Release(); }
            try { _task?.Wait(2000); } catch { }
            IsRunning = false;
            IsPaused  = false;
            StatusChanged?.Invoke("Lecture arrêtée.");
        }

        public void Pause()
        {
            if (!IsRunning || IsPaused) return;
            IsPaused = true;
            _pauseSem.Wait(0);  // prendre le sémaphore = bloquer la boucle
            StatusChanged?.Invoke("En pause.");
        }

        public void Resume()
        {
            if (!IsPaused) return;
            IsPaused = false;
            _pauseSem.Release();
            StatusChanged?.Invoke("Reprise.");
        }

        // ════════════════════════════════════════════════════════════════════
        // BOUCLE DE LECTURE
        // ════════════════════════════════════════════════════════════════════

        private async Task PlayLoop(CancellationToken ct)
        {
            do
            {
                CurrentFrame = 0;

                if (_isStreaming)
                    await StreamFromFile(ct);
                else if (_frames != null)
                    await PlayFromMemory(_frames, ct);

            } while (Loop && !ct.IsCancellationRequested);

            IsRunning = false;
            PlaybackFinished?.Invoke();
            StatusChanged?.Invoke("Lecture terminée.");
        }

        private async Task PlayFromMemory(List<EEGSample> frames, CancellationToken ct)
        {
            double intervalMs = 1000.0 / (SampleRate * Math.Max(Speed, 0.1));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < frames.Count && !ct.IsCancellationRequested; i++)
            {
                // Pause
                await _pauseSem.WaitAsync(ct);
                _pauseSem.Release();

                double targetMs = i * intervalMs;
                double nowMs    = sw.Elapsed.TotalMilliseconds;
                if (nowMs < targetMs)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(targetMs - nowMs), ct); }
                    catch (OperationCanceledException) { break; }
                }

                SampleReady?.Invoke(frames[i]);
                CurrentFrame = i + 1;
            }
        }

        private async Task StreamFromFile(CancellationToken ct)
        {
            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            double intervalMs = 1000.0 / (SampleRate * Math.Max(Speed, 0.1));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long frameIdx = 0;

            if (ext is ".csv" or ".txt")
            {
                using var sr = new StreamReader(FilePath, Encoding.UTF8);
                // Détecter et skipper l'en-tête
                string? firstLine = sr.ReadLine();
                bool hasHeader = firstLine != null && firstLine.Split(',')[0].Trim().ToLower().Contains("time");
                if (!hasHeader && firstLine != null)
                {
                    // Ce n'est pas un header — traiter la ligne comme données
                    var s = ParseCsvLine(firstLine, ChannelCount);
                    if (s != null) { SampleReady?.Invoke(s); frameIdx++; }
                }

                while (!sr.EndOfStream && !ct.IsCancellationRequested)
                {
                    await _pauseSem.WaitAsync(ct);
                    _pauseSem.Release();

                    double target = frameIdx * intervalMs;
                    double nowMs  = sw.Elapsed.TotalMilliseconds;
                    if (nowMs < target)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(target - nowMs), ct); }
                        catch (OperationCanceledException) { break; }
                    }

                    var line = await sr.ReadLineAsync();
                    if (line == null) break;
                    var sample = ParseCsvLine(line, ChannelCount);
                    if (sample != null) { SampleReady?.Invoke(sample); CurrentFrame = ++frameIdx; }
                }
            }
            else
            {
                int frameSize = 8 + ChannelCount * 4;
                using var br = new BinaryReader(File.OpenRead(FilePath));

                while (br.BaseStream.Position + frameSize <= br.BaseStream.Length && !ct.IsCancellationRequested)
                {
                    await _pauseSem.WaitAsync(ct);
                    _pauseSem.Release();

                    double target = frameIdx * intervalMs;
                    double nowMs  = sw.Elapsed.TotalMilliseconds;
                    if (nowMs < target)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(target - nowMs), ct); }
                        catch (OperationCanceledException) { break; }
                    }

                    var sample = ReadBinFrame(br, ChannelCount);
                    if (sample != null) { SampleReady?.Invoke(sample); CurrentFrame = ++frameIdx; }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // PARSERS
        // ════════════════════════════════════════════════════════════════════

        private List<EEGSample> LoadCsv()
        {
            var list = new List<EEGSample>(4096);
            using var sr = new StreamReader(FilePath, Encoding.UTF8);

            string? firstLine = sr.ReadLine();
            bool hasHeader = firstLine != null && firstLine.Split(',')[0].Trim().ToLower().Contains("time");
            if (!hasHeader && firstLine != null)
            {
                var s = ParseCsvLine(firstLine, ChannelCount);
                if (s != null) list.Add(s);
            }

            while (!sr.EndOfStream)
            {
                var line = sr.ReadLine();
                if (line == null) break;
                var s = ParseCsvLine(line, ChannelCount);
                if (s != null) list.Add(s);
            }
            return list;
        }

        private List<EEGSample> LoadBin()
        {
            var list = new List<EEGSample>(4096);
            int frameSize = 8 + ChannelCount * 4;
            using var br = new BinaryReader(File.OpenRead(FilePath));
            while (br.BaseStream.Position + frameSize <= br.BaseStream.Length)
            {
                var s = ReadBinFrame(br, ChannelCount);
                if (s != null) list.Add(s);
            }
            return list;
        }

        private static EEGSample? ParseCsvLine(string line, int expectedChannels)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            var parts = line.Split(',');
            if (parts.Length < 2) return null;

            int n = parts.Length - 1;
            var sample = new EEGSample(n);

            if (long.TryParse(parts[0].Trim(), out long ts))
                sample.Timestamp = ts;

            for (int i = 0; i < n; i++)
            {
                if (double.TryParse(parts[i + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    sample.Channels[i] = v;
            }
            return sample;
        }

        private static EEGSample? ReadBinFrame(BinaryReader br, int channels)
        {
            try
            {
                long ts = br.ReadInt64();
                var s = new EEGSample(channels) { Timestamp = ts };
                for (int i = 0; i < channels; i++)
                    s.Channels[i] = br.ReadSingle();
                return s;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            Stop();
            _pauseSem.Dispose();
            _cts?.Dispose();
        }
    }
}
