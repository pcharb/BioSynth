═══════════════════════════════════════════════════════════
  BioSynth.Tests — Guide des tests unitaires
═══════════════════════════════════════════════════════════

Framework  : xUnit 2.9
Assertions : FluentAssertions 6.12
Mocks      : Moq 4.20

══════════════════════════════════════════════════════
  LANCER LES TESTS
══════════════════════════════════════════════════════

Depuis la racine du projet (dossier contenant BioSynth.sln) :

  # Tous les tests
  dotnet test

  # Avec rapport détaillé
  dotnet test --logger "console;verbosity=detailed"

  # Un seul fichier de tests
  dotnet test --filter "FullyQualifiedName~BrainZoneControllerTests"

  # Une seule méthode
  dotnet test --filter "FullyQualifiedName~ApplyPreset_Repos_HasHighAlpha"

  # Tests rapides uniquement (exclure les lents)
  dotnet test --filter "Category!=Slow"

══════════════════════════════════════════════════════
  ORGANISATION DES TESTS
══════════════════════════════════════════════════════

  EEGSampleTests.cs             → EEGSample, EEGConfig
  ChannelNamesTests.cs          → ChannelNames.GetChannelName (10-20)
  BrainZoneControllerTests.cs   → Zones, RegionOf, presets, positions
  EEGGeneratorTests.cs          → Génération de signaux (intégration)
  EEGDataReplayTests.cs         → Lecture CSV/BIN, timing, pause/speed
  EyeTrackingGeneratorTests.cs  → FSM oculomotrice, coordonnées, pupille
  FaceTrackingGeneratorTests.cs → Émotions, AU FACS, pose 6-DOF

══════════════════════════════════════════════════════
  TESTS LENTS (signaux réels)
══════════════════════════════════════════════════════

Certains tests attendent des événements physiologiques simulés
(ex. clignements, transitions émotionnelles) — ils peuvent prendre
5 à 15 secondes. C'est normal.

  EyeTrackingTests.Generator_ConfidenceDuringBlink_DropsToZero
  → attend jusqu'à 15 s pour observer un clignement

  FaceTrackingGeneratorTests.Generator_OverTime_ProducesMultipleEmotions
  → attend 8 s pour observer plusieurs transitions

══════════════════════════════════════════════════════
  COUVERTURE
══════════════════════════════════════════════════════

  dotnet test --collect:"XPlat Code Coverage"
  # Rapport dans TestResults/*/coverage.cobertura.xml

══════════════════════════════════════════════════════
  PRÉREQUIS
══════════════════════════════════════════════════════

  - .NET 8 SDK
  - Windows (WPF requis pour le projet principal)
  - Pas besoin de lsl.dll ni d'équipement matériel
  - Les tests LSL sont exclus (nécessiteraient un réseau LSL actif)
═══════════════════════════════════════════════════════════
