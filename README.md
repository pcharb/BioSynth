<p align="center"><img src="docs/cirics-logo.png" alt="CIRICS" width="360"></p>

# BioSynth

Simulateur multimodal de signaux neurophysiologiques pour le développement de pipelines d'interface cerveau-machine (BCI).

Développer un pipeline de neurorétroaction suppose un accès à des données EEG, oculaires et faciales synchronisées. En pratique, ça signifie des approbations éthiques, du matériel coûteux et des participants difficiles à recruter. BioSynth court-circuite cette dépendance en générant les trois modalités en temps réel, en les synchronisant via LSL, et en exposant une interface d'entrée pour y substituer un modèle d'IA externe.

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
| **Replay de données** | CSV, Excel, BrainVision (.vhdr) ou BIN · timing original · timestamps irréguliers · marqueurs · liste de lecture · vitesse variable · pause/resume |
| **TopoMap 2D** | Carte topographique temps réel · heatmap IDW · nez en haut (convention 10-20) |
| **Cerveau 3D** | Cerveau procédural ou maillage anatomique `.obj` · heatmap interpolée sur la surface · électrodes 10-10 · navigation libre |
| **Multiplateforme** | Une seule application pour Windows, Linux et macOS · fenêtre dédiée · aucun paquet NuGet requis |

---

## Prérequis

