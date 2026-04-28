using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BioSynth
{
    // ════════════════════════════════════════════════════════════════════
    // Wrapper P/Invoke minimal sur liblsl.dll (LSL natif)
    // Bibliothèque : https://github.com/sccn/liblsl/releases
    // Placer liblsl.dll (x64) dans le dossier de l'exécutable.
    // ════════════════════════════════════════════════════════════════════

    internal static class Lsl
    {
        // ── Nom de la DLL ─────────────────────────────────────────────────
        // liblsl peut s'appeler différemment selon la version et l'OS.
        // La version ≥ 1.14 sur Windows s'appelle généralement "lsl"
        // (lsl.dll), versions antérieures : "liblsl64" ou "liblsl".
        // On tente "lsl" en priorité (compatible toutes versions récentes).
        private const string DLL = "lsl";

        // Types de canaux LSL
        public const int CF_FLOAT32  = 1;
        public const int CF_DOUBLE64 = 2;
        public const int CF_STRING   = 4;

        // ── Outlet ────────────────────────────────────────────────────────

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr lsl_create_streaminfo(
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [MarshalAs(UnmanagedType.LPStr)] string type,
            int channelCount,
            double nominalSrate,
            int channelFormat,
            [MarshalAs(UnmanagedType.LPStr)] string sourceId);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr lsl_create_outlet(IntPtr info, int chunkSize, int maxBuffered);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void lsl_destroy_outlet(IntPtr outlet);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int lsl_push_sample_f(IntPtr outlet, float[] data);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int lsl_push_sample_d(IntPtr outlet, double[] data);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern double lsl_local_clock();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr lsl_get_desc(IntPtr info);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr lsl_append_child(IntPtr elem,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr lsl_append_child_value(IntPtr elem,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void lsl_destroy_streaminfo(IntPtr info);

        // Vérifier si liblsl est disponible — tester les deux noms connus
        public static bool IsAvailable()
        {
            try   { lsl_local_clock(); return true; }
            catch { }
            // Si "lsl" échoue, l'utilisateur doit renommer sa DLL en lsl.dll
            return false;
        }

        // Retourner un message d'aide si la DLL est introuvable
        public static string GetDllHint()
            => "Placer lsl.dll (x64) dans le dossier de l'exécutable.\n" +
               "Télécharger : github.com/sccn/liblsl/releases → liblsl-x.x.x-Win_amd64.zip\n" +
               "Le fichier s'appelle 'lsl.dll' dans les versions récentes (≥ 1.14).\n" +
               "Versions plus anciennes : renommer liblsl64.dll en lsl.dll.";
    }

    // ════════════════════════════════════════════════════════════════════
    // Stream LSL pour EEG
    // ════════════════════════════════════════════════════════════════════

    public class EEGLslStream : IDisposable
    {
        private IntPtr _outlet = IntPtr.Zero;
        private IntPtr _info   = IntPtr.Zero;
        private readonly int _channelCount;
        private readonly float[] _buf;
        public bool IsOpen => _outlet != IntPtr.Zero;

        public EEGLslStream(int channelCount, int sampleRate, string streamName = "BioSynth_EEG")
        {
            _channelCount = channelCount;
            _buf = new float[channelCount];

            _info = Lsl.lsl_create_streaminfo(
                streamName, "EEG",
                channelCount, sampleRate,
                Lsl.CF_FLOAT32,
                $"eeg-sim-{channelCount}ch");

            // Métadonnées EEGLAB/MNE compatibles
            var desc     = Lsl.lsl_get_desc(_info);
            var channels = Lsl.lsl_append_child(desc, "channels");
            for (int i = 0; i < channelCount; i++)
            {
                string label = ChannelNames.GetChannelName(i, channelCount);
                var ch = Lsl.lsl_append_child(channels, "channel");
                Lsl.lsl_append_child_value(ch, "label",  label);
                Lsl.lsl_append_child_value(ch, "unit",   "microvolts");
                Lsl.lsl_append_child_value(ch, "type",   "EEG");
                Lsl.lsl_append_child_value(ch, "region",
                    BrainZoneController.RegionOf(label).ToString());
            }
            Lsl.lsl_append_child_value(desc, "manufacturer", "BioSynth");
            Lsl.lsl_append_child_value(desc, "samplerate",   sampleRate.ToString());

            _outlet = Lsl.lsl_create_outlet(_info, 0, 360);
        }

        public void Push(EEGSample sample)
        {
            if (_outlet == IntPtr.Zero) return;
            int n = Math.Min(sample.Channels.Length, _channelCount);
            for (int i = 0; i < n; i++)
                _buf[i] = (float)sample.Channels[i];
            Lsl.lsl_push_sample_f(_outlet, _buf);
        }

        public void Dispose()
        {
            if (_outlet != IntPtr.Zero) { Lsl.lsl_destroy_outlet(_outlet); _outlet = IntPtr.Zero; }
            if (_info   != IntPtr.Zero) { Lsl.lsl_destroy_streaminfo(_info); _info = IntPtr.Zero; }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Stream LSL pour Eye Tracking
    // ════════════════════════════════════════════════════════════════════

    public class EyeTrackingLslStream : IDisposable
    {
        private IntPtr _outlet = IntPtr.Zero;
        private IntPtr _info   = IntPtr.Zero;

        // 8 canaux : GazeX, GazeY, GazeXNorm, GazeYNorm,
        //            PupilLeft, PupilRight, ConfLeft, ConfRight
        private const int CH_COUNT = 8;
        private readonly float[] _buf = new float[CH_COUNT];

        public bool IsOpen => _outlet != IntPtr.Zero;

        private static readonly string[] Labels =
        {
            "GazeX_px", "GazeY_px", "GazeX_norm", "GazeY_norm",
            "Pupil_L_mm", "Pupil_R_mm", "Conf_L", "Conf_R"
        };
        private static readonly string[] Units =
        {
            "pixels", "pixels", "normalized", "normalized",
            "millimeters", "millimeters", "normalized", "normalized"
        };

        public EyeTrackingLslStream(int sampleRate, string streamName = "BioSynth_EyeTracking")
        {
            _info = Lsl.lsl_create_streaminfo(
                streamName, "Gaze",
                CH_COUNT, sampleRate,
                Lsl.CF_FLOAT32,
                "eyetracking-sim");

            var desc     = Lsl.lsl_get_desc(_info);
            var channels = Lsl.lsl_append_child(desc, "channels");
            for (int i = 0; i < CH_COUNT; i++)
            {
                var ch = Lsl.lsl_append_child(channels, "channel");
                Lsl.lsl_append_child_value(ch, "label", Labels[i]);
                Lsl.lsl_append_child_value(ch, "unit",  Units[i]);
                Lsl.lsl_append_child_value(ch, "type",  "Gaze");
            }
            Lsl.lsl_append_child_value(desc, "manufacturer", "BioSynth");

            _outlet = Lsl.lsl_create_outlet(_info, 0, 360);
        }

        public void Push(EyeSample s)
        {
            if (_outlet == IntPtr.Zero) return;
            _buf[0] = (float)s.GazeX;
            _buf[1] = (float)s.GazeY;
            _buf[2] = (float)s.GazeXNorm;
            _buf[3] = (float)s.GazeYNorm;
            _buf[4] = (float)s.PupilLeft;
            _buf[5] = (float)s.PupilRight;
            _buf[6] = (float)s.ConfidenceLeft;
            _buf[7] = (float)s.ConfidenceRight;
            Lsl.lsl_push_sample_f(_outlet, _buf);
        }

        public void Dispose()
        {
            if (_outlet != IntPtr.Zero) { Lsl.lsl_destroy_outlet(_outlet); _outlet = IntPtr.Zero; }
            if (_info   != IntPtr.Zero) { Lsl.lsl_destroy_streaminfo(_info); _info = IntPtr.Zero; }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Stream LSL pour Face Tracking — deux outlets séparés
    //   1. EmotionStream  : 9 canaux (arousal, valence, conf, 7 scores)
    //   2. FaceStream     : 6 canaux pose (PoseX/Y/Z, Pitch/Yaw/Roll)
    // ════════════════════════════════════════════════════════════════════

    public class FaceTrackingLslStream : IDisposable
    {
        private IntPtr _emotionOutlet = IntPtr.Zero;
        private IntPtr _emotionInfo   = IntPtr.Zero;
        private IntPtr _poseOutlet    = IntPtr.Zero;
        private IntPtr _poseInfo      = IntPtr.Zero;

        // Emotion : Arousal, Valence, Conf, Neutral, Happy, Sad, Angry, Surprised, Fearful, Disgusted
        private const int EMOTION_CH = 10;
        // Pose : PoseX, PoseY, PoseZ, Pitch, Yaw, Roll
        private const int POSE_CH    = 6;

        private readonly float[] _emotionBuf = new float[EMOTION_CH];
        private readonly float[] _poseBuf    = new float[POSE_CH];

        public bool IsOpen => _emotionOutlet != IntPtr.Zero;

        public FaceTrackingLslStream(int sampleRate,
            string emotionStreamName = "BioSynth_Emotions",
            string poseStreamName    = "BioSynth_Face")
        {
            // ── Stream émotions ───────────────────────────────────────────
            _emotionInfo = Lsl.lsl_create_streaminfo(
                emotionStreamName, "Emotions",
                EMOTION_CH, sampleRate,
                Lsl.CF_FLOAT32,
                "face-emotion-sim");

            var edesc = Lsl.lsl_get_desc(_emotionInfo);
            var ech   = Lsl.lsl_append_child(edesc, "channels");

            string[] eLabels = { "Arousal","Valence","Confidence",
                                 "Neutral","Happy","Sad","Angry",
                                 "Surprised","Fearful","Disgusted" };
            foreach (var lbl in eLabels)
            {
                var c = Lsl.lsl_append_child(ech, "channel");
                Lsl.lsl_append_child_value(c, "label", lbl);
                Lsl.lsl_append_child_value(c, "unit",  "normalized");
                Lsl.lsl_append_child_value(c, "type",  "Emotions");
            }

            _emotionOutlet = Lsl.lsl_create_outlet(_emotionInfo, 0, 360);

            // ── Stream pose ───────────────────────────────────────────────
            _poseInfo = Lsl.lsl_create_streaminfo(
                poseStreamName, "HeadPose",
                POSE_CH, sampleRate,
                Lsl.CF_FLOAT32,
                "face-pose-sim");

            var pdesc   = Lsl.lsl_get_desc(_poseInfo);
            var pch     = Lsl.lsl_append_child(pdesc, "channels");
            string[] pLabels = { "PoseX_mm","PoseY_mm","PoseZ_mm","Pitch_deg","Yaw_deg","Roll_deg" };
            string[] pUnits  = { "millimeters","millimeters","millimeters","degrees","degrees","degrees" };
            for (int i = 0; i < POSE_CH; i++)
            {
                var c = Lsl.lsl_append_child(pch, "channel");
                Lsl.lsl_append_child_value(c, "label", pLabels[i]);
                Lsl.lsl_append_child_value(c, "unit",  pUnits[i]);
                Lsl.lsl_append_child_value(c, "type",  "HeadPose");
            }

            _poseOutlet = Lsl.lsl_create_outlet(_poseInfo, 0, 360);
        }

        public void PushEmotion(EmotionSample s)
        {
            if (_emotionOutlet == IntPtr.Zero) return;
            _emotionBuf[0] = s.Arousal;
            _emotionBuf[1] = s.Valence;
            _emotionBuf[2] = s.Confidence;
            for (int i = 0; i < 7; i++)
                _emotionBuf[3 + i] = s.Scores[i];
            Lsl.lsl_push_sample_f(_emotionOutlet, _emotionBuf);
        }

        public void PushFace(FaceSample s)
        {
            if (_poseOutlet == IntPtr.Zero) return;
            _poseBuf[0] = (float)s.PoseX;
            _poseBuf[1] = (float)s.PoseY;
            _poseBuf[2] = (float)s.PoseZ;
            _poseBuf[3] = (float)s.RotPitch;
            _poseBuf[4] = (float)s.RotYaw;
            _poseBuf[5] = (float)s.RotRoll;
            Lsl.lsl_push_sample_f(_poseOutlet, _poseBuf);
        }

        public void Dispose()
        {
            if (_emotionOutlet != IntPtr.Zero) { Lsl.lsl_destroy_outlet(_emotionOutlet); _emotionOutlet = IntPtr.Zero; }
            if (_emotionInfo   != IntPtr.Zero) { Lsl.lsl_destroy_streaminfo(_emotionInfo); _emotionInfo = IntPtr.Zero; }
            if (_poseOutlet    != IntPtr.Zero) { Lsl.lsl_destroy_outlet(_poseOutlet); _poseOutlet = IntPtr.Zero; }
            if (_poseInfo      != IntPtr.Zero) { Lsl.lsl_destroy_streaminfo(_poseInfo); _poseInfo = IntPtr.Zero; }
        }
    }
}
