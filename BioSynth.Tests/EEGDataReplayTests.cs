using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour EEGDataReplay.
/// Valide la lecture CSV, le parsing, la vitesse et le contrôle de lecture.
/// </summary>
public class EEGDataReplayTests : IDisposable
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static string WriteCsv(int channels, int frames, int sampleRate = 256)
    {
        var path = Path.GetTempFileName() + ".csv";
        using var w = new StreamWriter(path);

        // En-tête
        var header = "Timestamp_us," + string.Join(",", Enumerable.Range(1, channels).Select(i => $"Ch{i}"));
        w.WriteLine(header);

        long intervalUs = 1_000_000L / sampleRate;
        for (int f = 0; f < frames; f++)
        {
            long ts   = f * intervalUs;
            var vals  = Enumerable.Range(0, channels).Select(c => ((f + c) * 3.14).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteLine($"{ts},{string.Join(",", vals)}");
        }
        return path;
    }

    private static string WriteBin(int channels, int frames, int sampleRate = 256)
    {
        var path = Path.GetTempFileName() + ".bin";
        using var bw = new BinaryWriter(File.Open(path, FileMode.Create));

        long intervalUs = 1_000_000L / sampleRate;
        for (int f = 0; f < frames; f++)
        {
            bw.Write((long)(f * intervalUs));
            for (int c = 0; c < channels; c++)
                bw.Write((float)(f * 0.5 + c));
        }
        return path;
    }

    private readonly List<string> _tempFiles = new();

    private string TempCsv(int channels = 8, int frames = 100, int sr = 256)
    {
        var path = WriteCsv(channels, frames, sr);
        _tempFiles.Add(path);
        return path;
    }

    private string TempBin(int channels = 8, int frames = 100, int sr = 256)
    {
        var path = WriteBin(channels, frames, sr);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    // ── Inspect CSV ────────────────────────────────────────────────────────

    [Fact]
    public void Inspect_ValidCsv_ReturnsSuccess()
    {
        var path   = TempCsv(8, 256);
        var replay = new EEGDataReplay { FilePath = path };

        var (ok, info, ch, sr, frames) = replay.Inspect();

        ok.Should().BeTrue();
        ch.Should().Be(8);
        frames.Should().Be(256);
    }

    [Fact]
    public void Inspect_CsvSampleRate_IsDetectedFromTimestamps()
    {
        var path   = TempCsv(8, 512, 256);
        var replay = new EEGDataReplay { FilePath = path };

        var (ok, _, _, sr, _) = replay.Inspect();

        ok.Should().BeTrue();
        sr.Should().BeInRange(200, 320,
            because: "le sample rate détecté doit être proche de 256 Hz");
    }

    [Fact]
    public void Inspect_ValidBin_ReturnsSuccess()
    {
        var path   = TempBin(8, 256);
        var replay = new EEGDataReplay { FilePath = path };

        var (ok, info, ch, sr, frames) = replay.Inspect();

        ok.Should().BeTrue();
        ch.Should().Be(8);
        frames.Should().Be(256);
    }

    [Fact]
    public void Inspect_NonExistentFile_ReturnsFalse()
    {
        var replay = new EEGDataReplay { FilePath = "/fichier/qui/nexiste/pas.csv" };

        var (ok, info, _, _, _) = replay.Inspect();

        ok.Should().BeFalse();
        info.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Inspect_UnsupportedExtension_ReturnsFalse()
    {
        var path = Path.GetTempFileName() + ".xyz";
        File.WriteAllText(path, "data");
        _tempFiles.Add(path);
        var replay = new EEGDataReplay { FilePath = path };

        var (ok, info, _, _, _) = replay.Inspect();

        ok.Should().BeFalse();
    }

    // ── Lecture et samples ────────────────────────────────────────────────

    [Fact]
    public async Task Start_Csv_EmitsSamples()
    {
        var path    = TempCsv(8, 50);
        var replay  = new EEGDataReplay { FilePath = path, Speed = 100.0 };
        var samples = new List<EEGSample>();
        replay.SampleReady += s => { lock (samples) samples.Add(s); };

        replay.Start();
        await Task.Delay(500);
        replay.Stop();

        samples.Should().NotBeEmpty();
        samples.Count.Should().BeInRange(1, 50);
    }

    [Fact]
    public async Task Start_Bin_EmitsSamplesWithCorrectChannelCount()
    {
        int channels = 16;
        var path     = TempBin(channels, 50);
        var replay   = new EEGDataReplay { FilePath = path, Speed = 100.0 };
        var samples  = new List<EEGSample>();
        replay.SampleReady += s => { lock (samples) samples.Add(s); };

        replay.Start();
        await Task.Delay(500);
        replay.Stop();

        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s => s.Channels.Length == channels);
    }

    [Fact]
    public async Task Start_Csv_PreservesDataValues()
    {
        // Écrire un CSV avec des valeurs connues
        var path = Path.GetTempFileName() + ".csv";
        _tempFiles.Add(path);
        using (var w = new StreamWriter(path))
        {
            w.WriteLine("Timestamp_us,Ch1,Ch2");
            w.WriteLine("0,42.000,99.500");
            w.WriteLine("3906,13.250,-7.750");
        }

        var replay  = new EEGDataReplay { FilePath = path, Speed = 1000.0 };
        var samples = new List<EEGSample>();
        replay.SampleReady += s => { lock (samples) samples.Add(s); };

        replay.Start();
        await Task.Delay(300);
        replay.Stop();

        samples.Should().HaveCountGreaterThanOrEqualTo(1);
        samples[0].Channels[0].Should().BeApproximately(42.0, 0.01);
        samples[0].Channels[1].Should().BeApproximately(99.5, 0.01);
    }

    // ── Propriétés ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressPct_IncreasesOverTime()
    {
        var path   = TempCsv(8, 512);
        var replay = new EEGDataReplay { FilePath = path, Speed = 10.0 };
        replay.SampleReady += _ => { };

        replay.Start();
        await Task.Delay(100);
        double pct1 = replay.ProgressPct;
        await Task.Delay(150);
        double pct2 = replay.ProgressPct;
        replay.Stop();

        pct2.Should().BeGreaterThanOrEqualTo(pct1,
            because: "la progression doit augmenter avec le temps");
    }

    [Fact]
    public async Task IsRunning_TrueAfterStart_FalseAfterStop()
    {
        var path   = TempCsv(8, 1000);
        var replay = new EEGDataReplay { FilePath = path, Speed = 1.0 };
        replay.SampleReady += _ => { };

        replay.Start();
        await Task.Delay(50);
        replay.IsRunning.Should().BeTrue();

        replay.Stop();
        await Task.Delay(100);
        replay.IsRunning.Should().BeFalse();
    }

    // ── Pause / Resume ────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_StopsSampleEmission()
    {
        var path   = TempCsv(8, 2000);
        var replay = new EEGDataReplay { FilePath = path, Speed = 20.0 };
        var count1 = 0; var count2 = 0;

        replay.SampleReady += _ => Interlocked.Increment(ref count1);
        replay.Start();
        await Task.Delay(100);

        replay.Pause();
        count1 = 0;
        await Task.Delay(200);
        count2 = count1;  // doit rester proche de 0

        replay.Stop();

        count2.Should().BeLessThan(5,
            because: "après Pause, très peu ou aucun sample ne doit être émis");
    }

    [Fact]
    public async Task Resume_AfterPause_ResumesSampleEmission()
    {
        var path    = TempCsv(8, 5000);
        var replay  = new EEGDataReplay { FilePath = path, Speed = 50.0 };
        var samples = new List<EEGSample>();
        replay.SampleReady += s => { lock (samples) samples.Add(s); };

        replay.Start();
        await Task.Delay(80);
        replay.Pause();
        int countBeforeResume = samples.Count;

        await Task.Delay(150);
        replay.Resume();
        await Task.Delay(150);
        replay.Stop();

        samples.Count.Should().BeGreaterThan(countBeforeResume,
            because: "après Resume, de nouveaux samples doivent être émis");
    }

    // ── Speed ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HighSpeed_CompletesFilesFaster()
    {
        // Fichier de 256 frames → à 256 Hz, dure 1 s
        // À vitesse 50x, devrait finir en ~20 ms
        var path   = TempCsv(8, 256, 256);
        bool done  = false;
        var replay = new EEGDataReplay { FilePath = path, Speed = 50.0, Loop = false };
        replay.SampleReady        += _ => { };
        replay.PlaybackFinished   += () => done = true;

        replay.Start();
        await Task.Delay(2000);  // max 2 s d'attente

        done.Should().BeTrue(because: "à 50x, un fichier de 1 s doit finir en < 2 s");
    }

    // ── Loop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loop_True_EmitsMoreSamplesThanFileContains()
    {
        var path   = TempCsv(8, 20);  // seulement 20 frames
        var replay = new EEGDataReplay { FilePath = path, Speed = 500.0, Loop = true };
        var count  = 0;
        replay.SampleReady += _ => Interlocked.Increment(ref count);

        replay.Start();
        await Task.Delay(500);
        replay.Stop();

        count.Should().BeGreaterThan(20,
            because: "avec Loop=true, le replay doit dépasser le nombre de frames du fichier");
    }
}
