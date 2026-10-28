using System.Collections.Concurrent;

namespace BioSynth.App;

/// <summary>
/// Équivalent sans interface de MainWindow.xaml.cs : possède les générateurs, le replay,
/// l'inlet LSL et les flux LSL sortants, et prépare à ~30 Hz une « trame » d'affichage
/// que le serveur diffuse aux navigateurs connectés.
///
/// Règles de concurrence :
///   - les commandes (start/stop, options) sont sérialisées par <see cref="_cmd"/> ;
///   - les callbacks des moteurs (threads de génération) n'écrivent que dans des files
///     concurrentes ou des champs volatils, et poussent directement vers LSL (sans perte
///     liée au rythme d'affichage, contrairement à l'ancienne version WPF qui poussait l'EEG depuis l'UI) ;
///   - un callback qui doit arrêter une modalité (fin de replay) passe par Task.Run,
///     comme Dispatcher.BeginInvoke dans l'ancienne version WPF, pour ne jamais attendre le thread appelant.
/// </summary>
public sealed class SimulatorHost : IDisposable
{
    private readonly object _cmd = new();

    public BrainZoneController Zones { get; } = new();
    public bool   LslAvailable { get; }
    public string DataDir      { get; }

    public SimulatorHost()
    {
        LslAvailable = Lsl.IsAvailable();
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BioSynth");
        try { Directory.CreateDirectory(DataDir); } catch { DataDir = Directory.GetCurrentDirectory(); }
    }

    // ═════════════════════════════════════════════════════════════════════
    // EEG
    // ═════════════════════════════════════════════════════════════════════

    private const int MAX_QUEUE = 8192;
    private EEGGenerator?  _gen;
    private EEGDataReplay? _replay;
    private EEGLslInlet?   _inlet;
    private volatile bool  _eegRunning;
    private string   _eegMode = "generate";          // generate | replay | lsl
    private int      _channelCount = 8, _sampleRate = 256;
    private string[]? _fileLabels;                   // noms réels (replay / inlet)
    private int      _displayChannels = 4;
    private int      _eegEpoch;
    private long     _eegSamples;
    private readonly ConcurrentQueue<EEGSample> _eegQ = new();
    private readonly Slot<EEGLslStream>    _eegLsl    = new();
    private readonly Slot<MarkerLslStream> _markerLsl = new();
    private LslOptions _eegLslOpt = new();
    private volatile string _eegStatus = "Prêt";
    private volatile string _eegRec    = "EEG IDLE";
    private readonly double[] _power = new double[64];
    private int _powerTick;
    private List<LslStreamInfo> _discovered = new();
    private volatile string _inletStatus = "○ Non connecté";
    private volatile string _inletInfo   = "Aucun flux sélectionné";
    private volatile bool   _discovering;

    public object StartEeg(EegStartRequest r)
    {
        lock (_cmd)
        {
            if (_eegRunning) StopEegCore();
            _eegLslOpt = r.Lsl ?? new LslOptions();
            return r.Source switch
            {
                "replay" => StartReplay(r),
                "lsl"    => StartInlet(r),
                _        => StartGenerator(r),
            };
        }
    }

    private object StartGenerator(EegStartRequest r)
    {
        var cfg = new EEGConfig
        {
            ChannelCount = r.Channels is 8 or 16 or 32 or 64 ? r.Channels : 8,
            SampleRate   = r.SampleRate > 0 ? Math.Min(r.SampleRate, 20000) : 256,
            OutputMode   = ParseOutput(r.Output, OutputMode.File),
            DataFormat   = r.Format switch { "Binary" => DataFormat.BinaryFloat32, "BDF" => DataFormat.BDF, _ => DataFormat.CSV },
            FilePath     = r.FilePath,
            StreamHost   = r.Host,
            StreamPort   = r.Port,
            AddArtifacts = r.Artifacts,
            NoiseLevel   = Math.Clamp(r.Noise, 0, 1),
        };
        if (CheckOutput(cfg.OutputMode, cfg.FilePath, cfg.StreamHost) is string err) return Fail(err);

        ResetEegPipeline(cfg.ChannelCount, cfg.SampleRate, null);
        _gen = new EEGGenerator(cfg) { ZoneController = Zones };
        _gen.SampleGenerated += OnEegSample;
        _gen.StatusChanged   += m => _eegStatus = m;
        _eegMode = "generate";
        OpenEegLsl();
        _eegRunning = true;
        _eegRec = "● EEG REC";
        _gen.Start();
        return Ok();
    }

