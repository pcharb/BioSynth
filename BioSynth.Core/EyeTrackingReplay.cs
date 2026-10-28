using System;
using System.Collections.Generic;
using System.Linq;

namespace BioSynth
{
    /// <summary>
    /// Rejeu d'enregistrements oculométriques réels (CSV, Excel, BrainVision), un ou plusieurs
    /// fichiers, avec cadencement sur les timestamps du fichier.
    ///
    /// Le moteur de lecture (chargement, liste de lecture, horloge, pause, vitesse, boucle) est
    /// celui d'EEGDataReplay ; cette classe ne fait que traduire chaque échantillon vers un
    /// EyeSample :
    ///   - les colonnes reconnues par leur nom (GazeX, GazeY, PupilL, ConfR, Blink, Velocity...)
    ///     alimentent les champs standards, ce qui rend les exports BioSynth relisibles ;
    ///   - toutes les colonnes numériques, reconnues ou non (ex. angles de regard), sont
    ///     conservées dans EyeSample.Raw avec leurs noms dans RawNames, et publiées telles quelles
    ///     sur le flux LSL.
    /// </summary>
    public class EyeTrackingReplay : IDisposable
    {
        private readonly EEGDataReplay _engine = new() { ForceGenericFormat = true };

        public event Action<EyeSample>?          SampleGenerated;
        public event Action<double, string>?     MarkerReady;
        public event Action<int, int, string>?   FileChanged;
        public event Action<string>?             StatusChanged;
        public event Action?                     PlaybackFinished;

        public bool     IsRunning   => _engine.IsRunning;
        public bool     IsPaused    => _engine.IsPaused;
        public double   Speed       { get => _engine.Speed; set => _engine.Speed = value; }
        public bool     Loop        { get => _engine.Loop;  set => _engine.Loop  = value; }
        public double   ProgressPct => _engine.ProgressPct;
        public string[]? ChannelNames => _engine.ChannelNames;
        public int      SampleRate  => _engine.SampleRate;
        public IReadOnlyList<string> Warnings => _engine.Warnings;
        public long     TotalSamplesGenerated { get; private set; }
        /// <summary>Configuration de sortie (UDP/TCP/fichier, IncludeRawColumns). Null = aucune sortie hors LSL.</summary>
        public EyeTrackingConfig? OutputConfig { get; set; }
        private IEyeOutput? _output;

        // Index des colonnes reconnues (-1 si absente)
        private int _iGx = -1, _iGy = -1, _iGxN = -1, _iGyN = -1, _iPl = -1, _iPr = -1,
                    _iCl = -1, _iCr = -1, _iBlink = -1, _iVel = -1;

        public EyeTrackingReplay(IEnumerable<string> files)
        {
            var list = files.ToList();
            _engine.FilePath = list.FirstOrDefault() ?? "";
            if (list.Count > 1) _engine.FilePaths.AddRange(list);

            _engine.SampleReady      += OnEngineSample;
            _engine.MarkerReady      += (t, l) => MarkerReady?.Invoke(t, l);
            _engine.FileChanged      += (i, n, name) => FileChanged?.Invoke(i, n, name);
            _engine.StatusChanged    += m => StatusChanged?.Invoke(m);
            _engine.PlaybackFinished += () => PlaybackFinished?.Invoke();
        }

        public (bool ok, string info, int channels, int sampleRate, long frames) Inspect() => _engine.Inspect();

        public void Start()
        {
            TotalSamplesGenerated = 0;
            if (OutputConfig != null)
            {
                _output = EyeOutputFactory.Create(OutputConfig);
                _output.WriteHeader();
            }
            // L'inspection précharge les fichiers ; on connaît donc les colonnes avant le premier échantillon.
            _engine.Inspect();
            _mapped = false;
            _engine.Start();
            EnsureMapped();
        }

