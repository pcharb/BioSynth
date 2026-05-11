# BioSynth

**Simulateur multimodal de signaux neurophysiologiques pour la recherche en interface cerveau-machine (BCI)**

BioSynth génère en temps réel des signaux EEG, oculaires (eye tracking) et faciaux (face tracking) synthétiques, anatomiquement réalistes, et les diffuse via UDP, LSL ou fichier. Conçu pour développer et valider des pipelines BCI avant l'acquisition de données réelles sur participants.

Développé dans le cadre d'un projet doctoral en Informatique Cognitive — **UQAM, Laboratoire Renaud**.

---

## Table des matières

- [Fonctionnalités](#fonctionnalités)
- [Prérequis](#prérequis)
- [Installation](#installation)
- [Démarrage rapide](#démarrage-rapide)
- [Architecture](#architecture)
- [Modules](#modules)
- [Sorties et protocoles](#sorties-et-protocoles)
- [LSL — Lab Streaming Layer](#lsl--lab-streaming-layer)
- [Source de données IA externe](#source-de-données-ia-externe)
- [Visualisation](#visualisation)
- [Tests unitaires](#tests-unitaires)
- [Structure du projet](#structure-du-projet)
- [Exemples de consommateurs](#exemples-de-consommateurs)
- [Licence](#licence)

---

## Fonctionnalités

| Fonctionnalité | Détail |
|----------------|--------|
| **EEG synthétique** | 5 bandes (δ θ α β γ) · 8 à 64 canaux · système 10-20 · 256 Hz |
| **Zones cérébrales** | 7 régions anatomiques · sliders d'activation · 6 presets d'états mentaux |
| **Eye Tracking** | FSM oculomotrice · saccades · clignements · pupille · 120 Hz |
| **Face Tracking** | FACS/AU · 7 émotions · pose 6-DOF · machine à états · 30 Hz |
| **LSL streaming** | 4 flux indépendants · compatible LabRecorder / BIDS via XDF |
| **Source IA externe** | Réception d'un flux LSL entrant depuis n'importe quel modèle tiers |
| **Replay de données** | Lecture CSV ou BIN · timing original · vitesse variable · pause/resume |
| **TopoMap 2D** | Carte topographique en temps réel · heatmap IDW · fenêtre dédiée |
| **Cerveau 3D** | Mesh OBJ anatomique réel · HelixToolkit · navigation libre |

---

## Prérequis

- **Windows 10 / 11** — WPF requis
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**
- **[lsl.dll](https://github.com/sccn/liblsl/releases)** *(optionnel — uniquement pour le streaming LSL)*

---

## Installation

```powershell
git clone https://github.com/pcharb/BioSynth.git
cd BioSynth\BioSynth
dotnet restore
```

### LSL (optionnel)

Télécharger `liblsl-x.x.x-Win_amd64.zip` depuis [github.com/sccn/liblsl/releases](https://github.com/sccn/liblsl/releases), extraire `lsl.dll` et le placer dans :

```
BioSynth\bin\Debug\net8.0-windows\lsl.dll
```

---

## Démarrage rapide

```powershell
cd BioSynth\BioSynth
dotnet run
```

1. Choisir le nombre de canaux et le sample rate dans le panneau gauche
2. Cliquer **▶ DÉMARRER** pour l'EEG, l'Eye Tracking ou le Face Tracking
3. Observer les signaux dans l'oscilloscope en temps réel
4. Activer **LSL** via les checkboxes pour diffuser vers LabRecorder ou tout consommateur pylsl

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                        BioSynth                          │
│                                                          │
│  ┌─────────────┐  ┌───────────────┐  ┌───────────────┐  │
│  │ EEGGenerator│  │ EyeTracking   │  │ FaceTracking  │  │
│  │ δ θ α β γ   │  │ Generator     │  │ Generator     │  │
│  │ 8–64 ch     │  │ FSM · 120 Hz  │  │ FACS · 30 Hz  │  │
│  └──────┬──────┘  └──────┬────────┘  └──────┬─────── ┘  │
│         │                │                  │            │
│  ┌──────▼────────────────▼──────────────────▼──────────┐ │
│  │               Sorties communes                       │ │
│  │  Fichier CSV/BIN · UDP · LSL Stream · TopoMap 2D    │ │
│  └──────────────────────────────────────────────────────┘ │
│                                                          │
│  Source LSL externe (IA) ──inlet──▶ même pipeline       │
└──────────────────────────────────────────────────────────┘
```

---

## Modules

### Module EEG

Génération par **superposition additive de 5 oscillateurs sinusoïdaux** avec phases randomisées et fréquences décalées par canal. L'amplitude est modulée lentement à 0.05 Hz.

| Bande | Plage | Amplitude de base |
|-------|-------|-------------------|
| δ Delta | 0.5 – 4 Hz | 80 µV |
| θ Theta | 4 – 8 Hz | 40 µV |
| α Alpha | 8 – 13 Hz | 100 µV |
| β Beta | 13 – 30 Hz | 20 µV |
| γ Gamma | 30 – 80 Hz | 5 µV |

**7 zones cérébrales** avec multiplicateurs indépendants par bande et sliders d'activation [0–1].

**6 presets d'états mentaux** : Repos · Concentration · Méditation · Éveil · Sommeil léger · Sommeil profond

**Artefacts** : clignements oculaires (Fp1/Fp2, ±300 µV, 150 ms) et artefacts musculaires temporaux.

**Source** : génération synthétique **ou** replay fichier CSV/BIN **ou** flux LSL entrant (IA).

---

### Module Eye Tracking

Machine à états finis à 4 états : **Fixation → Saccade → MicroSaccade → Blink**

- Saccades : durée calculée d'après la **loi de la séquence principale**, trajectoire sigmoïde
- Fixations : 120 – 970 ms, dérive physiologique involontaire (sinusoïdes 0.28 – 0.70 Hz)
- Clignements : toutes les 3 à 9 s, durée 150 ms, confidence → 0
- Pupille : oscillation lente à 0.08 Hz + dilatation pendant les saccades

**Flux LSL** `BioSynth_EyeTracking` — 8 canaux : GazeX/Y px, GazeX/Y norm, Pupil L/R mm, Conf L/R

---

### Module Face Tracking

**Machine à états émotionnels** avec transitions douces (lissage exponentiel, τ = 0.6 s). Micro-expressions toutes les 8 à 23 s (enveloppe sinusoïdale, 180 ms).

Émotions : Neutral · Happy · Sad · Angry · Surprised · Fearful · Disgusted

**Modèle circomplexe de Russell** pour Arousal et Valence continus.

**18 Action Units FACS** avec intensités [0, 1] · **68 landmarks faciaux 2D** · Pose tête 6-DOF

**Flux LSL** :
- `BioSynth_Emotions` : Arousal, Valence, Confidence, 7 scores (10 canaux)
- `BioSynth_Face` : PoseX/Y/Z mm + Pitch/Yaw/Roll degrés (6 canaux)

---

## Sorties et protocoles

| Sortie | Format | Destination |
|--------|--------|-------------|
| Fichier CSV | `Timestamp_us, Ch1, Ch2, ...` | chemin configurable |
| Fichier BIN | `int64 + N × float32` little-endian | chemin configurable |
| UDP EEG | binaire (même format BIN) | port 9999 |
| UDP Eye Tracking | JSON horodaté | port 9998 |
| UDP Face Tracking | JSON émotions + pose | port 9997 |
| LSL EEG | float32, type `EEG` | autodécouverte réseau |
| LSL Eye Tracking | float32, type `Gaze` | autodécouverte réseau |
| LSL Émotions | float32, type `Emotions` | autodécouverte réseau |
| LSL HeadPose | float32, type `HeadPose` | autodécouverte réseau |

---

## LSL — Lab Streaming Layer

LSL est le protocole standard de synchronisation en neurosciences computationnelles (UCSD Swartz Center). Les timestamps sub-millisécondes communs à tous les flux garantissent l'alignement EEG + ET + FT.

**Activation** : cocher la checkbox LSL dans chaque panneau. Le nom du flux est personnalisable.

**Compatibilité** : pylsl · MATLAB · MNE-Python · OpenViBE · BrainVision Recorder · LabVIEW

**BIDS** : utiliser [LabRecorder](https://github.com/labstreaminglayer/App-LabRecorder) → format XDF → convertible en BIDS via [xdf2bids](https://github.com/sccn/xdf2bids).

---

## Source de données IA externe

Sélectionner **LSL IA** dans le sélecteur de source EEG, cliquer **Découvrir les flux LSL**, sélectionner la source et démarrer. BioSynth reçoit le flux et le traite dans son pipeline complet.

```python
# Exemple minimal — source IA Python
import pylsl, time

info   = pylsl.StreamInfo("MonIA_EEG", "EEG", 64, 256, "float32", "uid-001")
outlet = pylsl.StreamOutlet(info)

while True:
    sample = mon_modele.predict()      # GAN, VAE, LLM, diffusion...
    outlet.push_sample(sample.tolist())
    time.sleep(1 / 256)
```

```bash
pip install pylsl
```

---

## Visualisation

| Vue | Description |
|-----|-------------|
| **Oscilloscope** | Multi-canal défilant, échelle configurable |
| **TopoMap 2D** | Carte coronale · heatmap IDW · ouverture à la demande |
| **Cerveau 3D** | Mesh OBJ anatomique · HelixToolkit · navigation trackball |

---

## Tests unitaires

**83 tests** xUnit (FluentAssertions + Moq) couvrant toutes les classes métier.

```powershell
# Depuis la racine (dossier contenant BioSynth.sln)
dotnet test

# Avec rapport détaillé
dotnet test --logger "console;verbosity=detailed"

# Couverture de code
dotnet test --collect:"XPlat Code Coverage"
```

| Fichier de tests | Classe | Tests |
|------------------|--------|-------|
| `EEGSampleTests.cs` | `EEGSample`, `EEGConfig` | 8 |
| `ChannelNamesTests.cs` | `ChannelNames` | 7 |
| `BrainZoneControllerTests.cs` | `BrainZoneController` | 23 |
| `EEGGeneratorTests.cs` | `EEGGenerator` | 10 |
| `EEGDataReplayTests.cs` | `EEGDataReplay` | 14 |
| `EyeTrackingGeneratorTests.cs` | `EyeTrackingGenerator` | 8 |
| `FaceTrackingGeneratorTests.cs` | `FaceTrackingGenerator` | 13 |

---

## Structure du projet

```
BioSynth/
├── BioSynth.sln
├── BioSynth/
│   ├── BioSynth.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / .cs
│   ├── EEGGenerator.cs
│   ├── EyeTrackingGenerator.cs
│   ├── FaceTrackingGenerator.cs
│   ├── BrainZoneController.cs
│   ├── EEGDataReplay.cs
│   ├── EEGLslInlet.cs
│   ├── EEGTopoMap.cs
│   ├── TopoMapWindow.cs
│   ├── LSLStreamer.cs
│   ├── BrainView3DWindow.cs
│   ├── BrainObjLoader.cs
│   └── Assets/
│       ├── brain.obj
│       ├── brain.mtl
│       └── brain_tex.jpg
└── BioSynth.Tests/
    ├── BioSynth.Tests.csproj
    ├── EEGSampleTests.cs
    ├── ChannelNamesTests.cs
    ├── BrainZoneControllerTests.cs
    ├── EEGGeneratorTests.cs
    ├── EEGDataReplayTests.cs
    ├── EyeTrackingGeneratorTests.cs
    └── FaceTrackingGeneratorTests.cs
```

---

## Exemples de consommateurs

### Python — UDP EEG

```python
import socket, struct

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(('0.0.0.0', 9999))

CHANNELS = 8
while True:
    data, _ = sock.recvfrom(8 + CHANNELS * 4)
    ts       = struct.unpack_from('<q', data, 0)[0]
    channels = struct.unpack_from(f'<{CHANNELS}f', data, 8)
    print(f"t={ts/1e6:.3f}s  Fp1={channels[0]:.1f}µV")
```

### Python — LSL EEG

```python
import pylsl

streams = pylsl.resolve_stream('type', 'EEG')
inlet   = pylsl.StreamInlet(streams[0])

while True:
    sample, timestamp = inlet.pull_sample()
    print(f"t={timestamp:.3f}  Fp1={sample[0]:.1f}µV")
```

### Python — Visualiser un fichier CSV

```python
import pandas as pd
import matplotlib.pyplot as plt

df = pd.read_csv('eeg_data.csv')
df['Time_s'] = df['Timestamp_us'] / 1e6

for col in df.columns[1:5]:
    plt.plot(df['Time_s'], df[col], label=col, lw=0.8)

plt.xlabel('Temps (s)')
plt.ylabel('Amplitude (µV)')
plt.legend()
plt.tight_layout()
plt.show()
```

### C# — TCP EEG

```csharp
using var client = new TcpClient("127.0.0.1", 9999);
using var reader = new BinaryReader(client.GetStream());
const int CHANNELS = 8;

while (true)
{
    long ts    = reader.ReadInt64();
    float[] ch = Enumerable.Range(0, CHANNELS)
                            .Select(_ => reader.ReadSingle()).ToArray();
    Console.WriteLine($"t={ts/1e6:F3}s  Fp1={ch[0]:F1}µV");
}
```

---

## Licence

Ce projet est distribué sous licence **[GNU General Public License v3.0](LICENSE)**.

Toute modification doit être redistribuée sous la même licence — le code source doit rester accessible à la communauté scientifique.

---

*BioSynth — Philippe Charbonneau · Doctorat en Informatique Cognitive · UQAM · Laboratoire Renaud · 2026*