    private object StartReplay(EegStartRequest r)
    {
        var paths = (r.ReplayPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0 || paths.Any(p => !File.Exists(p)))
            return Fail("Sélectionner un ou plusieurs fichiers valides avant de démarrer.");

        var replay = new EEGDataReplay { FilePath = paths[0], Speed = ClampSpeed(r.Speed), Loop = r.Loop };
        if (paths.Count > 1) replay.FilePaths.AddRange(paths);
        var (ok, info, inspCh, inspSr, _) = replay.Inspect();
        if (!ok) { replay.Dispose(); return Fail(info); }

        _replay = replay;
        replay.SampleReady   += OnEegSample;
        replay.StatusChanged += m => _eegStatus = m;
        replay.FileChanged   += (i, n, _) => _eegRec = n > 1 ? $"● REPLAY {i + 1}/{n}" : "● REPLAY";
        replay.MarkerReady   += (t, label) =>
        {
            _markerLsl.Use(m => m.Push(label));
            _eegStatus = $"Marqueur {label} à {t:F2} s";
        };
        replay.PlaybackFinished += () =>
        {
            if (replay.Loop) return;
            Task.Run(() => { lock (_cmd) if (ReferenceEquals(_replay, replay)) StopEegCore("Lecture terminée."); });
        };

        // Les tampons d'affichage sont dimensionnés avant le premier échantillon
        ResetEegPipeline(Math.Max(1, inspCh), inspSr > 0 ? inspSr : 256, null);
        replay.Start();
        _channelCount = Math.Max(1, replay.ChannelCount);
        _sampleRate   = replay.SampleRate;
        _fileLabels   = replay.ChannelNames;
        _eegMode = "replay";
        _eegRunning = true;
        _eegRec = paths.Count > 1 ? $"● REPLAY 1/{paths.Count}" : "● REPLAY";
        OpenEegLsl();
        return Ok(info);
    }

    private object StartInlet(EegStartRequest r)
    {
        if (!LslAvailable) return Fail("liblsl introuvable.\n" + Lsl.GetDllHint());
        int idx = r.LslStreamIndex;
        if (idx < 0 || idx >= _discovered.Count)
            return Fail("Cliquer sur « Découvrir les flux LSL » puis sélectionner un flux dans la liste.");

        var inlet = new EEGLslInlet();
        if (!inlet.Connect(_discovered[idx]))
        {
            inlet.Dispose();
            return Fail("Impossible de se connecter au flux LSL sélectionné. Vérifier que la source est toujours active.");
        }
        ResetEegPipeline(inlet.ChannelCount, inlet.SampleRate, null);
        _inlet = inlet;
        inlet.SampleReady   += OnEegSample;
        inlet.StatusChanged += m => { _eegStatus = m; _inletStatus = m; };
        inlet.Start();
        _eegMode = "lsl";
        _eegRunning = true;
        _eegRec = "● LSL IA";
        OpenEegLsl();
        return Ok();
    }

    public object StopEeg() { lock (_cmd) { StopEegCore(); return Ok(); } }

    private void StopEegCore(string? status = null)
    {
        _eegRunning = false;
        _gen?.Stop(); _gen?.Dispose(); _gen = null;
        if (_replay != null) { _replay.Stop(); _replay.Dispose(); _replay = null; }
        if (_inlet  != null) { _inlet.Stop();  _inlet.Dispose();  _inlet  = null; _inletStatus = "○ Non connecté"; }
        _eegLsl.Set(null);
        _markerLsl.Set(null);
        _eegRec = "EEG IDLE";
        _eegStatus = status ?? "EEG arrêté.";
    }

    private void ResetEegPipeline(int channels, int sampleRate, string[]? labels)
    {
        while (_eegQ.TryDequeue(out _)) { }
        Array.Clear(_power);
        _channelCount = channels;
        _sampleRate   = sampleRate;
        _fileLabels   = labels;
        _eegSamples   = 0;
        _eegEpoch++;
    }

    private void OnEegSample(EEGSample s)
    {
        Interlocked.Increment(ref _eegSamples);
        _eegLsl.Use(l => l.Push(s));
        if (_eegQ.Count < MAX_QUEUE) _eegQ.Enqueue(s);
    }

