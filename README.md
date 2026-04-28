# ⬡ EEG Simulator — Générateur de Données EEG Synthétiques

Application WPF (.NET 8) pour générer des signaux EEG réalistes à des fins de test de récepteurs et pipelines de traitement neurosignal.

---

## 📋 Prérequis

- **Windows 10/11** (WPF requis)
- **.NET 8 SDK** → https://dotnet.microsoft.com/download/dotnet/8.0

---

## 🚀 Installation & Lancement

```bash
# Cloner / déposer les fichiers dans un dossier
cd BioSynth

# Restaurer les packages NuGet
dotnet restore

# Compiler et lancer
dotnet run
```

---

## 🎛️ Fonctionnalités

### Canaux supportés
| Canaux | Système | Électrodes |
|--------|---------|------------|
| **8 CH**  | Système de base   | Fp1, Fp2, C3, C4, P3, P4, O1, O2 |
| **16 CH** | Étendu            | Inclut zones frontales, temporales, pariétales |
| **32 CH** | Haute densité     | Couverture complète 10-20 étendu |
| **64 CH** | Très haute densité| Couverture totale du scalp |

### Bandes de fréquence simulées
| Bande | Fréquences | Amplitude |
|-------|-----------|-----------|
| **δ Delta** | 0.5 – 4 Hz  | 80 µV |
| **θ Theta** | 4 – 8 Hz    | 40 µV |
| **α Alpha** | 8 – 13 Hz   | 100 µV |
| **β Beta**  | 13 – 30 Hz  | 20 µV |
| **γ Gamma** | 30 – 80 Hz  | 5 µV  |

Chaque canal a des phases et fréquences légèrement différentes pour un réalisme maximal.

### Artifacts simulés
- **Clignements oculaires** : burst de 150ms toutes les 3–10 secondes, sur les canaux frontaux (Fp1/Fp2), amplitude ±300 µV
- **Artifacts musculaires** : bruit haute fréquence sur canaux temporaux pendant les mêmes événements

### Modes de sortie
#### 📄 Fichier
- **CSV** : `Timestamp_us, Ch1, Ch2, ..., ChN` — lisible par Excel, Python, MATLAB
- **Binary Float32** : `int64 timestamp + N × float32`, format compact pour traitement temps réel
- **BDF** : format BioSemi Data Format simplifié (compatible EEGLab)

#### 🔌 TCP Stream
Le simulateur ouvre un serveur TCP sur `host:port`.  
Le récepteur se connecte et reçoit un flux binaire continu :
```
[int64 timestamp_µs][float32 ch1][float32 ch2]...[float32 chN]
```

#### 📡 UDP Stream
Envoi de paquets UDP (même format binaire) — sans connexion, utile pour tests broadcast.

---

## 🖥️ Interface

```
┌─────────────────┬────────────────────────────────────────┐
│  Configuration  │          OSCILLOSCOPE EEG              │
│                 │  Fp1 ~~~~~~~~~~~~~~~~~~~~              │
│ [Canaux: 8 ▼]  │  Fp2 ~~~~~~~~~~~~~~~~~~~~              │
│ [256 Hz    ▼]  │  C3  ~~~~~~~~~~~~~~~~~~~~              │
│ [Fichier   ▼]  │  C4  ~~~~~~~~~~~~~~~~~~~~              │
│                 │                                        │
│ Bruit: ━━●━━   │                                        │
│ ☑ Artifacts    │                                        │
│                 ├────────────────────────────────────────┤
│ [▶ DÉMARRER]   │  δ ████ θ ██ α █████ β █ γ ·          │
│                 │         BANDES DE FRÉQUENCE            │
└─────────────────┴────────────────────────────────────────┘
```

---

## 🔌 Exemple de récepteur TCP (Python)

```python
import socket, struct

HOST, PORT = '127.0.0.1', 9999
CHANNELS = 8

with socket.socket() as s:
    s.connect((HOST, PORT))
    while True:
        # Lire 1 sample : 8 octets timestamp + N×4 octets float
        raw = s.recv(8 + CHANNELS * 4)
        if len(raw) < 8 + CHANNELS * 4:
            continue
        ts = struct.unpack_from('<q', raw, 0)[0]
        vals = struct.unpack_from(f'<{CHANNELS}f', raw, 8)
        print(f"t={ts/1e6:.3f}s  Ch1={vals[0]:.1f}µV")
```

## 🔌 Exemple récepteur C# (fichier EEGReceiver_Example.cs inclus)

```csharp
using var client = new TcpClient("127.0.0.1", 9999);
using var reader = new BinaryReader(client.GetStream());
while (true)
{
    long ts       = reader.ReadInt64();
    float[] chs   = Enumerable.Range(0, CHANNELS)
                               .Select(_ => reader.ReadSingle()).ToArray();
    Console.WriteLine($"t={ts/1e6:F3}s  Ch1={chs[0]:F1}µV");
}
```

## 📊 Lecture du fichier CSV (Python / Pandas)

```python
import pandas as pd
import matplotlib.pyplot as plt

df = pd.read_csv('eeg_data.csv')
df['Time_s'] = df['Timestamp_us'] / 1e6

fig, axes = plt.subplots(4, 1, figsize=(12, 8), sharex=True)
for i, ax in enumerate(axes):
    col = df.columns[i + 1]  # sauter Timestamp
    ax.plot(df['Time_s'], df[col], lw=0.8)
    ax.set_ylabel(f'{col} (µV)')
    ax.set_ylim(-300, 300)
plt.xlabel('Temps (s)')
plt.tight_layout()
plt.show()
```

---

## ⚙️ Paramètres avancés (dans EEGGenerator.cs)

| Paramètre | Valeur par défaut | Description |
|-----------|------------------|-------------|
| `NoiseLevel` | 0.5 | Niveau de bruit blanc (0 = pur, 1 = très bruité) |
| `AddArtifacts` | true | Activer/désactiver les artifacts oculaires/musculaires |
| `BUFFER_SIZE` | 512 | Points affichés par canal dans l'oscilloscope |

---

## 📁 Structure du projet

```
BioSynth/
├── BioSynth.csproj          # Configuration .NET / NuGet
├── App.xaml / App.xaml.cs       # Point d'entrée WPF
├── MainWindow.xaml              # Interface principale (XAML)
├── MainWindow.xaml.cs           # Logique UI et rendu
├── EEGGenerator.cs              # Cœur : génération + sorties
├── EEGReceiver_Example.cs       # Exemple de récepteur TCP
└── README.md                    # Ce fichier
```

---

## 📝 Notes techniques

- La génération utilise **Task.Run** avec un **CancellationToken** pour ne pas bloquer l'UI
- Le rendu graphique est piloté par un **DispatcherTimer à 30 fps** (Polyline WPF natif)
- Les samples EEG sont générés à la fréquence exacte configurée via `Stopwatch` pour éviter la dérive temporelle
- Format binaire : **little-endian** (standard Intel/x86)

---

*Généré par EEG Simulator — usage à des fins de test et développement uniquement.*
