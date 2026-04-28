using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour BrainZoneController.
/// Valide l'activation des zones, les multiplicateurs de bandes et les presets.
/// </summary>
public class BrainZoneControllerTests
{
    // ── Initialisation ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_Creates7Zones()
    {
        var ctrl = new BrainZoneController();

        ctrl.Zones.Should().HaveCount(7);
    }

    [Fact]
    public void Constructor_AllZones_HaveDefaultActivationOfHalf()
    {
        var ctrl = new BrainZoneController();

        foreach (var zone in ctrl.Zones)
            zone.Activation.Should().BeApproximately(0.5, 0.01,
                because: $"la zone {zone.Label} doit démarrer à 0.5");
    }

    [Fact]
    public void Constructor_AllZones_HaveNonEmptyLabel()
    {
        var ctrl = new BrainZoneController();

        foreach (var zone in ctrl.Zones)
            zone.Label.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Constructor_AllZones_HaveBandMultipliersAboveZero()
    {
        var ctrl = new BrainZoneController();

        foreach (var zone in ctrl.Zones)
        {
            zone.Delta.Should().BeGreaterThan(0);
            zone.Theta.Should().BeGreaterThan(0);
            zone.Alpha.Should().BeGreaterThan(0);
            zone.Beta.Should().BeGreaterThan(0);
            zone.Gamma.Should().BeGreaterThan(0);
        }
    }

    // ── Accès aux zones ───────────────────────────────────────────────────

    [Theory]
    [InlineData(BrainRegion.Frontal)]
    [InlineData(BrainRegion.Prefrontal)]
    [InlineData(BrainRegion.Central)]
    [InlineData(BrainRegion.Temporal)]
    [InlineData(BrainRegion.Parietal)]
    [InlineData(BrainRegion.Occipital)]
    [InlineData(BrainRegion.FrontoCentral)]
    public void Zone_ReturnsCorrectRegion(BrainRegion region)
    {
        var ctrl = new BrainZoneController();

        var zone = ctrl.Zone(region);

        zone.Region.Should().Be(region);
    }

    // ── RegionOf ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fp1",  BrainRegion.Frontal)]
    [InlineData("Fp2",  BrainRegion.Frontal)]
    [InlineData("Fz",   BrainRegion.Frontal)]
    [InlineData("AF3",  BrainRegion.Prefrontal)]
    [InlineData("FC3",  BrainRegion.FrontoCentral)]
    [InlineData("C3",   BrainRegion.Central)]
    [InlineData("Cz",   BrainRegion.Central)]
    [InlineData("T7",   BrainRegion.Temporal)]
    [InlineData("T8",   BrainRegion.Temporal)]
    [InlineData("P3",   BrainRegion.Parietal)]
    [InlineData("Pz",   BrainRegion.Parietal)]
    [InlineData("O1",   BrainRegion.Occipital)]
    [InlineData("O2",   BrainRegion.Occipital)]
    [InlineData("Oz",   BrainRegion.Occipital)]
    public void RegionOf_KnownElectrode_ReturnsCorrectRegion(string name, BrainRegion expected)
    {
        BrainZoneController.RegionOf(name).Should().Be(expected);
    }

    [Fact]
    public void RegionOf_UnknownElectrode_ReturnsCentralAsFallback()
    {
        var region = BrainZoneController.RegionOf("XYZ_UNKNOWN");

        region.Should().Be(BrainRegion.Central);
    }

    // ── GetMultipliers ────────────────────────────────────────────────────

    [Fact]
    public void GetMultipliers_Returns5Values_OnePerBand()
    {
        var ctrl = new BrainZoneController();

        var mults = ctrl.GetMultipliers(0, 8);  // Ch0 = Fp1 → Frontal

        mults.Should().HaveCount(5);
    }

    [Fact]
    public void GetMultipliers_AllValuesPositive_WithDefaultActivation()
    {
        var ctrl = new BrainZoneController();

        var mults = ctrl.GetMultipliers(0, 8);

        mults.Should().OnlyContain(m => m > 0,
            because: "l'activation par défaut (0.5) doit donner des multiplicateurs positifs");
    }

    [Fact]
    public void GetMultipliers_ZeroActivation_ProducesZeroMultipliers()
    {
        var ctrl = new BrainZoneController();
        ctrl.Zone(BrainRegion.Frontal).Activation = 0.0;

        // Ch0 = Fp1 → Frontal
        var mults = ctrl.GetMultipliers(0, 8);

        mults.Should().OnlyContain(m => m == 0.0,
            because: "activation 0 doit annuler tous les multiplicateurs");
    }

    [Fact]
    public void GetMultipliers_HigherActivation_ProducesHigherMultipliers()
    {
        var ctrl = new BrainZoneController();
        ctrl.Zone(BrainRegion.Frontal).Activation = 0.2;
        var lowMults = ctrl.GetMultipliers(0, 8).ToArray();

        ctrl.Zone(BrainRegion.Frontal).Activation = 0.8;
        var highMults = ctrl.GetMultipliers(0, 8).ToArray();

        for (int i = 0; i < 5; i++)
            highMults[i].Should().BeGreaterThan(lowMults[i],
                because: $"activation 0.8 > 0.2 → multiplicateur bande {i} doit être plus élevé");
    }

    // ── UpdateChannelPower ────────────────────────────────────────────────

    [Fact]
    public void UpdateChannelPower_StoresPowerValues()
    {
        var ctrl   = new BrainZoneController();
        var powers = new double[8] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8 };

        ctrl.UpdateChannelPower(powers);

        for (int i = 0; i < 8; i++)
            ctrl.GetChannelPower(i).Should().BeApproximately(powers[i], 0.001);
    }