    private void OpenEegLsl()
    {
        if (!LslAvailable || !_eegLslOpt.Enabled) { _eegLsl.Set(null); _markerLsl.Set(null); return; }
        try
        {
            string name = string.IsNullOrWhiteSpace(_eegLslOpt.Name) ? "BioSynth_EEG" : _eegLslOpt.Name!.Trim();
            _eegLsl.Set(new EEGLslStream(_channelCount, _sampleRate, name, _eegMode == "replay" ? _replay?.ChannelNames : null));
            _markerLsl.Set(_eegMode == "replay" && _replay != null && _replay.Markers.Count > 0
                ? new MarkerLslStream(name + "_Markers") : null);
        }
        catch (Exception ex) { _eegStatus = $"LSL EEG erreur : {ex.Message}"; }
    }

    public object SetDisplayChannels(int n)
    {
        _displayChannels = Math.Clamp(n, 1, 64);
        return Ok();
    }

    public object SetReplaySpeed(double speed)
    {
        lock (_cmd) { if (_replay != null) _replay.Speed = ClampSpeed(speed); }
        return Ok();
    }

    public object ToggleReplayPause()
    {
        lock (_cmd)
        {
            if (_replay == null) return Fail("Aucun replay en cours.");
            if (_replay.IsPaused) _replay.Resume(); else _replay.Pause();
            return Ok(_replay.IsPaused ? "paused" : "playing");
        }
    }

    public object InspectEegFiles(string[] paths)
    {
        paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (paths.Length == 0) return Fail("Aucun fichier.");
        using var tmp = new EEGDataReplay { FilePath = paths[0] };
        if (paths.Length > 1) tmp.FilePaths.AddRange(paths);
        var (ok, info, ch, sr, frames) = tmp.Inspect();
        return new { ok, info, channels = ch, sampleRate = sr, frames };
    }

    public object DiscoverLsl()
    {
        if (!LslAvailable) return Fail("liblsl introuvable.\n" + Lsl.GetDllHint());
        if (_discovering) return Fail("Recherche déjà en cours.");
        _discovering = true;
        _inletInfo = "Recherche en cours...";
        _inletStatus = "○ Recherche des flux LSL...";
        try
        {
            var streams = EEGLslInlet.DiscoverStreams(2.0);
            lock (_cmd) _discovered = streams;
            if (streams.Count == 0)
            {
                _inletInfo = "Aucun flux EEG trouvé sur le réseau.";
                _inletStatus = "○ Non connecté";
            }
            else
            {
                _inletInfo = $"{streams.Count} flux trouvé(s). Sélectionner pour se connecter.";
                _inletStatus = "○ Sélectionner un flux";
            }
            return new
            {
                ok = true,
                streams = streams.Select(s => new { label = s.ToString(), name = s.Name, type = s.Type, channels = s.ChannelCount, sampleRate = s.SampleRate }).ToArray(),
            };
        }
        finally { _discovering = false; }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Zones cérébrales
    // ═════════════════════════════════════════════════════════════════════

    public object SetZones(double[] act)
    {
        for (int i = 0; i < Math.Min(act.Length, Zones.Zones.Length); i++)
            Zones.Zones[i].Activation = Math.Clamp(act[i], 0, 1);
        return ZonesState();
    }

    public object ApplyPreset(string preset)
    {
        Zones.ApplyPreset(preset);
        return ZonesState();
    }

    public object ZonesState() => new
    {
        ok = true,
        activation = Zones.Zones.Select(z => z.Activation).ToArray(),
    };

    // ═════════════════════════════════════════════════════════════════════
    // Eye Tracking
    // ═════════════════════════════════════════════════════════════════════

    private EyeTrackingGenerator? _etGen;
    private EyeTrackingReplay?    _etReplay;
    private volatile bool _etRunning;
    private int  _etEpoch;
    private int  _etRate = 120;
    private readonly ConcurrentQueue<EyeSample> _etQ = new();
    private readonly Slot<EyeTrackingLslStream> _etLsl = new();
    private LslOptions _etLslOpt = new();
    private volatile string _etStatus = "Prêt";
    private volatile string _etRec    = "ET IDLE";
    private readonly EtDisplayState _et = new();

