namespace BioSynth.App;

// Corps JSON des commandes envoyées par l'interface (noms en camelCase côté JS).

public sealed class LslOptions
{
    public bool    Enabled { get; set; }
    public string? Name    { get; set; }
}

public sealed class EegStartRequest
{
    /// <summary>"generate" | "replay" | "lsl"</summary>
    public string   Source      { get; set; } = "generate";
    public int      Channels    { get; set; } = 8;
    public int      SampleRate  { get; set; } = 256;
    /// <summary>"File" | "TCP" | "UDP" | "None"</summary>
    public string   Output      { get; set; } = "File";
    /// <summary>"CSV" | "Binary" | "BDF"</summary>
    public string   Format      { get; set; } = "CSV";
    public string   FilePath    { get; set; } = "";
    public string   Host        { get; set; } = "127.0.0.1";
    public int      Port        { get; set; } = 9999;
    public bool     Artifacts   { get; set; } = true;
    public double   Noise       { get; set; } = 0.5;
    public string[] ReplayPaths { get; set; } = Array.Empty<string>();
    public double   Speed       { get; set; } = 1.0;
    public bool     Loop        { get; set; }
    public int      LslStreamIndex { get; set; } = -1;
    public LslOptions Lsl       { get; set; } = new();
}

public sealed class EtStartRequest
{
    public string   Source      { get; set; } = "generate";
    public int      SampleRate  { get; set; } = 120;
    public int      ScreenW     { get; set; } = 1920;
    public int      ScreenH     { get; set; } = 1080;
    public string   Output      { get; set; } = "UDP";
    public string   FilePath    { get; set; } = "";
    public string   Host        { get; set; } = "127.0.0.1";
    public int      Port        { get; set; } = 9998;
    public bool     Saccades    { get; set; } = true;
    public bool     Blinks      { get; set; } = true;
    public bool     Pupil       { get; set; } = true;
    public double   Noise       { get; set; } = 0.3;
    public string[] ReplayPaths { get; set; } = Array.Empty<string>();
    public double   Speed       { get; set; } = 1.0;
    public bool     Loop        { get; set; }
    public bool     RawColumns  { get; set; }
    public LslOptions Lsl       { get; set; } = new();
}

public sealed class FtStartRequest
{
    /// <summary>"Face" | "Emotion"</summary>
    public string   Mode        { get; set; } = "Face";
    public int      SampleRate  { get; set; } = 30;
    public string   Output      { get; set; } = "UDP";
    public string   FilePath    { get; set; } = "";
    public string   Host        { get; set; } = "127.0.0.1";
    public int      Port        { get; set; } = 9997;
    public bool     HeadMove    { get; set; } = true;
    public bool     Micro       { get; set; } = true;
    public double   EmotionSec  { get; set; } = 4.0;
    public double   Noise       { get; set; } = 0.2;
    public LslOptions Lsl       { get; set; } = new();
}

public sealed class ZonesRequest
{
    /// <summary>Activation [0..1] par région, dans l'ordre de BrainRegion.</summary>
    public double[] Activation { get; set; } = Array.Empty<double>();
}

public sealed class PresetRequest   { public string Preset { get; set; } = "Repos"; }
public sealed class PathsRequest    { public string[] Paths { get; set; } = Array.Empty<string>(); }
public sealed class SpeedRequest    { public double Speed { get; set; } = 1.0; }
public sealed class DisplayRequest  { public int Channels { get; set; } = 4; }
public sealed class LslToggleRequest { public string Modality { get; set; } = "eeg"; public bool Enabled { get; set; } public string? Name { get; set; } }