        private bool _mapped = false;
        private readonly object _mapLock = new();
        private void EnsureMapped()
        {
            if (_mapped) return;
            lock (_mapLock)
            {
                if (_mapped) return;
                MapColumns(_engine.ChannelNames ?? Array.Empty<string>());
                _mapped = true;
            }
        }

        public void Stop()
        {
            _engine.Stop();
            _output?.Dispose(); _output = null;
        }
        public void Pause()  => _engine.Pause();
        public void Resume() => _engine.Resume();
        public void Dispose() { _engine.Dispose(); _output?.Dispose(); _output = null; }

        // ── Correspondance colonnes → champs EyeSample ────────────────────────

        private static readonly string[][] Aliases =
        {
            new[] { "gazex", "gaze_x", "gazex_px", "x", "posx", "gazepointx" },
            new[] { "gazey", "gaze_y", "gazey_px", "y", "posy", "gazepointy" },
            new[] { "gazexnorm", "gazex_norm", "xnorm", "normx" },
            new[] { "gazeynorm", "gazey_norm", "ynorm", "normy" },
            new[] { "pupill_mm", "pupill", "pupil_l", "pupilleft", "pupil_left", "pupil_l_mm", "pupildiameterleft" },
            new[] { "pupilr_mm", "pupilr", "pupil_r", "pupilright", "pupil_right", "pupil_r_mm", "pupildiameterright" },
            new[] { "confl", "conf_l", "confidenceleft", "confidence_l", "validityleft" },
            new[] { "confr", "conf_r", "confidenceright", "confidence_r", "validityright" },
            new[] { "blink", "isblinking", "blinking" },
            new[] { "velocity_dps", "velocity", "velocitydeg", "vel" },
        };

        private void MapColumns(string[] names)
        {
            var lower = names.Select(n => n.ToLowerInvariant().Replace(" ", "")).ToArray();
            int Find(string[] aliases) => Array.FindIndex(lower, n => aliases.Contains(n));
            _iGx = Find(Aliases[0]); _iGy = Find(Aliases[1]);
            _iGxN = Find(Aliases[2]); _iGyN = Find(Aliases[3]);
            _iPl = Find(Aliases[4]); _iPr = Find(Aliases[5]);
            _iCl = Find(Aliases[6]); _iCr = Find(Aliases[7]);
            _iBlink = Find(Aliases[8]); _iVel = Find(Aliases[9]);
        }

        /// <summary>Vrai si le fichier expose au moins une position de regard reconnue.</summary>
        public bool HasGazeMapping { get { EnsureMapped(); return _iGx >= 0 && _iGy >= 0; } }

        private void OnEngineSample(EEGSample s)
        {
            EnsureMapped();   // les premiers échantillons peuvent arriver avant le retour de Start()
            double Get(int i) => i >= 0 && i < s.Channels.Length && !double.IsNaN(s.Channels[i]) ? s.Channels[i] : 0.0;
            var e = new EyeSample
            {
                Timestamp       = s.Timestamp,
                GazeX           = Get(_iGx),
                GazeY           = Get(_iGy),
                GazeXNorm       = Get(_iGxN),
                GazeYNorm       = Get(_iGyN),
                PupilLeft       = Get(_iPl),
                PupilRight      = Get(_iPr),
                ConfidenceLeft  = _iCl >= 0 ? Get(_iCl) : 1.0,
                ConfidenceRight = _iCr >= 0 ? Get(_iCr) : 1.0,
                IsBlinking      = _iBlink >= 0 && Get(_iBlink) >= 0.5,
                VelocityDeg     = Get(_iVel),
                EventType       = _iBlink >= 0 && Get(_iBlink) >= 0.5 ? "blink" : "fixation",
                Raw             = s.Channels,
                RawNames        = _engine.ChannelNames,
            };
            TotalSamplesGenerated++;
            _output?.WriteSample(e);
            SampleGenerated?.Invoke(e);
        }
    }
}