    [Fact]
    public void GetChannelPower_OutOfRange_ReturnsZero()
    {
        var ctrl = new BrainZoneController();

        ctrl.GetChannelPower(999).Should().Be(0.0);
    }

    // ── Presets ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Repos")]
    [InlineData("Concentration")]
    [InlineData("Méditation")]
    [InlineData("Éveil")]
    [InlineData("Sommeil léger")]
    [InlineData("Sommeil profond")]
    public void ApplyPreset_KnownPreset_SetsAllZones(string preset)
    {
        var ctrl = new BrainZoneController();

        ctrl.ApplyPreset(preset);

        // Toutes les zones doivent avoir une activation définie et positive
        foreach (var zone in ctrl.Zones)
            zone.Activation.Should().BeInRange(0.0, 1.0,
                because: $"après preset '{preset}', activation de {zone.Label} doit être dans [0,1]");
    }

    [Fact]
    public void ApplyPreset_Repos_HasHighAlpha()
    {
        var ctrl = new BrainZoneController();
        ctrl.ApplyPreset("Repos");

        // En état de repos, l'alpha doit être dominant (> beta)
        foreach (var zone in ctrl.Zones)
            zone.Alpha.Should().BeGreaterThan(zone.Beta,
                because: "le repos est caractérisé par une dominance alpha sur beta");
    }

    [Fact]
    public void ApplyPreset_Concentration_HasHighBeta()
    {
        var ctrl = new BrainZoneController();
        ctrl.ApplyPreset("Concentration");

        foreach (var zone in ctrl.Zones)
            zone.Beta.Should().BeGreaterThan(zone.Alpha,
                because: "la concentration est caractérisée par une dominance beta");
    }

    [Fact]
    public void ApplyPreset_SommeilProfond_HasHighDelta()
    {
        var ctrl = new BrainZoneController();
        ctrl.ApplyPreset("Sommeil profond");

        foreach (var zone in ctrl.Zones)
            zone.Delta.Should().BeGreaterThan(zone.Beta,
                because: "le sommeil profond est caractérisé par une dominance delta");
    }

    [Fact]
    public void ApplyPreset_Unknown_DoesNotCrash()
    {
        var ctrl = new BrainZoneController();
        var act = Action () => ctrl.ApplyPreset("INCONNU_XYZ");
        act.Should().NotThrow();
    }

    // ── ElectrodePositions ────────────────────────────────────────────────

    [Fact]
    public void ElectrodePositions_ContainsStandardElectrodes()
    {
        BrainZoneController.ElectrodePositions.Should().ContainKey("Cz");
        BrainZoneController.ElectrodePositions.Should().ContainKey("Fp1");
        BrainZoneController.ElectrodePositions.Should().ContainKey("O1");
        BrainZoneController.ElectrodePositions.Should().ContainKey("T7");
    }

    [Fact]
    public void ElectrodePositions_AllCoordinatesNormalized()
    {
        foreach (var (name, pos) in BrainZoneController.ElectrodePositions)
        {
            pos.X.Should().BeInRange(-1.1, 1.1, because: $"X de {name} doit être normalisé");
            pos.Y.Should().BeInRange(-1.1, 1.1, because: $"Y de {name} doit être normalisé");
            pos.Z.Should().BeInRange(-0.1, 1.1, because: $"Z de {name} (hauteur) doit être [0,1]");
        }
    }

    [Fact]
    public void ElectrodePositions_CzIsAtTop()
    {
        var cz = BrainZoneController.ElectrodePositions["Cz"];

        cz.X.Should().BeApproximately(0.0, 0.01, because: "Cz est sur la ligne médiane");
        cz.Z.Should().BeApproximately(1.0, 0.01, because: "Cz est au sommet du crâne");
    }

    [Fact]
    public void ElectrodePositions_LeftElectrodes_HaveNegativeX()
    {
        BrainZoneController.ElectrodePositions["Fp1"].X.Should().BeLessThan(0);
        BrainZoneController.ElectrodePositions["C3"].X.Should().BeLessThan(0);
        BrainZoneController.ElectrodePositions["T7"].X.Should().BeLessThan(0);
    }

    [Fact]
    public void ElectrodePositions_RightElectrodes_HavePositiveX()
    {
        BrainZoneController.ElectrodePositions["Fp2"].X.Should().BeGreaterThan(0);
        BrainZoneController.ElectrodePositions["C4"].X.Should().BeGreaterThan(0);
        BrainZoneController.ElectrodePositions["T8"].X.Should().BeGreaterThan(0);
    }
}
