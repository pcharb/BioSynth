═══════════════════════════════════════════════════════════════
  LSL — Lab Streaming Layer — Instructions d'installation
═══════════════════════════════════════════════════════════════

Si LabRecorder ne voit aucun flux BioSynth, suivre ces étapes.

══════════════════════════════════════════════════════
  ÉTAPE 1 — Télécharger liblsl
══════════════════════════════════════════════════════

  URL : https://github.com/sccn/liblsl/releases

  Télécharger : liblsl-1.16.x-Win_amd64.zip  (version x64)

  Extraire le zip. Le fichier qui nous intéresse est :
    lsl.dll          ← versions récentes (≥ 1.14)  ← UTILISER CELUI-CI
    liblsl64.dll     ← versions anciennes

══════════════════════════════════════════════════════
  ÉTAPE 2 — Placer la DLL au BON endroit
══════════════════════════════════════════════════════

  Placer lsl.dll dans le dossier de l'EXÉCUTABLE, c'est-à-dire :

    BioSynth\bin\Debug\net8.0-windows\lsl.dll
    OU
    BioSynth\bin\Release\net8.0-windows\lsl.dll

  Selon que tu lances en Debug ou Release.

  PAS dans le dossier source du projet — dans le dossier bin/.

  ⚠ Nom exact : lsl.dll  (pas liblsl.dll, pas liblsl64.dll)
  Le code cherche "lsl" — Windows cherche lsl.dll.

══════════════════════════════════════════════════════
  ÉTAPE 3 — Vérifier que LSL fonctionne
══════════════════════════════════════════════════════

  1. Lancer BioSynth (dotnet run OU l'exe compilé)
  2. Vérifier que TxtLslStatus affiche "○ LSL — cocher pour activer"
     (et non "⚠ lsl.dll introuvable")
  3. Démarrer l'EEG → cocher la checkbox "EEG" dans le panneau LSL
  4. Ouvrir LabRecorder → le flux "BioSynth_EEG" doit apparaître

══════════════════════════════════════════════════════
  DÉPANNAGE
══════════════════════════════════════════════════════

  Problème : "⚠ lsl.dll introuvable"
  → La DLL n'est pas dans le bon dossier ou a le mauvais nom.
  → Solution : voir Étapes 1 et 2.

  Problème : LabRecorder ne voit pas le flux
  → Vérifier que la checkbox LSL EEG/ET/FT est cochée.
  → Le flux n'apparaît QUE si le simulateur est démarré (▶ DÉMARRER).
  → Vérifier que BioSynth et LabRecorder sont sur le même réseau.
  → Désactiver temporairement le pare-feu Windows (test).
  → LabRecorder doit être lancé APRÈS BioSynth.

  Problème : plantage ou erreur BadImageFormat
  → La DLL est x86 — télécharger la version x64 (Win_amd64).

  Problème : plusieurs DLL (liblsl64.dll ET lsl.dll présentes)
  → Garder seulement lsl.dll, supprimer les autres.

══════════════════════════════════════════════════════
  FLUX LSL PRODUITS PAR EÉGSIMULATOR
══════════════════════════════════════════════════════

  BioSynth_EEG         Type: EEG      N canaux × 256 Hz
  BioSynth_EyeTracking Type: Gaze     8 canaux × 120 Hz
  BioSynth_Emotions    Type: Emotions 10 canaux × 30 Hz
  BioSynth_Face        Type: HeadPose 6 canaux  × 30 Hz

  Compatibilité BIDS : utiliser LabRecorder → format XDF
  Conversion XDF→BIDS : https://github.com/sccn/xdf2bids

═══════════════════════════════════════════════════════════════
