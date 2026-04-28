using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour FaceTrackingGenerator et EmotionSample.
/// Valide les contraintes physiologiques des émotions, AU et pose.
/// </summary>
public class FaceTrackingGeneratorTests : IDisposable
{
    private readonly FaceTrackingConfig _cfg = new()
    {
        SampleRate       = 30,
        OutputMode       = FaceOutputMode.None,
        EmotionChangeSec = 2.0,
        AddArtifacts     = false,
    };

    private FaceTrackingGenerator? _gen;

    public void Dispose() => _gen?.Dispose();

    // ── EmotionSample ─────────────────────────────────────────────────────

    [Fact]
    public void EmotionSample_ScoresSumToApproximatelyOne()
    {
        var sample = new EmotionSample
        {
            Scores = new float[] { 0.7f, 0.1f, 0.05f, 0.05f, 0.03f, 0.04f, 0.03f }
        };

        sample.Scores.Sum().Should().BeApproximately(1.0f, 0.01f,
            because: "les scores d'émotion doivent sommer à 1 (distribution de probabilité)");
    }

    [Fact]
    public void EmotionSample_AllScores_NonNegative()
    {
        var sample = new EmotionSample
        {
            Scores = new float[] { 0.6f, 0.2f, 0.1f, 0.05f, 0.02f, 0.02f, 0.01f }
        };

        sample.Scores.Should().OnlyContain(s => s >= 0.0f);
    }

    // ── Démarrage / arrêt ─────────────────────────────────────────────────

    [Fact]
    public void Start_SetsIsRunning()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        _gen.Start();
        Task.Delay(50).Wait();

        _gen.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop_ClearsIsRunning()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        _gen.Start();
        Task.Delay(50).Wait();
        _gen.Stop();
        Task.Delay(100).Wait();

        _gen.IsRunning.Should().BeFalse();
    }

    // ── EmotionSamples ────────────────────────────────────────────────────

    [Fact]
    public async Task Generator_EmitsEmotionSamples()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var emotions = new List<EmotionSample>();
        _gen.EmotionSampleGenerated += s => { lock (emotions) emotions.Add(s); };

        _gen.Start();
        await Task.Delay(300);
        _gen.Stop();

        emotions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EmotionSamples_ArousalAndValence_InRange()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var emotions = new List<EmotionSample>();
        _gen.EmotionSampleGenerated += s => { lock (emotions) emotions.Add(s); };

        _gen.Start();
        await Task.Delay(500);
        _gen.Stop();

        emotions.Should().NotBeEmpty();
        emotions.Should().OnlyContain(s =>
            s.Arousal >= 0.0f && s.Arousal <= 1.0f,
            because: "l'arousal doit être normalisé dans [0,1]");
        emotions.Should().OnlyContain(s =>
            s.Valence >= -1.0f && s.Valence <= 1.0f,
            because: "la valence doit être dans [-1,1] (modèle circomplexe de Russell)");
    }

    [Fact]
    public async Task EmotionSamples_ScoresAlwaysNonNegative()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var emotions = new List<EmotionSample>();
        _gen.EmotionSampleGenerated += s => { lock (emotions) emotions.Add(s); };

        _gen.Start();
        await Task.Delay(500);
        _gen.Stop();

        emotions.Should().OnlyContain(s =>
            s.Scores.All(sc => sc >= 0.0f),
            because: "les scores d'émotion sont des probabilités — jamais négatifs");
    }

    [Fact]
    public async Task EmotionSamples_ConfidenceInRange()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var emotions = new List<EmotionSample>();
        _gen.EmotionSampleGenerated += s => { lock (emotions) emotions.Add(s); };

        _gen.Start();
        await Task.Delay(300);
        _gen.Stop();

        emotions.Should().OnlyContain(s =>
            s.Confidence >= 0.0f && s.Confidence <= 1.0f);
    }

    // ── FaceSamples ───────────────────────────────────────────────────────

    [Fact]
    public async Task Generator_EmitsFaceSamples()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var faces = new List<FaceSample>();
        _gen.FaceSampleGenerated += s => { lock (faces) faces.Add(s); };

        _gen.Start();
        await Task.Delay(300);
        _gen.Stop();

        faces.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FaceSamples_Has68Landmarks()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var faces = new List<FaceSample>();
        _gen.FaceSampleGenerated += s => { lock (faces) faces.Add(s); };

        _gen.Start();
        await Task.Delay(300);
        _gen.Stop();

        faces.Should().NotBeEmpty();
        faces.Should().OnlyContain(s => s.Landmarks.Length == 68,
            because: "le modèle de landmarks faciaux standard a 68 points");
    }

    [Fact]
    public async Task FaceSamples_HeadPose_PitchYawRoll_InPhysiologicalRange()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var faces = new List<FaceSample>();
        _gen.FaceSampleGenerated += s => { lock (faces) faces.Add(s); };

        _gen.Start();
        await Task.Delay(1000);
        _gen.Stop();

        // Mouvements normaux de tête : ±45 degrés en pitch/yaw, ±30 roll
        faces.Should().OnlyContain(s =>
            s.RotPitch >= -90 && s.RotPitch <= 90 &&
            s.RotYaw   >= -90 && s.RotYaw   <= 90 &&
            s.RotRoll  >= -90 && s.RotRoll  <= 90,
            because: "la rotation de tête doit rester dans des limites physiologiques");
    }

    [Fact]
    public async Task FaceSamples_ActionUnits_InRange()
    {
        _gen = new FaceTrackingGenerator(_cfg);
        var faces = new List<FaceSample>();
        _gen.FaceSampleGenerated += s => { lock (faces) faces.Add(s); };

        _gen.Start();
        await Task.Delay(500);
        _gen.Stop();

        faces.Should().NotBeEmpty();
        faces.Should().OnlyContain(s =>
            s.ActionUnits.All(au => au >= 0.0f && au <= 1.0f),
            because: "les Action Units FACS sont normalisées dans [0,1]");
    }

    // ── Machine à états émotionnels ───────────────────────────────────────

    [Fact]
    public async Task Generator_OverTime_ProducesMultipleEmotions()
    {
        var cfg = new FaceTrackingConfig
        {
            SampleRate       = 30,
            OutputMode       = FaceOutputMode.None,
            EmotionChangeSec = 0.5,   // transitions rapides pour le test
        };
        _gen = new FaceTrackingGenerator(cfg);
        var dominants = new HashSet<EmotionLabel>();
        _gen.EmotionSampleGenerated += s =>
        {
            lock (dominants) dominants.Add(s.DominantEmotion);
        };

        _gen.Start();
        await Task.Delay(8000);  // assez long pour voir plusieurs transitions
        _gen.Stop();

        dominants.Should().HaveCountGreaterThan(1,
            because: "la machine à états doit produire au moins 2 émotions différentes sur 8 secondes");
    }
}
