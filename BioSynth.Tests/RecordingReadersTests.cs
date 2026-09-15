using System.Globalization;
using System.Text;
using BioSynth;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour RecordingLoader / TabularReader / BrainVisionReader.
/// Valide la détection de format, les colonnes de temps et de marqueurs, la
/// fréquence déduite et le rejeu via EEGDataReplay.
/// </summary>
public class RecordingReadersTests : IDisposable
{
    private readonly List<string> _tmp = new();

    private string TmpPath(string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), $"biosynth_{Guid.NewGuid():N}{ext}");
        _tmp.Add(p);
        return p;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>CSV de regard : colonne Frame, Time en secondes avec gigue, 4 angles.</summary>
    private string WriteGazeCsv(int frames)
    {
        var p = TmpPath(".csv");
        var sb = new StringBuilder("Frame,Time,AngleYeuxDevant,AngleYeuxVisage,AngleYeuxSeins,AngleYeuxGenital\n");
        double t = 913.4;
        var rng = new Random(1);
        for (int i = 0; i < frames; i++)
        {
            t += 0.008 + rng.NextDouble() * 0.006;   // ~11 ms ± 3 ms : irrégulier
            sb.Append(CultureInfo.InvariantCulture, $"{i},{t:F4},{i * 0.1:F3},83.5,77.7,79.5\n");
        }
        File.WriteAllText(p, sb.ToString());
        return p;
    }

    /// <summary>CSV européen : point-virgule et virgule décimale, temps en ms régulier.</summary>
    private string WriteSemicolonCsv(int frames, int fs)
    {
        var p = TmpPath(".csv");
        var sb = new StringBuilder("MilliSec;Ch_A;Ch_B\n");
        for (int i = 0; i < frames; i++)
            sb.Append($"{i * 1000 / fs};{(i * 0.5).ToString("F2", CultureInfo.InvariantCulture).Replace('.', ',')};1,25\n");
        File.WriteAllText(p, sb.ToString());
        return p;
    }

    /// <summary>Excel PPG : DataPoint, PPGValue, Marker vide, MilliSec, Sec, Var8 avec marqueurs.</summary>
    private string WritePpgXlsx(int frames)
    {
        var p = TmpPath(".xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        string[] headers = { "DataPoint", "PPGValue", "Marker", "MilliSec", "Sec", "Var6", "SamplingInterval", "Var8" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (int i = 0; i < frames; i++)
        {
            int r = i + 2;
            ws.Cell(r, 1).Value = i + 1;
            ws.Cell(r, 2).Value = 88.0 + Math.Sin(i * 0.3);
            ws.Cell(r, 4).Value = i * 40;
            ws.Cell(r, 5).Value = i * 0.04;
            if (i == 0) { ws.Cell(r, 7).Value = 40; ws.Cell(r, 8).Value = "PVA1"; }
            if (i == 25) ws.Cell(r, 8).Value = "PVA3";
        }
        wb.SaveAs(p);
        return p;
    }

    /// <summary>BrainVision ASCII multiplexé, 3 canaux, 200 Hz, avec .vmrk.</summary>
    private (string vhdr, string dat) WriteBrainVisionAscii(int samples)
    {
        var vhdr = TmpPath(".vhdr");
        var dat  = Path.ChangeExtension(vhdr, ".dat");
        var vmrk = Path.ChangeExtension(vhdr, ".vmrk");
        _tmp.Add(dat); _tmp.Add(vmrk);

        File.WriteAllText(vhdr,
            "BrainVision Data Exchange Header File Version 2.0\n" +
            "[Common Infos]\nCodepage=UTF-8\n" +
            $"DataFile={Path.GetFileName(dat)}\nMarkerFile={Path.GetFileName(vmrk)}\n" +
            "DataFormat=ASCII\nDataOrientation=MULTIPLEXED\nDataType=TIMEDOMAIN\n" +
            "NumberOfChannels=3\nSamplingInterval=5000\n" +
            "[ASCII Infos]\nDecimalSymbol=.\nSkipLines=1\nSkipColumns=0\n" +
            "[Channel Infos]\nCh1=Fp1,,,µV\nCh2=Cz,,,µV\nCh3=Oz,,,µV\n");

        var sb = new StringBuilder("Fp1 Cz Oz\n");
        for (int i = 0; i < samples; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i * 1.0:F3} {i * 2.0:F3} {i * 3.0:F3}\n");
        File.WriteAllText(dat, sb.ToString());

        File.WriteAllText(vmrk,
            "BrainVision Data Exchange Marker File, Version 2.0\n" +
            "[Common Infos]\nCodepage=UTF-8\n" +
            $"DataFile={Path.GetFileName(dat)}\n" +
            "[Marker Infos]\nMk1=New Segment,,1,1,0\nMk2=Stimulus,S  1,41,1,0\n");
        return (vhdr, dat);
    }

    /// <summary>BrainVision BINARY INT_16 vectorisé avec résolution 0.1.</summary>
    private string WriteBrainVisionBinary(int samples)
    {
        var vhdr = TmpPath(".vhdr");
        var eeg  = Path.ChangeExtension(vhdr, ".eeg");
        _tmp.Add(eeg);
        File.WriteAllText(vhdr,
            "BrainVision Data Exchange Header File Version 1.0\n" +
            "[Common Infos]\n" +
            $"DataFile={Path.GetFileName(eeg)}\n" +
            "DataFormat=BINARY\nDataOrientation=VECTORIZED\n" +
            "NumberOfChannels=2\nSamplingInterval=4000\n" +
            "[Binary Infos]\nBinaryFormat=INT_16\n" +
            "[Channel Infos]\nCh1=C3,,0.1,µV\nCh2=C4,,0.1,µV\n");
        using var bw = new BinaryWriter(File.Create(eeg));
        for (int i = 0; i < samples; i++) bw.Write((short)(i * 10));       // canal 1 : 0, 10, 20... → ×0.1
        for (int i = 0; i < samples; i++) bw.Write((short)(-i * 10));      // canal 2
        return vhdr;
    }

    // ── Détection de format ───────────────────────────────────────────────

    [Fact]
    public void IsLegacyBioSynthCsv_DetectsTimestampUsHeader()
    {
        var p = TmpPath(".csv");
        File.WriteAllText(p, "Timestamp_us,Ch1,Ch2\n0,1.0,2.0\n3906,1.1,2.1\n");
        RecordingLoader.IsLegacyBioSynthCsv(p).Should().BeTrue();
    }

    [Fact]
    public void IsLegacyBioSynthCsv_RejectsGenericCsv()
    {
        RecordingLoader.IsLegacyBioSynthCsv(WriteGazeCsv(10)).Should().BeFalse();
    }

    [Fact]
    public void IsSupported_DatWithoutVhdr_IsFalse()
    {
        var p = TmpPath(".dat");
        File.WriteAllText(p, "x");
        RecordingLoader.IsSupported(p).Should().BeFalse();
    }

    // ── CSV ───────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_IrregularTimestamps_NoSampleRate_ChannelsExcludeFrameAndTime()
    {
        var rec = RecordingLoader.Open(WriteGazeCsv(200));
        rec.SampleRate.Should().BeNull();
        rec.ChannelNames.Should().Equal("AngleYeuxDevant", "AngleYeuxVisage", "AngleYeuxSeins", "AngleYeuxGenital");
        rec.SampleCount.Should().Be(200);
        rec.Times[0].Should().Be(0);
        rec.Times[^1].Should().BeGreaterThan(1.5).And.BeLessThan(3.0);
        rec.EffectiveSampleRate.Should().BeInRange(80, 130);
    }

    [Fact]
    public void Csv_SemicolonDecimalComma_RegularRate_IsDetected()
    {
        var rec = RecordingLoader.Open(WriteSemicolonCsv(100, 50));
        rec.SampleRate.Should().NotBeNull();
        rec.SampleRate!.Value.Should().BeApproximately(50, 0.5);
        rec.ChannelNames.Should().Equal("Ch_A", "Ch_B");
        rec.Data[3][0].Should().BeApproximately(1.5, 1e-9);
        rec.Data[3][1].Should().BeApproximately(1.25, 1e-9);
    }

    // ── Excel ─────────────────────────────────────────────────────────────

    [Fact]
    public void Excel_Ppg_25Hz_MarkersFromVar8_SecColumnExcluded()
    {
        var rec = RecordingLoader.Open(WritePpgXlsx(100));
        rec.ChannelNames.Should().Equal("PPGValue");
        rec.SampleRate!.Value.Should().BeApproximately(25, 0.1);
        rec.Markers.Should().HaveCount(2);
        rec.Markers[0].Label.Should().Be("PVA1");
        rec.Markers[1].Time.Should().BeApproximately(1.0, 1e-6);
    }

    // ── BrainVision ───────────────────────────────────────────────────────

    [Fact]
    public void BrainVision_Ascii_Multiplexed_ReadsChannelsRateAndMarkers()
    {
        var (vhdr, _) = WriteBrainVisionAscii(400);
        var rec = RecordingLoader.Open(vhdr);
        rec.ChannelNames.Should().Equal("Fp1", "Cz", "Oz");
        rec.SampleRate.Should().Be(200);
        rec.SampleCount.Should().Be(400);
        rec.Unit.Should().Be("µV");
        rec.Data[10][1].Should().Be(20.0);
        rec.Markers.Should().HaveCount(2);
        rec.Markers[1].Label.Should().Be("Stimulus/S  1");
        rec.Markers[1].Time.Should().BeApproximately(0.2, 1e-9);   // point 41 → (41-1)/200
    }

    [Fact]
    public void BrainVision_OpenFromDatPath_UsesSiblingVhdr()
    {
        var (_, dat) = WriteBrainVisionAscii(50);
        RecordingLoader.Open(dat).ChannelCount.Should().Be(3);
    }

    [Fact]
    public void BrainVision_Binary_Vectorized_AppliesResolution()
    {
        var rec = RecordingLoader.Open(WriteBrainVisionBinary(100));
        rec.SampleRate.Should().Be(250);
        rec.SampleCount.Should().Be(100);
        rec.Data[7][0].Should().BeApproximately(7.0, 1e-9);
        rec.Data[7][1].Should().BeApproximately(-7.0, 1e-9);
    }

    // ── Intégration EEGDataReplay ─────────────────────────────────────────

    [Fact]
    public void Replay_BrainVision_Inspect_ReportsFileMetadata()
    {
        var (vhdr, _) = WriteBrainVisionAscii(400);
        var replay = new EEGDataReplay { FilePath = vhdr };
        var (ok, info, ch, sr, frames) = replay.Inspect();
        ok.Should().BeTrue(info);
        ch.Should().Be(3);
        sr.Should().Be(200);
        frames.Should().Be(400);
    }

    [Fact]
    public async Task Replay_Excel_EmitsSamplesAndMarkers_AtSpeed()
    {
        var replay = new EEGDataReplay { FilePath = WritePpgXlsx(100), Speed = 8.0 };  // 4 s de données → 0,5 s
        int samples = 0; var markers = new List<string>();
        replay.SampleReady += _ => Interlocked.Increment(ref samples);
        replay.MarkerReady += (_, l) => { lock (markers) markers.Add(l); };
        var done = new TaskCompletionSource();
        replay.PlaybackFinished += () => done.TrySetResult();

        replay.Start();
        replay.IsRecordingFormat.Should().BeTrue();
        replay.ChannelNames.Should().Equal("PPGValue");
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task, "la lecture doit se terminer");

        samples.Should().Be(100);
        markers.Should().Equal("PVA1", "PVA3");
        replay.Dispose();
    }

    [Fact]
    public async Task Replay_IrregularCsv_RespectsTimestamps()
    {
        var replay = new EEGDataReplay { FilePath = WriteGazeCsv(100), Speed = 1.0 };
        var stamps = new List<(double wall, long ts)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        replay.SampleReady += s => { lock (stamps) stamps.Add((sw.Elapsed.TotalSeconds, s.Timestamp)); };
        replay.Start();
        await Task.Delay(600);
        replay.Stop();

        stamps.Should().NotBeEmpty();
        // Chaque échantillon doit sortir au plus tard ~50 ms après son timestamp (temps mur ≈ temps enregistrement)
        foreach (var (wall, ts) in stamps)
            wall.Should().BeGreaterThanOrEqualTo(ts / 1e6 - 0.001).And.BeLessThan(ts / 1e6 + 0.05);
        replay.Dispose();
    }

    // ── Liste de lecture ──────────────────────────────────────────────────

    [Fact]
    public void Playlist_SameChannels_Inspect_AggregatesFiles()
    {
        var (a, _) = WriteBrainVisionAscii(100);
        var (b, _) = WriteBrainVisionAscii(150);
        var replay = new EEGDataReplay();
        replay.FilePaths.AddRange(new[] { a, b });
        var (ok, info, ch, sr, frames) = replay.Inspect();
        ok.Should().BeTrue(info);
        ch.Should().Be(3);
        sr.Should().Be(200);
        frames.Should().Be(250);
        info.Should().StartWith("2 fichiers");
    }

    [Fact]
    public void Playlist_MissingChannel_IsZeroFilled_WithWarning()
    {
        var (bv, _) = WriteBrainVisionAscii(50);            // Fp1, Cz, Oz
        var csv = TmpPath(".csv");                          // Fp1, Oz seulement (Cz manquant), régulier 200 Hz
        var sb = new StringBuilder("Time,Fp1,Oz\n");
        for (int i = 0; i < 50; i++) sb.Append(CultureInfo.InvariantCulture, $"{i * 0.005:F4},1,3\n");
        File.WriteAllText(csv, sb.ToString());

        var replay = new EEGDataReplay();
        replay.FilePaths.AddRange(new[] { bv, csv });
        var (ok, info, ch, _, frames) = replay.Inspect();

        ok.Should().BeTrue(info);
        ch.Should().Be(3);
        frames.Should().Be(100);
        replay.Warnings.Should().ContainSingle(w => w.Contains("Cz"));
        info.Should().Contain("⚠");
        replay.ChannelNames.Should().BeNull("les noms ne sont fixés qu'au Start()");
    }

    [Fact]
    public async Task Playlist_MissingChannel_EmitsZeroForThatChannel()
    {
        var (bv, _) = WriteBrainVisionAscii(20);
        var csv = TmpPath(".csv");
        var sb = new StringBuilder("Time,Oz,Fp1\n");        // ordre différent et Cz absent
        for (int i = 0; i < 20; i++) sb.Append(CultureInfo.InvariantCulture, $"{i * 0.005:F4},7,5\n");
        File.WriteAllText(csv, sb.ToString());

        var replay = new EEGDataReplay { Speed = 10.0 };
        replay.FilePaths.AddRange(new[] { bv, csv });
        var samples = new List<double[]>();
        replay.SampleReady += s => { lock (samples) samples.Add(s.Channels); };
        var done = new TaskCompletionSource();
        replay.PlaybackFinished += () => done.TrySetResult();
        replay.Start();
        replay.ChannelNames.Should().Equal("Fp1", "Cz", "Oz");
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task);

        samples.Should().HaveCount(40);
        samples[30].Should().Equal(5.0, 0.0, 7.0);           // Fp1=5, Cz=0 (manquant), Oz=7 réordonné
        replay.Dispose();
    }

    [Fact]
    public async Task Playlist_PlaysSequentially_WithContinuousTimestamps()
    {
        var (a, _) = WriteBrainVisionAscii(100);   // 0,5 s à 200 Hz
        var (b, _) = WriteBrainVisionAscii(100);
        var replay = new EEGDataReplay { Speed = 10.0 };
        replay.FilePaths.AddRange(new[] { a, b });

        var files = new List<int>(); var stamps = new List<long>();
        replay.FileChanged += (idx, _, _) => { lock (files) files.Add(idx); };
        replay.SampleReady += s => { lock (stamps) stamps.Add(s.Timestamp); };
        var done = new TaskCompletionSource();
        replay.PlaybackFinished += () => done.TrySetResult();

        replay.Start();
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task);

        files.Should().Equal(0, 1);
        stamps.Should().HaveCount(200);
        stamps.Should().BeInAscendingOrder();              // timestamps continus d'un fichier à l'autre
        stamps[100].Should().BeGreaterThan(stamps[99]);
        replay.Dispose();
    }

    [Fact]
    public async Task Playlist_Loop_RestartsFromFirstFile()
    {
        var (a, _) = WriteBrainVisionAscii(40);
        var (b, _) = WriteBrainVisionAscii(40);
        var replay = new EEGDataReplay { Speed = 10.0, Loop = true };
        replay.FilePaths.AddRange(new[] { a, b });
        var files = new List<int>();
        replay.FileChanged += (idx, _, _) => { lock (files) files.Add(idx); };

        replay.Start();
        await Task.Delay(700);       // 2 fichiers × 0,2 s à ×10 = 0,04 s par tour : plusieurs boucles
        replay.Stop();

        files.Count.Should().BeGreaterThan(2);
        files.Take(4).Should().Equal(0, 1, 0, 1);
        replay.Dispose();
    }

    // ── Replay oculométrique ──────────────────────────────────────────────

    [Fact]
    public async Task EyeReplay_GazeAngleCsv_KeepsRawColumns_NoGazeMapping()
    {
        var replay = new EyeTrackingReplay(new[] { WriteGazeCsv(60) }) { Speed = 10.0 };
        var (ok, info, ch, _, _) = replay.Inspect();
        ok.Should().BeTrue(info);
        ch.Should().Be(4);

        var samples = new List<EyeSample>();
        replay.SampleGenerated += s => { lock (samples) samples.Add(s); };
        var done = new TaskCompletionSource();
        replay.PlaybackFinished += () => done.TrySetResult();
        replay.Start();
        replay.HasGazeMapping.Should().BeFalse();
        replay.ChannelNames.Should().Equal("AngleYeuxDevant", "AngleYeuxVisage", "AngleYeuxSeins", "AngleYeuxGenital");
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task);

        samples.Should().HaveCount(60);
        samples[10].Raw.Should().NotBeNull();
        samples[10].Raw![0].Should().BeApproximately(1.0, 1e-6);   // AngleYeuxDevant = i*0.1
        samples[10].RawNames.Should().Equal(replay.ChannelNames);
        samples[10].ConfidenceLeft.Should().Be(1.0);               // pas de colonne de confiance : valide par défaut
        replay.Dispose();
    }

    [Fact]
    public async Task EyeReplay_BioSynthExportCsv_MapsStandardFields()
    {
        // Format d'export de BioSynth (EyeFileOutput), avec une colonne texte EventType
        var p = TmpPath(".csv");
        var sb = new StringBuilder("Timestamp_us,GazeX,GazeY,GazeXnorm,GazeYnorm,PupilL_mm,PupilR_mm,ConfL,ConfR,Blink,EventType,Velocity_dps\n");
        for (int i = 0; i < 24; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{i * 8333},{960 + i},{540},{0.5},{0.5},{3.2},{3.3},{0.98},{0.97},{(i == 5 ? 1 : 0)},{(i == 5 ? "blink" : "fixation")},{12.5}\n");
        File.WriteAllText(p, sb.ToString());

        var replay = new EyeTrackingReplay(new[] { p }) { Speed = 10.0 };
        var samples = new List<EyeSample>();
        replay.SampleGenerated += s => { lock (samples) samples.Add(s); };
        var done = new TaskCompletionSource();
        replay.PlaybackFinished += () => done.TrySetResult();
        replay.Start();
        replay.HasGazeMapping.Should().BeTrue();
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task);

        samples.Should().HaveCount(24);
        samples[3].GazeX.Should().Be(963);
        samples[3].PupilRight.Should().BeApproximately(3.3, 1e-9);
        samples[5].IsBlinking.Should().BeTrue();
        samples[5].EventType.Should().Be("blink");
        samples[3].Timestamp.Should().Be(3 * 8333);
        replay.Dispose();
    }

    [Fact]
    public async Task EyeReplay_IncludeRawColumns_WritesRawToCsvOutput()
    {
        var outCsv = TmpPath(".csv");
        var replay = new EyeTrackingReplay(new[] { WriteGazeCsv(10) })
        {
            Speed = 10.0,
            OutputConfig = new EyeTrackingConfig
            {
                OutputMode = OutputMode.File, DataFormat = DataFormat.CSV,
                FilePath = outCsv, IncludeRawColumns = true,
            },
        };
        var done = new TaskCompletionSource();
        replay.PlaybackFinished += () => done.TrySetResult();
        replay.Start();
        (await Task.WhenAny(done.Task, Task.Delay(5000))).Should().Be(done.Task);
        replay.Stop();

        var lines = File.ReadAllLines(outCsv);
        lines[0].Should().EndWith("Velocity_dps,AngleYeuxDevant,AngleYeuxVisage,AngleYeuxSeins,AngleYeuxGenital");
        lines.Should().HaveCount(11);
        lines[3].Split(',').Should().HaveCount(16);
        replay.Dispose();
    }

    public void Dispose()
    {
        foreach (var p in _tmp) { try { File.Delete(p); } catch { } }
    }
}