    public object StartEt(EtStartRequest r)
    {
        lock (_cmd)
        {
            if (_etRunning) StopEtCore();
            _etLslOpt = r.Lsl ?? new LslOptions();
            while (_etQ.TryDequeue(out _)) { }
            _et.Reset();
            _etEpoch++;

            var cfg = new EyeTrackingConfig
            {
                SampleRate            = r.SampleRate is 60 or 120 or 250 or 500 or 1000 ? r.SampleRate : 120,
                ScreenWidth           = r.ScreenW > 0 ? r.ScreenW : 1920,
                ScreenHeight          = r.ScreenH > 0 ? r.ScreenH : 1080,
                OutputMode            = ParseOutput(r.Output, OutputMode.UdpStream),
                FilePath              = r.FilePath,
                StreamHost            = r.Host,
                StreamPort            = r.Port,
                SimulateSaccades      = r.Saccades,
                SimulateBlinks        = r.Blinks,
                SimulatePupilDilation = r.Pupil,
                NoiseLevel            = Math.Clamp(r.Noise, 0, 1),
                IncludeRawColumns     = r.RawColumns,
            };
            if (CheckOutput(cfg.OutputMode, cfg.FilePath, cfg.StreamHost) is string err) return Fail(err);

            if (r.Source == "replay")
            {
                var paths = (r.ReplayPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (paths.Count == 0 || paths.Any(p => !File.Exists(p)))
                    return Fail("Sélectionner un ou plusieurs fichiers oculométriques valides avant de démarrer.");

                var rep = new EyeTrackingReplay(paths) { Speed = ClampSpeed(r.Speed), Loop = r.Loop, OutputConfig = cfg };
                var (ok, info, _, _, _) = rep.Inspect();
                if (!ok) { rep.Dispose(); return Fail(info); }

                _etReplay = rep;
                rep.SampleGenerated += OnEtSample;
                rep.StatusChanged   += m => _etStatus = m;
                rep.FileChanged     += (i, n, _) => _etRec = n > 1 ? $"● ET REPLAY {i + 1}/{n}" : "● ET REPLAY";
                rep.PlaybackFinished += () =>
                {
                    if (rep.Loop) return;
                    Task.Run(() => { lock (_cmd) if (ReferenceEquals(_etReplay, rep)) StopEtCore("Replay ET terminé."); });
                };
                _etRunning = true;
                rep.Start();                 // fixe ChannelNames avant l'ouverture du flux LSL
                _etRate = Math.Max(1, rep.SampleRate);
                OpenEtLsl();
                _et.SetRawMode(rep.HasGazeMapping ? null : rep.ChannelNames);
                _etStatus = rep.HasGazeMapping
                    ? "Replay ET : colonnes GazeX/GazeY reconnues."
                    : "Replay ET : colonnes brutes (pas de GazeX/GazeY), traces des colonnes du fichier.";
                _etRec = "● ET REPLAY";
                return Ok(info);
            }

            _etGen = new EyeTrackingGenerator(cfg);
            _etGen.SampleGenerated += OnEtSample;
            _etGen.StatusChanged   += m => _etStatus = m;
            _etRate = cfg.SampleRate;
            _et.SetRawMode(null);
            OpenEtLsl();
            _etRunning = true;
            _etGen.Start();
            _etRec = "● ET REC";
            return Ok();
        }
    }

    public object StopEt() { lock (_cmd) { StopEtCore(); return Ok(); } }

    private void StopEtCore(string? status = null)
    {
        _etRunning = false;
        _etGen?.Stop(); _etGen?.Dispose(); _etGen = null;
        if (_etReplay != null) { _etReplay.Stop(); _etReplay.Dispose(); _etReplay = null; }
        _etLsl.Set(null);
        _etRec = "ET IDLE";
        _etStatus = status ?? "ET arrêté.";
    }

    private void OnEtSample(EyeSample s)
    {
        _etLsl.Use(l => l.Push(s));
        if (_etQ.Count < 4096) _etQ.Enqueue(s);
    }

    private void OpenEtLsl()
    {
        if (!LslAvailable || !_etLslOpt.Enabled) { _etLsl.Set(null); return; }
        try
        {
            string name = string.IsNullOrWhiteSpace(_etLslOpt.Name) ? "BioSynth_EyeTracking" : _etLslOpt.Name!.Trim();
            _etLsl.Set(_etReplay != null
                ? new EyeTrackingLslStream(Math.Max(1, _etReplay.SampleRate), name, _etReplay.ChannelNames)
                : new EyeTrackingLslStream(_etRate, name));
        }
        catch (Exception ex) { _etStatus = $"LSL ET erreur : {ex.Message}"; }
    }

    public object SetEtReplaySpeed(double speed)
    {
        lock (_cmd) { if (_etReplay != null) _etReplay.Speed = ClampSpeed(speed); }
        return Ok();
    }

