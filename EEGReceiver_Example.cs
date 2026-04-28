// ═══════════════════════════════════════════════════════════════════════════════
// EEG Receiver — Exemple de récepteur TCP pour tester le simulateur
// Usage : dotnet run --project EEGReceiver.csproj
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

Console.WriteLine("=== EEG Receiver TCP (port 9999) ===");
Console.WriteLine("En attente du simulateur...\n");

const int CHANNELS = 8;       // Adapter selon la config du simulateur
const int SAMPLE_RATE = 256;

try
{
    using var client = new TcpClient("127.0.0.1", 9999);
    using var stream = client.GetStream();
    using var reader = new BinaryReader(stream);

    Console.WriteLine("Connecté !\n");
    long sampleCount = 0;

    while (true)
    {
        // Chaque sample : int64 (timestamp µs) + N × float32
        long ts = reader.ReadInt64();
        float[] channels = new float[CHANNELS];
        for (int i = 0; i < CHANNELS; i++)
            channels[i] = reader.ReadSingle();

        sampleCount++;

        // Afficher toutes les 256 samples (~1 seconde)
        if (sampleCount % SAMPLE_RATE == 0)
        {
            Console.WriteLine($"[t={ts / 1_000_000.0:F2}s] "
                + $"Ch1={channels[0]:+000.0;-000.0} µV  "
                + $"Ch2={channels[1]:+000.0;-000.0} µV  "
                + $"... ({sampleCount} samples reçus)");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Erreur : {ex.Message}");
}
