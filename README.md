# BioSynth

Simulateur multimodal de signaux neurophysiologiques pour le développement de pipelines d'interface cerveau-machine (BCI).

Développer un pipeline de neurorétroaction suppose un accès à des données EEG, oculaires et faciales synchronisées. En pratique, ça signifie des approbations éthiques, du matériel coûteux et des participants difficiles à recruter. BioSynth court-circuite cette dépendance en générant les trois modalités en temps réel, en les synchronisant via LSL, et en exposant une interface d'entrée pour y substituer un modèle d'IA externe.

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
- [Source IA externe](#source-ia-externe)
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
| **LSL streaming** | 4 flux indépendants · compatible LabRecorder · BIDS via XDF |
| **Source IA externe** | Inlet LSL — brancher n'importe quel modèle génératif comme source EEG |
| **Replay de données** | Lecture CSV ou BIN · timing original · vitesse variable · pause/resume |
| **TopoMap 2D** | Carte topographique temps réel · heatmap IDW · nez en haut (convention 10-20) |
| **Cerveau 3D** | Mesh OBJ anatomique · HelixToolkit · navigation libre |

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

Télécharger `liblsl-x.x.x-Win_amd64.zip` depuis [github.com/sccn/liblsl/releases](https://github.com/sccn/liblsl/releases), extraire `lsl.dll` et le placer dans le dossier de l'exécutable :

```
BioSynth\bin\Debug\net8.0-windows\lsl.dll
```

Le nom exact doit être `lsl.dll` — pas `liblsl.dll` ni `liblsl64.dll`. La barre de statut affiche `○ LSL — cocher pour activer` si la DLL est correctement détectée.

---

## Démarrage rapide

```powershell
cd BioSynth\BioSynth
dotnet run
```

1. Choisir le nombre de canaux et le sample rate dans le panneau gauche
2. Cliquer **▶ DÉMARRER** sur le module souhaité (EEG, Eye Tracking, Face Tracking)
3. Activer **LSL** via les checkboxes pour diffuser vers LabRecorder ou tout consommateur pylsl

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                            BioSynth                                   │
│                                                                       │
│  ┌─────────────────┐  ┌──────────────────┐  ┌────────────────────┐  │
│  │  EEGGenerator   │  │ EyeTracking      │  │ FaceTracking       │  │
│  │                 │  │ Generator        │  │ Generator          │  │
│  │ δ θ α β γ       │  │                  │  │                    │  │
│  │ 8–64 canaux     │  │ FSM oculomotrice │  │ FACS/AU · émotions │  │
│  │ 256 Hz          │  │ 120 Hz           │  │ Pose 6-DOF · 30 Hz │  │
│  └────────┬────────┘  └───────┬──────────┘  └─────────┬──────────┘  │
│           │                   │                        │             │
│  ┌────────▼───────────────────▼────────────────────────▼──────────┐  │
│  │                      Sorties communes                           │  │
│  │  Fichier CSV/BIN · UDP · LSL (4 flux) · TopoMap 2D · UI        │  │
│  └─────────────────────────────────────────────────────────────────┘  │
│                                                                       │
│   Source IA externe ──LSL inlet──▶ même pipeline (transparent)       │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Modules

### EEG

Signal synthétisé par superposition de 5 oscillateurs sinusoïdaux (un par bande), avec fréquences réparties de façon déterministe dans chaque bande pour éviter la synchronisation de phase entre canaux. L'amplitude est modulée lentement à 0,05 Hz.

| Bande | Plage | Amplitude de base |
|-------|-------|-------------------|
| δ Delta | 0,5 – 4 Hz | 80 µV |
| θ Theta | 4 – 8 Hz | 40 µV |
| α Alpha | 8 – 13 Hz | 100 µV |
| β Beta | 13 – 30 Hz | 20 µV |
| γ Gamma | 30 – 80 Hz | 5 µV |

**BrainZoneController** : 7 régions anatomiques avec multiplicateurs indépendants par bande. 6 presets : Repos (α occipital) · Concentration (β frontal) · Méditation (θ-α) · Éveil (β-γ) · Sommeil léger · Sommeil profond.

**Artefacts** : clignements oculaires (Fp1/Fp2, ±300 µV, 150 ms, toutes les 3–10 s) et artefacts musculaires sur les canaux temporaux.

**Source sélectionnable** : génération synthétique, replay fichier CSV/BIN, ou inlet LSL (modèle IA externe).

### Eye Tracking

FSM à 4 états : Fixation, Saccade, MicroSaccade, Blink.

Durée saccade (loi de la séquence principale) : `T = A × 2,2 ms + 12 ms`

Trajectoire sigmoïde : `x(t) = x₀ + Δx / (1 + e^(−12(t/T − 0,5)))`

Pupille : `p(t) = 3,5 + 0,18·sin(2π·0,08·t) + ε` mm

Clignements toutes les 3–9 s · Dérive de fixation par sinusoïdes (0,28 et 0,70 Hz)

**Flux LSL** `BioSynth_EyeTracking` — 8 canaux : GazeX/Y px, GazeX/Y norm, PupilL/R mm, ConfL/R

### Face Tracking

Machine à états entre 7 émotions (Neutral ≈ 35 %). Transitions par blend linéaire pondéré sur 0,8 s. Micro-expressions toutes les 8–23 s (durée 180 ms, enveloppe sinusoïdale). Arousal et valence calculés depuis le modèle circomplexe de Russell.

18 Action Units FACS · 68 landmarks 2D · Pose tête 6-DOF (translation mm + Euler ZYX)

**Flux LSL** : `BioSynth_Emotions` (10 ch : Arousal, Valence, Conf., 7 scores) · `BioSynth_Face` (6 ch : Pitch/Yaw/Roll° + PoseX/Y/Z mm)

---

## Sorties et protocoles

| Sortie | Format | Destination |
|--------|--------|-------------|
| Fichier CSV | `Timestamp_us, Ch1, Ch2, …` | chemin configurable |
| Fichier BIN | `int64` + N × `float32`, little-endian | chemin configurable |
| UDP EEG | même format BIN | port 9999 |
| UDP Eye Tracking | JSON horodaté | port 9998 |
| UDP Face Tracking | JSON émotions + pose | port 9997 |
| LSL EEG | float32, type `EEG` | autodécouverte réseau |
| LSL Eye Tracking | float32, type `Gaze` | autodécouverte réseau |
| LSL Émotions | float32, type `Emotions` | autodécouverte réseau |
| LSL HeadPose | float32, type `HeadPose` | autodécouverte réseau |

---

## LSL — Lab Streaming Layer

LSL est le protocole standard de synchronisation multimodale en neurosciences computationnelles (UCSD). Les timestamps sub-millisécondes sont communs à tous les flux, ce qui garantit l'alignement EEG + ET + FT.

**Activation** : cocher la checkbox LSL dans chaque panneau. Le nom du flux est personnalisable.

**Compatibilité** : pylsl · MATLAB · MNE-Python · OpenViBE · BrainVision Recorder · LabVIEW

**BIDS** : utiliser [LabRecorder](https://github.com/labstreaminglayer/App-LabRecorder) pour enregistrer les flux en format XDF, convertible en BIDS via [xdf2bids](https://github.com/sccn/xdf2bids).

**Dépannage** : voir `README_LSL.txt` dans le dossier du projet.

---

## Source IA externe

Le module `EEGLslInlet` découvre les flux LSL EEG disponibles sur le réseau (résolution multicast, timeout 2 s) et les injecte dans le pipeline existant. La source devient transparente pour l'oscilloscope, la TopoMap, les zones cérébrales et les flux LSL sortants.

Sélectionner **LSL IA** dans le sélecteur de source EEG, puis **Découvrir les flux LSL**.

**Exemple Python (20 lignes)** :

```python
import pylsl, time

info   = pylsl.StreamInfo("MonIA_EEG", "EEG", 64, 256, "float32", "uid-001")
outlet = pylsl.StreamOutlet(info)

while True:
    sample = mon_modele.predict()      # GAN, VAE, LLM, diffusion…
    outlet.push_sample(sample.tolist())
    time.sleep(1 / 256)
```

```bash
pip install pylsl
```

Compatible avec n'importe quel outil capable de diffuser un flux LSL : modèle Python, autre application sur le réseau local, appareil réel.

---

## Visualisation

**Oscilloscope** : défilement multi-canal, échelle configurable, sélection du nombre de canaux visibles.

**TopoMap 2D** : carte coronale (vue du dessus) avec heatmap par interpolation IDW de Shepard. Orientation 10-20 standard : nez en haut, occiput en bas. Bouton **🗺 TOPOMAP 2D** dans le panneau EEG.

**Cerveau 3D** : mesh OBJ anatomique rendu avec HelixToolkit. Navigation trackball libre.

---

## Tests unitaires

```powershell
# Depuis la racine (dossier contenant BioSynth.sln)
dotnet test

# Avec détail
dotnet test --logger "console;verbosity=detailed"

# Couverture de code
dotnet test --collect:"XPlat Code Coverage"
```

83 tests xUnit (FluentAssertions) couvrant les 7 classes principales :

| Fichier | Classe | Tests |
|---------|--------|-------|
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
│   ├── EEGGenerator.cs              # 5 bandes, zones cérébrales, artefacts
│   ├── EyeTrackingGenerator.cs      # FSM oculomotrice, loi séquence principale
│   ├── FaceTrackingGenerator.cs     # FACS/AU, 7 émotions, pose 6-DOF
│   ├── BrainZoneController.cs       # 7 régions anatomiques, positions 10-20
│   ├── EEGDataReplay.cs             # Lecture CSV/BIN, timing, vitesse variable
│   ├── EEGLslInlet.cs               # Inlet LSL — source IA externe
│   ├── EEGTopoMap.cs                # Interpolation IDW, heatmap
│   ├── TopoMapWindow.cs
│   ├── LSLStreamer.cs                # 4 outlets LSL
│   ├── BrainView3DWindow.cs         # Rendu HelixToolkit
│   ├── BrainObjLoader.cs
│   └── Assets/
│       ├── brain.obj
│       ├── brain.mtl
│       └── brain_tex.jpg
└── BioSynth.Tests/
    ├── BioSynth.Tests.csproj
    └── *.cs                         # 83 tests xUnit
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

### Python — Lire un fichier CSV enregistré

```python
import pandas as pd, matplotlib.pyplot as plt

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

---

## Licence

Ce projet est distribué sous licence **[GNU General Public License v3.0](LICENSE)**.

Toute modification doit être redistribuée sous la même licence.

---

*BioSynth — Philippe Charbonneau · Doctorat en Informatique Cognitive · UQAM · Laboratoire Renaud · 2026*