    public object ToggleEtReplayPause()
    {
        lock (_cmd)
        {
            if (_etReplay == null) return Fail("Aucun replay ET en cours.");
            if (_etReplay.IsPaused) _etReplay.Resume(); else _etReplay.Pause();
            return Ok(_etReplay.IsPaused ? "paused" : "playing");
        }
    }

    public object InspectEtFiles(string[] paths)
    {
        paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (paths.Length == 0) return Fail("Aucun fichier.");
        using var tmp = new EyeTrackingReplay(paths);
        var (ok, info, ch, sr, frames) = tmp.Inspect();
        return new { ok, info, channels = ch, sampleRate = sr, frames };
    }

    // ═════════════════════════════════════════════════════════════════════
    // Face Tracking
    // ═════════════════════════════════════════════════════════════════════

    private FaceTrackingGenerator? _ftGen;
    private volatile bool _ftRunning;
    private string _ftMode = "FACE";
    private int    _ftRate = 30;
    private int    _ftEpoch;
    private readonly ConcurrentQueue<EmotionSample> _ftEmoQ  = new();
    private readonly ConcurrentQueue<FaceSample>    _ftFaceQ = new();
    private readonly Slot<FaceTrackingLslStream> _ftLsl = new();
    private LslOptions _ftLslOpt = new();
    private volatile string _ftStatus = "Prêt";
    private volatile string _ftRec    = "FT IDLE";
    private int   _ftTrans; private float _ftArousalSum, _ftValenceSum; private int _ftStatCount;
    private EmotionLabel _ftLastEmo = EmotionLabel.Neutral;

    public object StartFt(FtStartRequest r)
    {
        lock (_cmd)
        {
            if (_ftRunning) StopFtCore();
            _ftLslOpt = r.Lsl ?? new LslOptions();
            while (_ftFaceQ.TryDequeue(out _)) { }
            while (_ftEmoQ.TryDequeue(out _)) { }
            _ftTrans = 0; _ftArousalSum = 0; _ftValenceSum = 0; _ftStatCount = 0;
            _ftLastEmo = EmotionLabel.Neutral;
            _ftEpoch++;

            var cfg = new FaceTrackingConfig
            {
                SampleRate       = r.SampleRate is 15 or 24 or 30 or 60 or 120 ? r.SampleRate : 30,
                StreamMode       = r.Mode == "Emotion" ? FaceStreamMode.EmotionOnly : FaceStreamMode.FaceLandmarks,
                OutputMode       = ParseOutput(r.Output, OutputMode.UdpStream),
                FilePath         = r.FilePath,
                StreamHost       = r.Host,
                StreamPort       = r.Port,
                NoiseLevel       = Math.Clamp(r.Noise, 0, 1),
                SimulateMicro    = r.Micro,
                SimulateHeadMove = r.HeadMove,
                EmotionChangeSec = Math.Clamp(r.EmotionSec, 1, 15),
            };
            if (CheckOutput(cfg.OutputMode, cfg.FilePath, cfg.StreamHost) is string err) return Fail(err);

            _ftGen = new FaceTrackingGenerator(cfg);
            _ftGen.FaceSampleGenerated    += OnFtFace;
            _ftGen.EmotionSampleGenerated += OnFtEmotion;
            _ftGen.StatusChanged          += m => _ftStatus = m;
            _ftMode = cfg.StreamMode == FaceStreamMode.EmotionOnly ? "ÉMOTION" : "FACE";
            _ftRate = cfg.SampleRate;
            if (LslAvailable && _ftLslOpt.Enabled)
            {
                try
                {
                    string? n = string.IsNullOrWhiteSpace(_ftLslOpt.Name) ? null : _ftLslOpt.Name!.Trim();
                    _ftLsl.Set(new FaceTrackingLslStream(cfg.SampleRate,
                        n != null ? n + "_Emotions" : "BioSynth_Emotions",
                        n != null ? n + "_HeadPose" : "BioSynth_Face"));
                }
                catch (Exception ex) { _ftStatus = $"LSL FT erreur : {ex.Message}"; }
            }
            _ftRunning = true;
            _ftGen.Start();
            _ftRec = "● FT REC";
            return Ok();
        }
    }

    public object StopFt() { lock (_cmd) { StopFtCore(); return Ok(); } }

    private void StopFtCore()
    {
        _ftRunning = false;
        _ftGen?.Stop(); _ftGen?.Dispose(); _ftGen = null;
        _ftLsl.Set(null);
        _ftRec = "FT IDLE";
        _ftStatus = "FT arrêté.";
    }

