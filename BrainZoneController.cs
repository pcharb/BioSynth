using System;
using System.Collections.Generic;

namespace BioSynth
{
    // ════════════════════════════════════════════════════════════════════
    // Zones cérébrales (régions anatomiques du système 10-20)
    // ════════════════════════════════════════════════════════════════════

    public enum BrainRegion
    {
        Frontal       = 0,
        Prefrontal    = 1,
        Central       = 2,
        Temporal      = 3,
        Parietal      = 4,
        Occipital     = 5,
        FrontoCentral = 6
    }

    // ─── Profil d'activation d'une zone : multiplicateurs par bande ───────────

    public class ZoneActivation
    {
        public BrainRegion Region     { get; set; }
        public string      Label      { get; set; } = "";
        public double      Activation { get; set; } = 0.5;   // [0..1] global
        public double      Delta      { get; set; } = 1.0;   // [0..3] par bande
        public double      Theta      { get; set; } = 1.0;
        public double      Alpha      { get; set; } = 1.0;
        public double      Beta       { get; set; } = 1.0;
        public double      Gamma      { get; set; } = 1.0;
        public (byte R, byte G, byte B) Color { get; set; } = (100, 100, 100);
    }

    // ════════════════════════════════════════════════════════════════════
    // Contrôleur principal des zones cérébrales
    // ════════════════════════════════════════════════════════════════════

    public class BrainZoneController
    {
        private readonly ZoneActivation[] _zones = new ZoneActivation[7];

        // Mapping canal → région anatomique
        private static readonly Dictionary<string, BrainRegion> ChannelRegionMap = new()
        {
            { "Fp1",BrainRegion.Frontal }, { "Fp2",BrainRegion.Frontal },
            { "Fpz",BrainRegion.Frontal },
            { "F7", BrainRegion.Frontal }, { "F8", BrainRegion.Frontal },
            { "F3", BrainRegion.Frontal }, { "F4", BrainRegion.Frontal },
            { "Fz", BrainRegion.Frontal },
            { "F1", BrainRegion.Frontal }, { "F2", BrainRegion.Frontal },
            { "F5", BrainRegion.Frontal }, { "F6", BrainRegion.Frontal },
            { "F9", BrainRegion.Frontal }, { "F10",BrainRegion.Frontal },

            { "AF3",BrainRegion.Prefrontal }, { "AF4",BrainRegion.Prefrontal },
            { "AFz",BrainRegion.Prefrontal }, { "AF7",BrainRegion.Prefrontal },
            { "AF8",BrainRegion.Prefrontal }, { "AF9",BrainRegion.Prefrontal },
            { "AF10",BrainRegion.Prefrontal },

            { "FC1",BrainRegion.FrontoCentral }, { "FC2",BrainRegion.FrontoCentral },
            { "FC3",BrainRegion.FrontoCentral }, { "FC4",BrainRegion.FrontoCentral },
            { "FC5",BrainRegion.FrontoCentral }, { "FC6",BrainRegion.FrontoCentral },
            { "FCz",BrainRegion.FrontoCentral },
            { "FT7",BrainRegion.FrontoCentral }, { "FT8",BrainRegion.FrontoCentral },
            { "FT9",BrainRegion.FrontoCentral }, { "FT10",BrainRegion.FrontoCentral },

            { "C3", BrainRegion.Central }, { "C4", BrainRegion.Central },
            { "Cz", BrainRegion.Central }, { "C1", BrainRegion.Central },
            { "C2", BrainRegion.Central }, { "C5", BrainRegion.Central },
            { "C6", BrainRegion.Central },

            { "T7", BrainRegion.Temporal }, { "T8", BrainRegion.Temporal },
            { "T9", BrainRegion.Temporal }, { "T10",BrainRegion.Temporal },
            { "TP7",BrainRegion.Temporal }, { "TP8",BrainRegion.Temporal },
            { "TP9",BrainRegion.Temporal }, { "TP10",BrainRegion.Temporal },

            { "P3", BrainRegion.Parietal }, { "P4", BrainRegion.Parietal },
            { "Pz", BrainRegion.Parietal }, { "P1", BrainRegion.Parietal },
            { "P2", BrainRegion.Parietal }, { "P5", BrainRegion.Parietal },
            { "P6", BrainRegion.Parietal }, { "P7", BrainRegion.Parietal },
            { "P8", BrainRegion.Parietal }, { "P9", BrainRegion.Parietal },
            { "P10",BrainRegion.Parietal },
            { "CP1",BrainRegion.Parietal }, { "CP2",BrainRegion.Parietal },
            { "CP3",BrainRegion.Parietal }, { "CP4",BrainRegion.Parietal },
            { "CP5",BrainRegion.Parietal }, { "CP6",BrainRegion.Parietal },
            { "CPz",BrainRegion.Parietal },

            { "O1", BrainRegion.Occipital }, { "O2", BrainRegion.Occipital },
            { "Oz", BrainRegion.Occipital },
            { "PO3",BrainRegion.Occipital }, { "PO4",BrainRegion.Occipital },
            { "POz",BrainRegion.Occipital }, { "PO7",BrainRegion.Occipital },
            { "PO8",BrainRegion.Occipital },
        };

