using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BioSynth
{
    // ════════════════════════════════════════════════════════════════════
    // Inlet LSL — reçoit des données d'une source externe (IA ou autre)
    // et les injecte dans le pipeline BioSynth via ConcurrentQueue.
    // ════════════════════════════════════════════════════════════════════

    public class EEGLslInlet : IDisposable
    {
        // ── P/Invoke LSL inlet ────────────────────────────────────────────
        private const string DLL = "lsl";

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lsl_resolve_byprop(
            IntPtr[] buf, int bufLen,
            [MarshalAs(UnmanagedType.LPStr)] string prop,
            [MarshalAs(UnmanagedType.LPStr)] string value,
            int minimum, double timeout);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lsl_create_inlet(
            IntPtr info, int maxBufLen, int maxChunkLen, int recoverClock);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lsl_destroy_inlet(IntPtr inlet);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lsl_pull_sample_f(
            IntPtr inlet, float[] buf, int bufLen,
            double timeout, out int ec);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lsl_get_inlet_info(IntPtr inlet);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lsl_get_channel_count(IntPtr info);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern double lsl_get_nominal_srate(IntPtr info);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lsl_get_name(IntPtr info);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lsl_get_type(IntPtr info);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lsl_destroy_streaminfo(IntPtr info);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern double lsl_local_clock();

        // ── État ──────────────────────────────────────────────────────────
        private IntPtr _inlet  = IntPtr.Zero;
        private CancellationTokenSource? _cts;
        private Task?  _task;

        public bool   IsRunning    { get; private set; }
        public int    ChannelCount { get; private set; }
        public int    SampleRate   { get; private set; }
        public string StreamName   { get; private set; } = "";
        public string StreamType   { get; private set; } = "";

        // ── Événements ────────────────────────────────────────────────────
        public event Action<EEGSample>? SampleReady;
        public event Action<string>?   StatusChanged;

        // ════════════════════════════════════════════════════════════════
        // Découverte des flux LSL EEG disponibles sur le réseau
        // ════════════════════════════════════════════════════════════════

        public static List<LslStreamInfo> DiscoverStreams(double timeoutSec = 2.0)
        {
            var result = new List<LslStreamInfo>();
            try
            {
                var buf = new IntPtr[64];
                // Chercher tous les flux de type EEG
                int found = (int)lsl_resolve_byprop(buf, 64, "type", "EEG", 0, timeoutSec);
                for (int i = 0; i < found; i++)
                {
                    if (buf[i] == IntPtr.Zero) continue;
                    var info = new LslStreamInfo
                    {
                        Handle      = buf[i],
                        Name        = Marshal.PtrToStringAnsi(lsl_get_name(buf[i]))      ?? "",
                        Type        = Marshal.PtrToStringAnsi(lsl_get_type(buf[i]))      ?? "",
                        ChannelCount= lsl_get_channel_count(buf[i]),
                        SampleRate  = (int)lsl_get_nominal_srate(buf[i]),
                    };
                    result.Add(info);
                }
            }
            catch { /* LSL non disponible */ }
            return result;
        }

        // ════════════════════════════════════════════════════════════════
        // Connexion à un flux
        // ════════════════════════════════════════════════════════════════

        public bool Connect(LslStreamInfo stream)
        {
            try
            {
                _inlet = lsl_create_inlet(stream.Handle, 360, 0, 1);
                if (_inlet == IntPtr.Zero) return false;

                ChannelCount = stream.ChannelCount;
                SampleRate   = stream.SampleRate > 0 ? stream.SampleRate : 256;
                StreamName   = stream.Name;
                StreamType   = stream.Type;
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"LSL inlet erreur : {ex.Message}");
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Lecture continue
        // ════════════════════════════════════════════════════════════════

        public void Start()
        {
            if (_inlet == IntPtr.Zero) return;
            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => PullLoop(_cts.Token));
            IsRunning = true;
            StatusChanged?.Invoke($"LSL inlet connecté : {StreamName} ({ChannelCount} ch, {SampleRate} Hz)");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _task?.Wait(1000); } catch { }
            IsRunning = false;
            StatusChanged?.Invoke("LSL inlet déconnecté.");
        }

        private void PullLoop(CancellationToken ct)
        {
            var buf = new float[ChannelCount];
            long sampleIdx = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // timeout 0.1 s — non bloquant, permet de vérifier l'annulation
                    double ts = lsl_pull_sample_f(_inlet, buf, buf.Length, 0.1, out int ec);
                    if (ec != 0 || ts == 0) continue;

                    var sample = new EEGSample(ChannelCount)
                    {
                        Timestamp = (long)(ts * 1_000_000)
                    };
                    Array.Copy(buf, sample.Channels, ChannelCount);
                    SampleReady?.Invoke(sample);
                    sampleIdx++;
                }
                catch (OperationCanceledException) { break; }
                catch { /* erreur transitoire — continuer */ }
            }
        }

        // ════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            Stop();
            if (_inlet != IntPtr.Zero)
            {
                lsl_destroy_inlet(_inlet);
                _inlet = IntPtr.Zero;
            }
            _cts?.Dispose();
        }
    }

    // ── Descripteur de flux découvert ─────────────────────────────────────
    public class LslStreamInfo
    {
        public IntPtr Handle       { get; set; }
        public string Name         { get; set; } = "";
        public string Type         { get; set; } = "";
        public int    ChannelCount { get; set; }
        public int    SampleRate   { get; set; }

        public override string ToString()
            => $"{Name}  ({ChannelCount} ch · {SampleRate} Hz · {Type})";
    }
}