    private void OnFtFace(FaceSample s)       { _ftLsl.Use(l => l.PushFace(s));    if (_ftFaceQ.Count < 120) _ftFaceQ.Enqueue(s); }
    private void OnFtEmotion(EmotionSample s) { _ftLsl.Use(l => l.PushEmotion(s)); if (_ftEmoQ.Count  < 120) _ftEmoQ.Enqueue(s); }

    // ═════════════════════════════════════════════════════════════════════
    // LSL : activation à chaud (cases à cocher pendant l'acquisition)
    // ═════════════════════════════════════════════════════════════════════

    public object ToggleLsl(LslToggleRequest r)
    {
        lock (_cmd)
        {
            var opt = new LslOptions { Enabled = r.Enabled, Name = r.Name };
            switch (r.Modality)
            {
                case "eeg": _eegLslOpt = opt; if (_eegRunning) OpenEegLsl(); else { _eegLsl.Set(null); _markerLsl.Set(null); } break;
                case "et":  _etLslOpt  = opt; if (_etRunning)  OpenEtLsl();  else _etLsl.Set(null); break;
                case "ft":
                    _ftLslOpt = opt;
                    if (!r.Enabled || !LslAvailable) _ftLsl.Set(null);
                    else if (_ftRunning)
                    {
                        try
                        {
                            string? n = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name!.Trim();
                            _ftLsl.Set(new FaceTrackingLslStream(_ftRate,
                                n != null ? n + "_Emotions" : "BioSynth_Emotions",
                                n != null ? n + "_HeadPose" : "BioSynth_Face"));
                        }
                        catch (Exception ex) { _ftStatus = $"LSL FT erreur : {ex.Message}"; }
                    }
                    break;
            }
            return Ok();
        }
    }

    private object LslState()
    {
        if (!LslAvailable)
            return new { available = false, text = "⚠ liblsl introuvable : voir README_LSL.txt", active = Array.Empty<string>() };
        var active = new List<string>();
        if (_eegLsl.IsOpen(s => s.IsOpen)) active.Add("EEG");
        if (_etLsl.IsOpen(s => s.IsOpen))  active.Add("ET");
        if (_ftLsl.IsOpen(s => s.IsOpen))  active.Add("FT");
        return new
        {
            available = true,
            text = active.Count > 0 ? $"● LSL actif : {string.Join(", ", active)}" : "○ LSL — cocher pour activer",
            active = active.ToArray(),
        };
    }

    // ═════════════════════════════════════════════════════════════════════
    // Trame d'affichage (~30 Hz)
    // ═════════════════════════════════════════════════════════════════════

    private int _tick;

    /// <summary>
    /// Vide les files et construit la trame complète (interface principale) et la trame TopoMap.
    /// Appelé uniquement par le diffuseur, sur un seul thread.
    /// </summary>
    public (object full, object? topo) Tick()
    {
        lock (_cmd)
        {
            _tick++;
            var eeg  = DrainEeg();
            var et   = DrainEt();
            var ft   = DrainFt();
            object? topo = null;
            if (++_powerTick >= 10 || _tick == 1)
            {
                _powerTick = 0;
                topo = TopoState();
            }
            var full = new { t = _tick, eeg, et, ft, lsl = LslState() };
            return (full, topo);
        }
    }

    private string LabelOf(int i)
        => _fileLabels != null && i < _fileLabels.Length && !string.IsNullOrEmpty(_fileLabels[i])
            ? _fileLabels[i]
            : ChannelNames.GetChannelName(i, _channelCount);

    private object DrainEeg()
    {
        int nDisp = Math.Min(_displayChannels, Math.Max(1, _channelCount));
        var cols = new List<float>[nDisp];
        for (int c = 0; c < nDisp; c++) cols[c] = new List<float>(16);

        int n = 0;
        while (n < 2048 && _eegQ.TryDequeue(out var s))
        {
            int chs = s.Channels.Length;
            for (int c = 0; c < nDisp; c++)
            {
                double v = c < chs ? s.Channels[c] : 0;
                cols[c].Add(double.IsFinite(v) ? (float)Math.Round(v, 2) : 0f);
            }
            // Puissance par canal (même lissage que l'ancienne version WPF), pour la TopoMap
            for (int c = 0; c < Math.Min(chs, _power.Length); c++)
            {
                double v = s.Channels[c];
                if (double.IsFinite(v)) _power[c] = _power[c] * 0.95 + Math.Abs(v) * 0.05 / 200.0;
            }
            n++;
        }