- **Windows 10 / 11, Linux ou macOS**
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** (Ubuntu / Debian : `sudo apt install dotnet-sdk-8.0` ; Fedora : `sudo dnf install dotnet-sdk-8.0` ; macOS : `brew install dotnet@8`)
- **Un navigateur Chromium** pour la fenêtre d'application : Edge (présent sur tout Windows 10/11), Chrome, Chromium ou Brave. À défaut, l'interface s'ouvre dans un onglet du navigateur par défaut.
- **liblsl** *(optionnel, uniquement pour LSL)* : voir [LSL](#lsl-optionnel).

---

## Installation

```bash
git clone https://github.com/pcharb/BioSynth.git
cd BioSynth
```

### LSL (optionnel)

| Système | Installation |
|---------|--------------|
| Windows | Télécharger `liblsl-x.x.x-Win_amd64.zip` depuis [github.com/sccn/liblsl/releases](https://github.com/sccn/liblsl/releases) et placer `lsl.dll` dans `BioSynth\bin\Release\net8.0\` (ou `Debug`). |
| Ubuntu / Debian | `sudo apt install ./liblsl-1.16.2-noble_amd64.deb` (paquet `jammy` pour Ubuntu 22.04), même page de téléchargement. |
| macOS | `brew install labstreaminglayer/tap/lsl` |

La console affiche `liblsl : détectée` au démarrage et la barre d'état `○ LSL — cocher pour activer`. Dépannage : `README_LSL.txt`.

---

## Démarrage rapide

| Système | Commande |
|---------|----------|
| Windows | double-clic sur `BioSynth.cmd` |
| Linux, macOS | `./run.sh` |
| Tous | `cd BioSynth` puis `dotnet run` |

L'interface s'ouvre dans une fenêtre BioSynth (navigateur en mode application, sans onglets ni barre d'adresse). Fermer la fenêtre arrête l'application ; Ctrl+C dans la console aussi.

1. Choisir le nombre de canaux et le sample rate dans l'onglet EEG
2. Cliquer **▶ DÉMARRER** sur le module souhaité (EEG, Eye Tracking, Face Tracking)
3. Activer **LSL** via les cases à cocher pour diffuser vers LabRecorder ou tout consommateur pylsl
4. Suivre les signaux dans l'onglet **Visualisation** ; la TopoMap s'ouvre dans sa propre fenêtre

**Options** (après `dotnet run --` ou en argument des lanceurs) :

| Option | Effet |
|--------|-------|
| `--port 6000` | autre port que 5080 |
| `--browser` | ouvrir dans un onglet du navigateur par défaut plutôt que dans une fenêtre dédiée |
| `--no-browser` | n'ouvrir aucune fenêtre (machine sans écran, accès par tunnel SSH) |
| `--keep-server` | laisser le serveur actif après la fermeture de la fenêtre |
| `--lan` | écouter sur toutes les interfaces réseau. Par défaut, BioSynth n'écoute que sur `127.0.0.1` ; à réserver à un réseau de confiance, car l'interface donne accès aux fichiers de la machine |

Relancer BioSynth alors qu'il tourne déjà ouvre simplement une nouvelle fenêtre sur l'instance existante.

### Version portable

```bash
dotnet publish BioSynth -c Release -o dist
```

Le dossier `dist/` (environ 0,5 Mo) se copie tel quel sur n'importe quelle machine Windows, Linux ou macOS disposant du runtime ASP.NET Core 8 (`aspnetcore-runtime-8.0` sous Linux) et se lance par `dotnet BioSynth.dll`. Pour une version qui n'exige aucun runtime installé, ajouter `-r win-x64`, `-r linux-x64` ou `-r osx-arm64` et `--self-contained` (un dossier par système).

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

**Source sélectionnable** : génération synthétique, replay d'un enregistrement, ou inlet LSL (modèle IA externe).

### Organisation en onglets

La fenêtre est divisée en quatre onglets : **EEG**, **Eye Tracking** et **Face Tracking** regroupent chacun la configuration, la source (génération, replay, inlet LSL) et le bouton de démarrage de leur modalité ; **Visualisation** rassemble tous les graphes (oscilloscope EEG, carte de regard et traces oculométriques, émotions et pose faciale, spectre et statistiques). Les acquisitions continuent en arrière-plan quel que soit l'onglet affiché ; seul le redessin des graphes est suspendu quand l'onglet Visualisation n'est pas visible, ce qui économise du CPU.

### Thème clair / sombre et identité visuelle

L'interface reprend l'identité du CIRICS (Centre interdisciplinaire de recherche et innovation en cybersécurité et société) : logo dans l'en-tête, les fenêtres TopoMap et Cerveau 3D, la barre d'état et l'icône, et palette déclinée du bleu du logo (`#748DC6`). Les couleurs sont des variables CSS dans `BioSynth/wwwroot/app.css`, en trois familles :

- **Habillage** : `Brand*` (bleu CIRICS ; le bleu exact du logo est réservé au logo et aux grands repères, les textes utilisent une version plus foncée en clair, plus claire en sombre, pour garder un contraste d'au moins 4,5:1) et `Mod*` (une teinte par modalité dans la même famille : EEG bleu, Eye Tracking bleu-vert, Face Tracking bleu-violet) ;
- **Données** : `S1` à `S8`, palette catégorielle à ordre fixe pour les traces des canaux, les émotions, les bandes et les colonnes oculométriques, validée pour la séparation sous daltonisme et le contraste sur les deux fonds. Les libellés restent en couleur de texte neutre, la couleur étant portée par la pastille ou la trace ;
- **États** : `Good`, `Warn`, `Critical`, `Danger`.

- **Magnitude** : `Seq0` (faible) à `Seq4` (fort), rampe séquentielle à teinte unique tirée du bleu CIRICS, pour la TopoMap et le Cerveau 3D. Elle s'inverse avec le thème (en clair, faible = pâle et fort = foncé ; en sombre, l'inverse) et chaque version est validée (luminance monotone, écarts visibles, extrémité faible distincte du fond). Le cortex du modèle 3D utilise des gris-bleus neutres (`Cortex*`).

Les trois fenêtres (principale, TopoMap, Cerveau 3D) partagent ces jetons et suivent le même thème.

Le thème « ◐ Auto » suit le réglage clair/sombre du système (Windows, GNOME, KDE, macOS) et bascule en direct quand il change. La liste déroulante en haut à droite permet de forcer ☀ Clair ou ☾ Sombre. Le choix du thème, l'onglet actif et les réglages des formulaires sont mémorisés d'une session à l'autre. Les logos sont dans `BioSynth/wwwroot/img/` (pictogramme et logo complet en masque transparent, recoloré par CSS selon le thème).

### Fichiers

Les boîtes de dialogue d'ouverture et d'enregistrement sont remplacées par un sélecteur intégré qui parcourt les dossiers de la machine (Ctrl-clic et Maj-clic pour une liste de lecture). Les fichiers de sortie vont par défaut dans le dossier `BioSynth` du répertoire personnel (`~/BioSynth`, `C:\Users\<nom>\BioSynth`). Le mode de sortie « Aucune (LSL seul) » évite d'écrire un fichier ou d'émettre en UDP/TCP quand seul LSL est utile.

### Replay d'enregistrements réels

Le mode Replay rejoue un fichier enregistré comme s'il était acquis en direct, à la cadence d'origine (ou ×0,5 à ×10), avec pause et boucle. Formats pris en charge par `RecordingLoader` :

| Format | Détails |
|--------|---------|
| **BrainVision** `.vhdr` + `.vmrk` + `.eeg`/`.dat` | ASCII ou BINARY (INT_16, INT_32, IEEE_FLOAT_32), MULTIPLEXED ou VECTORIZED, résolution par canal, marqueurs du `.vmrk`. Sélectionner indifféremment le `.vhdr` ou le fichier de données. |
| **CSV générique** | Séparateur (`,` `;` tab) et virgule décimale détectés. Colonne de temps devinée (`Time`, `Timestamp`, `MilliSec`, `Sec`...), colonne de marqueurs devinée (`Marker`, `Event`, `Trigger`, `Var8`...). Les colonnes `Frame`, `DataPoint`, `Index` sont ignorées. Si les pas de temps sont réguliers (écart < 5 %), une fréquence est déclarée ; sinon les échantillons sont rejoués sur leurs timestamps (ex. eye-tracking à ~90 Hz avec gigue). |
| **Excel** `.xlsx` | Même logique que le CSV, première feuille (lecteur interne `XlsxReader`). |
| **CSV maison / BIN** | Format d'export de BioSynth (`Timestamp_us,Ch1,...` ou `int64 + N × float32`), inchangé. |

Les noms de canaux du fichier remplacent la nomenclature 10-20 dans les métadonnées du flux LSL EEG, et les marqueurs sont publiés sur un second flux LSL `<nom>_Markers` (type `Markers`, 1 canal string), compatible LabRecorder et MNE.

**Liste de lecture** : le dialogue de sélection accepte plusieurs fichiers du même type (mêmes canaux, dans le même ordre). Ils sont joués à la suite par ordre alphabétique, puis en boucle si « Boucler » est coché. Les timestamps restent continus d'un fichier à l'autre (`ContinuousTimestamps`), le flux LSL n'est jamais interrompu, et l'étiquette d'enregistrement indique `REPLAY 2/5`. Les canaux sont alignés par nom sur l'union de tous les fichiers (ordre du premier, puis les canaux inédits) ; un canal absent d'un fichier est rejoué à 0 sur toute sa durée et l'inspection affiche un avertissement ⚠ qui nomme le fichier et le canal. Des fréquences différentes sont tolérées, chaque fichier étant rejoué à sa propre cadence, avec avertissement.

**Replay oculométrique** : le panneau Eye Tracking a aussi une source « Replay » (radio Générer / Replay). Il utilise le même moteur (formats, liste de lecture, timestamps irréguliers, vitesse, pause, boucle) via `EyeTrackingReplay`. Les colonnes reconnues par leur nom (`GazeX`, `GazeY`, `PupilL_mm`, `ConfR`, `Blink`, `Velocity_dps`... y compris l'export CSV de BioSynth) alimentent les champs standards d'`EyeSample` et donc les sorties UDP/TCP binaires et les statistiques ; toutes les colonnes numériques du fichier, reconnues ou non (par exemple des angles de regard), sont conservées dans `EyeSample.Raw` et publiées telles quelles sur le flux LSL `Gaze`, avec les noms de colonnes du fichier comme labels. La case « Colonnes brutes en sortie » (option `IncludeRawColumns`) les fait aussi passer dans les sorties UDP/TCP, qui basculent alors du binaire 46 octets vers une ligne JSON par échantillon (`{"type":"et","t_us":…,"gx":…,"raw":{"AngleYeuxDevant":…}}`), et dans l'export CSV, où elles sont ajoutées après les colonnes standards. Décochée, les sorties gardent le format binaire habituel.

Si l'heuristique se trompe de colonne, `TabularReader.TimeColumn` et `TabularReader.MarkerColumn` permettent de forcer les noms avant l'ouverture.

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

**TopoMap 2D** : carte coronale (vue du dessus) avec heatmap par interpolation IDW de Shepard. Orientation 10-20 standard : nez en haut, occiput en bas. Bouton **🗺 TOPOMAP 2D** dans l'onglet EEG ; la carte s'ouvre dans une fenêtre séparée et peut rester visible pendant qu'on travaille dans les autres onglets.

**Cerveau 3D** : bouton **🧠 CERVEAU 3D** dans l'onglet EEG (fenêtre séparée, rendu WebGL avec three.js embarqué, donc sans connexion). La heatmap est interpolée sommet par sommet à partir des électrodes, avec la même normalisation que la TopoMap (puissance du canal 70 %, activation de la zone 30 %), sur la même rampe de couleur que la TopoMap. Les électrodes sont projetées sur la surface du modèle ; celles de la ligne médiane se placent dans la scissure inter-hémisphérique. Vues prédéfinies (face, dessus, gauche, droite, arrière ; gauche et droite désignent les côtés du sujet), rotation automatique, étiquettes.

Le modèle par défaut est un cerveau procédural (hémisphères à circonvolutions, scissures latérales, cervelet, tronc cérébral), suffisant pour situer l'activité mais non anatomiquement exact. Pour un maillage anatomique, placer `brain.obj` (ou `Brain_Model.obj`) dans le dossier de données `~/BioSynth/` : il est chargé automatiquement. Le bouton **OBJ…** charge un autre fichier pour la session. Les parties nommées `Brain_Part_01` à `Brain_Part_06` sont reconnues (hémisphères, cervelet et tronc colorés séparément) ; les boutons **↻ 90°** et **Axe Y / Axe Z** corrigent l'orientation d'un modèle exporté autrement, et ce réglage est mémorisé.

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

140 cas de test xUnit (FluentAssertions, chaque ligne `InlineData` comptant pour un cas), sur le moteur `BioSynth.Core`. Ils s'exécutent de la même façon sous Windows, Linux et macOS. Répartition par fichier :

| Fichier | Classe | Tests |
|---------|--------|-------|
| `EEGSampleTests.cs` | `EEGSample`, `EEGConfig` | 8 |
| `ChannelNamesTests.cs` | `ChannelNames` | 7 |
| `BrainZoneControllerTests.cs` | `BrainZoneController` | 23 |
| `EEGGeneratorTests.cs` | `EEGGenerator` | 10 |
| `EEGDataReplayTests.cs` | `EEGDataReplay` | 14 |
| `RecordingReadersTests.cs` | `RecordingLoader`, `TabularReader`, `BrainVisionReader`, liste de lecture, replay oculométrique | 20 |
| `EyeTrackingGeneratorTests.cs` | `EyeTrackingGenerator` | 8 |
| `FaceTrackingGeneratorTests.cs` | `FaceTrackingGenerator` | 13 |

---

## Structure du projet

```
BioSynth/
├── BioSynth.sln
├── BioSynth.cmd                     # Lanceur Windows (double-clic)
├── run.sh                           # Lanceur Linux / macOS
├── README_LSL.txt                   # Installation et dépannage de liblsl
├── BioSynth/                        # Application (serveur local + interface)
│   ├── BioSynth.csproj
│   ├── Program.cs                   # Serveur, API, WebSocket, sélecteur de fichiers, options
│   ├── AppWindow.cs                 # Fenêtre d'application (Edge / Chrome / Chromium en mode --app)
│   ├── SimulatorHost.cs             # Pilotage des générateurs, replay, inlet et flux LSL
│   ├── EtDisplayState.cs            # Traînée du regard, compteurs, mode colonnes brutes
│   ├── Requests.cs                  # Commandes envoyées par l'interface
│   └── wwwroot/
│       ├── index.html, app.js       # Onglets EEG, Eye Tracking, Face Tracking, Visualisation
│       ├── topomap.html, topomap.js # Carte topographique (fenêtre séparée)
│       ├── brain3d.html, brain3d.js # Cerveau 3D (fenêtre séparée)
│       ├── lib/three/               # three.js 0.160 (licence MIT), embarqué
│       ├── app.css, theme.js        # Palette CIRICS, thèmes clair / sombre
│       ├── img/                     # Logo CIRICS (pictogramme, logo complet, icône)
├── BioSynth.Core/                   # Moteur (net8.0, sans dépendance)
│   ├── EEGGenerator.cs              # 5 bandes, zones cérébrales, artefacts, sorties
│   ├── BrainZoneController.cs       # 7 régions anatomiques, positions 10-20
│   ├── EyeTrackingGenerator.cs      # FSM oculomotrice, loi séquence principale
│   ├── EyeTrackingReplay.cs         # Rejeu d'enregistrements oculométriques réels
│   ├── FaceTrackingGenerator.cs     # FACS/AU, 7 émotions, pose 6-DOF
│   ├── EEGDataReplay.cs             # Rejeu : timing, vitesse variable, marqueurs
│   ├── RecordingReaders.cs          # Lecteurs CSV générique, Excel, BrainVision
│   ├── XlsxReader.cs                # Lecture .xlsx (ZIP + XML)
│   ├── LSLStreamer.cs               # 4 outlets LSL + marqueurs
│   └── EEGLslInlet.cs               # Inlet LSL : source IA externe
├── BioSynth.Tests/                  # 140 cas xUnit
├── docs/
│   └── cirics-logo.png              # Logo pour ce README
└── examples/
    └── EEGReceiver_Example.cs       # Récepteur TCP minimal
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

## Notes de version (octobre 2026)

Application unique pour Windows, Linux et macOS : serveur local ASP.NET Core et interface dans une fenêtre de navigateur dédiée, vue Cerveau 3D en WebGL. Corrections du moteur :

- **Source IA externe (inlet LSL)** : `lsl_pull_sample_f` était déclaré avec un retour `int` au lieu de `double` ; en x64 l'horodatage était toujours lu comme 0 et chaque échantillon était ignoré. Le flux est aussi ouvert explicitement (`lsl_open_stream`) à la connexion, et `lsl_resolve_byprop` renvoie bien un `int`.
- **Flux de marqueurs LSL** : `CF_STRING` valait 4 (`int32` dans liblsl) au lieu de 3 ; les marqueurs étaient publiés comme des entiers nuls.
- **Replay BIN** : le nombre de canaux était le premier diviseur de la taille du fichier (un fichier 8 canaux × 256 trames était lu comme 2 canaux). Il est maintenant déduit de la régularité des horodatages.
- **Replay du format d'export BioSynth** : la colonne `Timestamp_us` n'était pas reconnue comme colonne de temps par le lecteur générique ; les horodatages sont aussi arrondis au lieu d'être tronqués.
- **Excel** : lecture par `XlsxReader` (ZIP + XML), sans ClosedXML, ce qui garde le moteur sans dépendance.
- **Tests** : la suite ne compilait plus (`OutputMode.None`, `EyeOutputMode`, `FaceOutputMode`, `Landmarks`, `DominantEmotion`). `OutputMode.None` est ajouté au moteur et les tests suivent l'API actuelle ; les 140 cas passent sous Linux.
- **Positions d'électrodes** : 28 électrodes des montages 32 et 64 canaux (`POz`, `F1`, `C5`, `CPz`, `T9`...) n'avaient pas de coordonnées et n'apparaissaient ni sur la TopoMap ni en 3D. Elles sont ajoutées à `BrainZoneController.ElectrodePositions` (points médians normalisés entre voisines 10-20, rangée inférieure juste sous l'équateur).
- **TopoMap** : le gain de puissance (× 5) saturait la carte ; il est ramené à × 1,5 pour que les états mentaux couvrent toute l'échelle de couleurs. La puissance est calculée sur tous les canaux, et non sur les seuls canaux affichés.

---

## Licence

Ce projet est distribué sous licence **[GNU General Public License v3.0](LICENSE)**.

Toute modification doit être redistribuée sous la même licence.

---

*BioSynth — Philippe Charbonneau · 2026*

