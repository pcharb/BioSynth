using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    ///   CSV maison : "Timestamp_us, Ch1, Ch2, ..."  (en-tête détecté automatiquement)
    ///   BIN        : int64 timestamp + N × float32  (même format que l'enregistreur)
    ///   CSV générique, Excel (.xlsx), BrainVision (.vhdr/.vmrk/.eeg/.dat) : via RecordingLoader.
    ///     Colonne de temps et marqueurs détectés d'après les en-têtes ; échantillons
    ///     irréguliers rejoués sur leurs timestamps ; marqueurs émis via MarkerReady.
    ///
    /// Contrôle :
    ///   Start() / Stop() / Pause() / Resume()
    ///   Speed : facteur de vitesse (1.0 = temps réel, 2.0 = double vitesse)
    ///   Loop  : reboucler à la fin du fichier (ou de la liste)
    ///
    /// Liste de lecture :
    ///   FilePaths : plusieurs fichiers du même type (mêmes canaux) joués à la suite,
    ///   puis en boucle si Loop. Les timestamps restent continus d'un fichier à l'autre
    ///   (ContinuousTimestamps) pour que les consommateurs LSL voient un flux unique.
    /// </summary>
    public class EEGDataReplay : IDisposable
    {
        // ── Configuration ──────────────────────────────────────────────────
        public string  FilePath  { get; set; } = "";
        /// <summary>Liste de lecture. Si non vide, remplace FilePath ; tous les fichiers doivent avoir les mêmes canaux.</summary>
        public List<string> FilePaths { get; } = new();
        /// <summary>En liste de lecture : timestamps cumulés d'un fichier à l'autre plutôt que remis à zéro.</summary>
        public bool ContinuousTimestamps { get; set; } = true;
        /// <summary>Ignore la détection du format maison (Timestamp_us / BIN) et passe tout par RecordingLoader.
        /// Utilisé par le replay oculométrique, dont les exports BioSynth contiennent des colonnes texte.</summary>
        public bool ForceGenericFormat { get; set; } = false;
        /// <summary>Fichiers effectivement joués (FilePaths, sinon FilePath seul).</summary>
        public IReadOnlyList<string> Files => FilePaths.Count > 0 ? FilePaths : new[] { FilePath };
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

        /// <summary>Noms de canaux du fichier (null pour CSV maison / BIN : noms 10-20 par défaut).</summary>
        public string[]? ChannelNames { get; private set; }
        /// <summary>Marqueurs (temps en s, label) du fichier, vides si le format n'en a pas.</summary>
        public IReadOnlyList<(double Time, string Label)> Markers => _recording?.Markers ?? new List<(double, string)>();
        /// <summary>Vrai si le fichier passe par RecordingLoader (CSV générique, Excel, BrainVision).</summary>
        public bool IsRecordingFormat { get; private set; }
        /// <summary>Index (0-based) et nom du fichier en cours dans la liste de lecture.</summary>
        /// <summary>Avertissements de la dernière inspection (canaux manquants comblés à 0, etc.).</summary>
        public List<string> Warnings { get; } = new();
        public int    CurrentFileIndex { get; private set; }
        public string CurrentFileName  { get; private set; } = "";

        // ── Événements ─────────────────────────────────────────────────────
        public event Action<EEGSample>? SampleReady;
        public event Action<string>?    StatusChanged;
        public event Action?            PlaybackFinished;
        /// <summary>Marqueur rejoué (temps en s depuis le début, label).</summary>
        public event Action<double, string>? MarkerReady;
        /// <summary>Passage au fichier suivant de la liste : (index 0-based, total, nom).</summary>
        public event Action<int, int, string>? FileChanged;

        // ── Interne ────────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task?                    _task;
        private readonly SemaphoreSlim   _pauseSem = new(1, 1);

        // Données pré-chargées en mémoire (pour fichiers < 100 MB)
        private List<EEGSample>? _frames;
        private bool             _isStreaming = false;  // lecture streaming si fichier > 100 MB
        private Recording?       _recording;            // formats génériques (CSV, Excel, BrainVision)
        private List<Recording>? _playlist;             // plusieurs fichiers, tous pré-chargés

        // ════════════════════════════════════════════════════════════════════
        // CHARGEMENT
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Inspecte le fichier et retourne les métadonnées sans charger tout en mémoire.
        /// Lancer avant Start() pour afficher les infos à l'utilisateur.
        /// </summary>
        public (bool ok, string info, int channels, int sampleRate, long frames) Inspect()
        {
            if (Files.Count > 1) return InspectPlaylist();
            if (FilePaths.Count == 1) FilePath = FilePaths[0];
            return InspectSingle();
        }

        /// <summary>Inspection d'un seul fichier (FilePath), quel que soit le contenu de FilePaths.</summary>
        private (bool ok, string info, int channels, int sampleRate, long frames) InspectSingle()
        {
            if (!File.Exists(FilePath))
                return (false, $"Fichier non trouvé : {FilePath}", 0, 0, 0);

            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            try
            {
                bool legacyCsv = ext is ".csv" or ".txt" && RecordingLoader.IsLegacyBioSynthCsv(FilePath);
                if (legacyCsv)
                    return InspectCsv();
                if (ext == ".bin" || (ext == ".eeg" && !RecordingLoader.IsSupported(FilePath)))
                    return InspectBin();
                if (RecordingLoader.IsSupported(FilePath))
                    return InspectRecording();
                return (false, $"Format non supporté : {ext}. Utiliser .csv, .bin, .xlsx ou .vhdr", 0, 0, 0);
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

        private bool IsLegacyFormat(string path)
        {
            if (ForceGenericFormat) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".bin"
                || (ext is ".csv" or ".txt" && RecordingLoader.IsLegacyBioSynthCsv(path))
                || (ext == ".eeg" && !RecordingLoader.IsSupported(path));
        }

        /// <summary>Charge un fichier de n'importe quel format en Recording (les formats maison sont convertis).</summary>
        private Recording LoadAsRecording(string path)
        {
            if (!IsLegacyFormat(path)) return RecordingLoader.Open(path);

            // Formats maison : on réutilise les parseurs historiques puis on convertit.
            string saved = FilePath; FilePath = path;
            try
            {
                var (ok, info, ch, sr, _) = InspectSingle();
                if (!ok) throw new InvalidDataException(info);
                ChannelCount = ch; SampleRate = sr;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                var frames = ext is ".csv" or ".txt" ? LoadCsv() : LoadBin();
                var times = new double[frames.Count];
                var data  = new double[frames.Count][];
                long t0 = frames.Count > 0 ? frames[0].Timestamp : 0;
                for (int i = 0; i < frames.Count; i++)
                {
                    times[i] = (frames[i].Timestamp - t0) / 1e6;
                    data[i]  = frames[i].Channels;
                }
                return new Recording
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    ChannelNames = Enumerable.Range(0, ch).Select(i => BioSynth.ChannelNames.GetChannelName(i, ch)).ToArray(),
                    SampleRate = sr, Times = times, Data = data, Unit = "µV",
                };
            }
            finally { FilePath = saved; }
        }

        private (bool, string, int, int, long) InspectPlaylist()
        {
            var notFound = Files.FirstOrDefault(f => !File.Exists(f));
            if (notFound != null) return (false, $"Fichier non trouvé : {notFound}", 0, 0, 0);
            try
            {
                var list = new List<Recording>(Files.Count);
                foreach (var path in Files) list.Add(LoadAsRecording(path));

                Warnings.Clear();

                // Union des canaux : ordre du premier fichier, puis les canaux inédits des suivants.
                // Un canal absent d'un fichier est rejoué à 0 sur toute sa durée, avec avertissement.
                var union = new List<string>();
                foreach (var r in list)
                    foreach (var n in r.ChannelNames)
                        if (!union.Contains(n, StringComparer.OrdinalIgnoreCase)) union.Add(n);
                var target = union.ToArray();

                for (int i = 0; i < list.Count; i++)
                {
                    var missing = target.Where(n => !list[i].ChannelNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (missing.Count > 0)
                        Warnings.Add($"{list[i].Name} : canal{(missing.Count > 1 ? "aux" : "")} manquant{(missing.Count > 1 ? "s" : "")} mis à 0 : {string.Join(", ", missing)}");
                    list[i] = list[i].RemapChannels(target);
                }

                var first = list[0];
                var rates = list.Select(r => r.EffectiveSampleRate).Distinct().ToList();
                if (rates.Count > 1)
                    Warnings.Add($"Fréquences différentes entre fichiers : {string.Join(", ", rates)} Hz (chacun est rejoué à sa propre cadence)");

                _playlist = list;
                long total = list.Sum(r => (long)r.SampleCount);
                double dur = list.Sum(r => r.Duration);
                string fs = first.SampleRate is double rate ? $"{rate:F1} Hz" : "irrégulier";
                string info = $"{list.Count} fichiers — {target.Length} canaux, {fs}, {total:N0} échantillons, {dur:F1} s au total";
                if (Warnings.Count > 0) info += "\n⚠ " + string.Join("\n⚠ ", Warnings);
                return (true, info, target.Length, first.EffectiveSampleRate, total);
            }
            catch (Exception ex)
            {
                return (false, $"Erreur lecture : {ex.Message}", 0, 0, 0);
            }
        }

        private (bool, string, int, int, long) InspectRecording()
        {
            // Les fichiers sont chargés entièrement : on garde l'objet pour Start().
            if (_recording == null || !string.Equals(_recording.Name, Path.GetFileNameWithoutExtension(FilePath)))
                _recording = RecordingLoader.Open(FilePath);
            var r = _recording;
            return (true, r.Summary(), r.ChannelCount, r.EffectiveSampleRate, r.SampleCount);
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

            if (Files.Count > 1)
            {
                // Liste de lecture : tout est pré-chargé par InspectPlaylist()
                IsRecordingFormat = true;
                _recording   = null;
                ChannelNames = _playlist![0].ChannelNames;
                _frames      = null;
                _isStreaming = false;
                _cts  = new CancellationTokenSource();
                _task = Task.Run(() => PlayLoop(_cts.Token));
                IsRunning = true;
                StatusChanged?.Invoke($"Lecture : {info}");
                return;
            }
            _playlist = null;

            IsRecordingFormat = !IsLegacyFormat(FilePath);

            if (IsRecordingFormat)
            {
                _recording ??= RecordingLoader.Open(FilePath);
                ChannelNames = _recording.ChannelNames;
                _frames      = null;
                _isStreaming = false;
                _cts  = new CancellationTokenSource();
                _task = Task.Run(() => PlayLoop(_cts.Token));
                IsRunning = true;
                StatusChanged?.Invoke($"Lecture : {_recording.Summary()}");
                return;
            }
            ChannelNames = null;

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
            long   frameOffset = 0;   // progression cumulée (liste de lecture)
            double timeOffset  = 0;   // secondes cumulées pour les timestamps continus
            do
            {
                CurrentFrame = 0;

                if (_playlist != null)
                {
                    for (int k = 0; k < _playlist.Count && !ct.IsCancellationRequested; k++)
                    {
                        var rec = _playlist[k];
                        CurrentFileIndex = k;
                        CurrentFileName  = rec.Name;
                        FileChanged?.Invoke(k, _playlist.Count, rec.Name);
                        StatusChanged?.Invoke($"Fichier {k + 1}/{_playlist.Count} : {rec.Name}");
                        await PlayRecording(rec, ct, frameOffset, ContinuousTimestamps ? timeOffset : 0);
                        frameOffset += rec.SampleCount;
                        // Le fichier suivant commence un intervalle après la fin du précédent
                        timeOffset  += rec.Duration + 1.0 / rec.EffectiveSampleRate;
                    }
                    frameOffset = 0;   // la barre de progression repart à zéro à chaque boucle, pas les timestamps
                }
                else if (_recording != null && IsRecordingFormat)
                    await PlayRecording(_recording, ct);
                else if (_isStreaming)
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

        /// <summary>
        /// Rejeu par rattrapage : on avance une horloge « temps enregistrement » proportionnelle
        /// au temps mur × Speed, et on émet tous les échantillons dont le timestamp est dépassé.
        /// Fonctionne pour les fréquences fixes comme pour les timestamps irréguliers, sans dérive
        /// cumulative liée à la granularité de Task.Delay.
        /// </summary>
        private async Task PlayRecording(Recording rec, CancellationToken ct,
                                         long frameOffset = 0, double timeOffsetS = 0)
        {
            long tsOffsetUs = (long)(timeOffsetS * 1_000_000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double lastWall = 0, tRec = 0;
            int i = 0, m = 0;
            var markers = rec.Markers.OrderBy(x => x.Time).ToList();

            while (i < rec.SampleCount && !ct.IsCancellationRequested)
            {
                if (IsPaused)
                {
                    try { await _pauseSem.WaitAsync(ct); } catch (OperationCanceledException) { break; }
                    _pauseSem.Release();
                    lastWall = sw.Elapsed.TotalSeconds;   // ne pas rattraper le temps passé en pause
                }

                double now = sw.Elapsed.TotalSeconds;
                tRec += (now - lastWall) * Math.Max(Speed, 0.1);
                lastWall = now;

                int emitted = 0;
                while (i < rec.SampleCount && rec.Times[i] <= tRec)
                {
                    var s = new EEGSample(rec.ChannelCount) { Timestamp = tsOffsetUs + (long)(rec.Times[i] * 1_000_000) };
                    Array.Copy(rec.Data[i], s.Channels, rec.ChannelCount);
                    SampleReady?.Invoke(s);
                    i++; emitted++;
                }
                while (m < markers.Count && markers[m].Time <= tRec)
                {
                    MarkerReady?.Invoke(timeOffsetS + markers[m].Time, markers[m].Label);
                    m++;
                }
                CurrentFrame = frameOffset + i;

                if (emitted == 0)
                {
                    try { await Task.Delay(2, ct); } catch (OperationCanceledException) { break; }
                }
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