        long total = _gen?.TotalSamplesGenerated ?? Interlocked.Read(ref _eegSamples);
        object? replay = _replay == null ? null : new
        {
            pct    = Math.Round(_replay.ProgressPct, 1),
            frame  = _replay.CurrentFrame,
            paused = _replay.IsPaused,
        };
        return new
        {
            running  = _eegRunning,
            mode     = _eegMode,
            epoch    = _eegEpoch,
            rec      = _eegRec,
            status   = _eegStatus,
            channels = _channelCount,
            rate     = _sampleRate,
            labels   = Enumerable.Range(0, nDisp).Select(LabelOf).ToArray(),
            data     = cols.Select(c => c.ToArray()).ToArray(),
            samples  = total,
            duration = _sampleRate > 0 ? Math.Round(total / (double)_sampleRate, 1) : 0,
            replay,
            inlet    = new { status = _inletStatus, info = _inletInfo },
        };
    }

    private object TopoState()
    {
        int n = Math.Min(_channelCount, _power.Length);
        var labels = Enumerable.Range(0, n).Select(LabelOf).ToArray();
        return new
        {
            channels = _channelCount,
            rate     = _sampleRate,
            labels,
            region   = labels.Select(l => (int)BrainZoneController.RegionOf(l)).ToArray(),
            power    = _power.Take(n).Select(p => Math.Round(p, 4)).ToArray(),
            zones    = Zones.Zones.Select(z => Math.Round(z.Activation, 3)).ToArray(),
        };
    }

    private object DrainEt()
    {
        var pl = new List<float>(); var pr = new List<float>(); var raw = new List<float[]>();
        EyeSample? last = null;
        int guard = 0;
        while (guard++ < 4096 && _etQ.TryDequeue(out var s))
        {
            last = s;
            if (_et.RawMode && s.Raw != null)
            {
                raw.Add(_et.AcceptRaw(s.Raw));
                continue;
            }
            _et.AcceptStandard(s);
            pl.Add((float)Math.Round(s.PupilLeft, 3));
            pr.Add((float)Math.Round(s.PupilRight, 3));
        }

        long total = _etGen?.TotalSamplesGenerated ?? _etReplay?.TotalSamplesGenerated ?? 0;
        object? replay = _etReplay == null ? null : new { pct = Math.Round(_etReplay.ProgressPct, 1), paused = _etReplay.IsPaused };
        return new
        {
            running  = _etRunning,
            mode     = _etReplay != null ? "replay" : "generate",
            epoch    = _etEpoch,
            rec      = _etRec,
            status   = _etStatus,
            samples  = total,
            rawMode  = _et.RawMode,
            rawNames = _et.RawNames,
            trail    = _et.Trail,
            lastEvt  = _et.LastEvent,
            pupilL   = pl, pupilR = pr, raw,
            text     = _et.Text,
            stats    = new { fix = _et.Fix, sac = _et.Sac, blink = _et.Blink, avgVel = _et.VelCount > 0 ? Math.Round(_et.VelSum / _et.VelCount) : 0 },
            replay,
        };
    }

    private object DrainFt()
    {
        EmotionSample? emo = null; FaceSample? face = null;
        while (_ftEmoQ.TryDequeue(out var e))
        {
            emo = e;
            if (e.Dominant != _ftLastEmo) { _ftTrans++; _ftLastEmo = e.Dominant; }
            _ftArousalSum += e.Arousal; _ftValenceSum += e.Valence; _ftStatCount++;
        }
        while (_ftFaceQ.TryDequeue(out var f)) face = f;

        return new
        {
            running = _ftRunning,
            mode    = _ftMode,
            epoch   = _ftEpoch,
            rec     = _ftRec,
            status  = _ftStatus,
            samples = _ftGen?.TotalSamplesGenerated ?? 0,
            emo = emo == null ? null : new
            {
                dom = (int)emo.Dominant,
                scores = emo.Scores.Select(x => (float)Math.Round(x, 3)).ToArray(),
                arousal = Math.Round(emo.Arousal, 3),
                valence = Math.Round(emo.Valence, 3),
            },
            face = face == null ? null : new
            {
                lx = face.LandmarkX.Select(x => (float)Math.Round(x, 4)).ToArray(),
                ly = face.LandmarkY.Select(y => (float)Math.Round(y, 4)).ToArray(),
                pitch = Math.Round(face.RotPitch, 1), yaw = Math.Round(face.RotYaw, 1), roll = Math.Round(face.RotRoll, 1),
                z = Math.Round(face.PoseZ), conf = Math.Round(face.Confidence, 3),
                // Micro-expression signalée si une AU dépasse 0.35 (même règle que l'ancienne version WPF)
                micro = face.ActionUnits.Any(a => a > 0.35f),
            },
            stats = new
            {
                trans = _ftTrans,
                avgArousal = _ftStatCount > 0 ? Math.Round(_ftArousalSum / _ftStatCount, 2) : (double?)null,
                avgValence = _ftStatCount > 0 ? Math.Round(_ftValenceSum / _ftStatCount, 2) : (double?)null,
            },
        };
    }