        // Positions 3D normalisées des électrodes sur sphère crânienne [-1,1]
        // X: gauche(-) droite(+)  Y: arrière(-) avant(+)  Z: bas(0) haut(1)
        public static readonly Dictionary<string, (double X, double Y, double Z)> ElectrodePositions = new()
        {
            { "Fp1",  (-0.31, -0.95,  0.00) }, { "Fp2",  ( 0.31, -0.95,  0.00) },
            { "Fpz",  ( 0.00, -1.00,  0.00) },
            { "F7",   (-0.71, -0.71,  0.00) }, { "F8",   ( 0.71, -0.71,  0.00) },
            { "F3",   (-0.55, -0.60,  0.50) }, { "F4",   ( 0.55, -0.60,  0.50) },
            { "Fz",   ( 0.00, -0.65,  0.71) },
            { "AF3",  (-0.40, -0.85,  0.27) }, { "AF4",  ( 0.40, -0.85,  0.27) },
            { "AFz",  ( 0.00, -0.90,  0.40) },
            { "FC1",  (-0.28, -0.45,  0.81) }, { "FC2",  ( 0.28, -0.45,  0.81) },
            { "FC3",  (-0.55, -0.45,  0.67) }, { "FC4",  ( 0.55, -0.45,  0.67) },
            { "FC5",  (-0.81, -0.35,  0.43) }, { "FC6",  ( 0.81, -0.35,  0.43) },
            { "FCz",  ( 0.00, -0.45,  0.87) },
            { "C3",   (-0.71,  0.00,  0.71) }, { "C4",   ( 0.71,  0.00,  0.71) },
            { "Cz",   ( 0.00,  0.00,  1.00) },
            { "C1",   (-0.35,  0.00,  0.94) }, { "C2",   ( 0.35,  0.00,  0.94) },
            { "T7",   (-1.00,  0.00,  0.00) }, { "T8",   ( 1.00,  0.00,  0.00) },
            { "TP7",  (-0.87,  0.40,  0.27) }, { "TP8",  ( 0.87,  0.40,  0.27) },
            { "FT7",  (-0.87, -0.40,  0.27) }, { "FT8",  ( 0.87, -0.40,  0.27) },
            { "P3",   (-0.55,  0.60,  0.50) }, { "P4",   ( 0.55,  0.60,  0.50) },
            { "Pz",   ( 0.00,  0.65,  0.71) },
            { "P7",   (-0.71,  0.71,  0.00) }, { "P8",   ( 0.71,  0.71,  0.00) },
            { "CP1",  (-0.28,  0.45,  0.81) }, { "CP2",  ( 0.28,  0.45,  0.81) },
            { "CP5",  (-0.81,  0.35,  0.43) }, { "CP6",  ( 0.81,  0.35,  0.43) },
            { "O1",   (-0.31,  0.95,  0.00) }, { "O2",   ( 0.31,  0.95,  0.00) },
            { "Oz",   ( 0.00,  1.00,  0.00) },
            { "PO3",  (-0.40,  0.85,  0.27) }, { "PO4",  ( 0.40,  0.85,  0.27) },
            { "PO7",  (-0.71,  0.71,  0.00) }, { "PO8",  ( 0.71,  0.71,  0.00) },
        };