    /// <summary>
    /// Modèle 3D anatomique facultatif pour la vue Cerveau 3D : brain.obj (ou Brain_Model.obj)
    /// dans le dossier de données (~/BioSynth) ou dans Assets/ à côté de l'exécutable.
    /// Sans modèle, l'interface génère un cerveau procédural.
    /// </summary>
    public string? FindBrainModel()
    {
        foreach (var dir in new[] { DataDir, Path.Combine(DataDir, "Assets"), Path.Combine(AppContext.BaseDirectory, "Assets") })
            foreach (var name in new[] { "brain.obj", "Brain_Model.obj" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Métadonnées statiques pour l'interface : positions 10-20, zones, valeurs par défaut.</summary>
    public object Meta() => new
    {
        lslAvailable = LslAvailable,
        lslHint      = Lsl.GetDllHint(),
        platform     = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        dataDir      = DataDir,
        brainModel   = FindBrainModel(),
        home         = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        electrodes   = BrainZoneController.ElectrodePositions.ToDictionary(k => k.Key, v => new[] { v.Value.X, v.Value.Y, v.Value.Z }),
        zones        = Zones.Zones.Select(z => new { label = z.Label, color = $"#{z.Color.R:X2}{z.Color.G:X2}{z.Color.B:X2}", activation = z.Activation }).ToArray(),
        channelNames = new[] { 8, 16, 32, 64 }.ToDictionary(n => n.ToString(), n => Enumerable.Range(0, n).Select(i => ChannelNames.GetChannelName(i, n)).ToArray()),
    };

    // ═════════════════════════════════════════════════════════════════════
    // Utilitaires
    // ═════════════════════════════════════════════════════════════════════

    private static OutputMode ParseOutput(string? tag, OutputMode fallback) => tag switch
    {
        "File" => OutputMode.File,
        "TCP"  => OutputMode.TcpStream,
        "UDP"  => OutputMode.UdpStream,
        "None" => OutputMode.None,
        _      => fallback,
    };

    /// <summary>Contrôles faits avant de lancer un générateur : ses erreurs de sortie surviendraient
    /// sinon dans la tâche de fond et l'acquisition s'arrêterait sans message.</summary>
    private static string? CheckOutput(OutputMode mode, string path, string host)
    {
        switch (mode)
        {
            case OutputMode.File:
                if (string.IsNullOrWhiteSpace(path)) return "Indiquer un chemin de fichier de sortie.";
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                }
                catch (Exception ex) { return $"Fichier de sortie inutilisable : {ex.Message}"; }
                return null;
            case OutputMode.TcpStream:
            case OutputMode.UdpStream:
                return System.Net.IPAddress.TryParse(host, out _) ? null : $"Adresse IP invalide : {host}";
            default:
                return null;
        }
    }

    private static double ClampSpeed(double s) => double.IsFinite(s) ? Math.Clamp(s, 0.1, 50) : 1.0;
    private static object Ok(string? info = null) => new { ok = true, info };
    private static object Fail(string error) => new { ok = false, error };

    public void Dispose()
    {
        lock (_cmd)
        {
            StopEegCore(); StopEtCore(); StopFtCore();
        }
    }
}

/// <summary>Référence protégée par verrou : un push LSL ne peut jamais viser un outlet en cours de destruction.</summary>
internal sealed class Slot<T> where T : class, IDisposable
{
    private readonly object _l = new();
    private T? _v;
    public void Set(T? v) { lock (_l) { _v?.Dispose(); _v = v; } }
    public void Use(Action<T> a) { lock (_l) { if (_v != null) a(_v); } }
    public bool IsOpen(Func<T, bool> f) { lock (_l) return _v != null && f(_v); }
}