        public BrainZoneController()
        {
            var defs = new (string lbl, byte r, byte g, byte b, double d, double th, double a, double b2, double g2)[]
            {
                ("Frontal",        100, 200, 255,  0.5, 0.6, 1.2, 0.8, 0.3),
                ("Préfrontal",      80, 180, 255,  0.5, 0.7, 1.1, 0.9, 0.4),
                ("Central",          0, 255, 136,  0.5, 0.7, 1.5, 0.6, 0.2),
                ("Temporal",       255, 165,   0,  0.5, 0.8, 1.0, 0.7, 0.3),
                ("Pariétal",       139,  92, 246,  0.5, 0.6, 1.3, 0.5, 0.2),
                ("Occipital",      255,  68,  68,  0.4, 0.4, 2.0, 0.3, 0.1),
                ("Fronto-Central", 200, 230, 100,  0.5, 0.7, 1.1, 0.9, 0.4),
            };

            for (int i = 0; i < 7; i++)
            {
                var df = defs[i];
                _zones[i] = new ZoneActivation
                {
                    Region     = (BrainRegion)i,
                    Label      = df.lbl,
                    Color      = (df.r, df.g, df.b),
                    Activation = 0.5,
                    Delta      = df.d,  Theta = df.th,
                    Alpha      = df.a,  Beta  = df.b2,
                    Gamma      = df.g2,
                };
            }
        }

        public ZoneActivation[] Zones => _zones;
        public ZoneActivation   Zone(BrainRegion r) => _zones[(int)r];

        public static BrainRegion RegionOf(string name)
            => ChannelRegionMap.TryGetValue(name, out var r) ? r : BrainRegion.Central;

        // Retourne les multiplicateurs de bande pour un canal donné
        public double[] GetMultipliers(int channelIndex, int totalChannels)
        {
            string name = ChannelNames.GetChannelName(channelIndex, totalChannels);
            var    zone = _zones[(int)RegionOf(name)];
            double act  = zone.Activation;
            return new[]
            {
                zone.Delta * act * 2.0,
                zone.Theta * act * 2.0,
                zone.Alpha * act * 2.0,
                zone.Beta  * act * 2.0,
                zone.Gamma * act * 2.0,
            };
        }

        // Puissance par canal (mise à jour par le code-behind, lue par la vue 3D)
        private double[] _channelPower = Array.Empty<double>();
        public void   UpdateChannelPower(double[] powers) => _channelPower = powers;
        public double GetChannelPower(int idx) => idx < _channelPower.Length ? _channelPower[idx] : 0.0;

        // Appliquer un état mental prédéfini à toutes les zones
        public void ApplyPreset(string preset)
        {
            // (delta, theta, alpha, beta, gamma, activation)
            var p = preset switch
            {
                "Repos"          => (0.5, 0.6, 1.8, 0.4, 0.2, 0.4),
                "Concentration"  => (0.2, 0.8, 0.5, 1.8, 1.2, 0.85),
                "Méditation"     => (0.3, 1.8, 2.0, 0.2, 0.1, 0.6),
                "Éveil"          => (0.1, 0.4, 0.8, 2.0, 1.5, 1.0),
                "Sommeil léger"  => (2.0, 1.2, 0.5, 0.2, 0.1, 0.3),
                "Sommeil profond"=> (2.5, 0.5, 0.2, 0.1, 0.05,0.15),
                _                => (0.5, 0.6, 1.2, 0.7, 0.3, 0.5),
            };
            foreach (var z in _zones)
            {
                z.Delta = p.Item1; z.Theta = p.Item2; z.Alpha = p.Item3;
                z.Beta  = p.Item4; z.Gamma = p.Item5; z.Activation = p.Item6;
            }
        }
    }
}
