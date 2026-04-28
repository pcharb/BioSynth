═══════════════════════════════════════════════════════════════
  VISUALISATION 3D DU CERVEAU — Instructions
═══════════════════════════════════════════════════════════════

Pour obtenir un vrai cerveau anatomique 3D, télécharge l'un
des modèles gratuits ci-dessous et place-le dans :

  BioSynth/Assets/brain.obj
  BioSynth/Assets/brain.mtl  (si disponible)

══════════════════════════════════════════════════════
  OPTION 1 — Recommandée : BrainMesh (libre de droits)
══════════════════════════════════════════════════════

  URL : https://www.turbosquid.com/3d-models/free-brain-3d-model/1156531
  Format : OBJ  |  Licence : Free

  OU

  URL : https://sketchfab.com/3d-models/brain-3d-model-a7e2af13b9f04d8b9c50e9f8a3b7c6e5
  (chercher "brain anatomy OBJ free" sur Sketchfab, filtrer CC0/CC-BY)

══════════════════════════════════════════════════════
  OPTION 2 — NIH 3D Print Exchange (qualité médicale)
══════════════════════════════════════════════════════

  URL : https://3d.nih.gov/entries/3dpx-001517
  Télécharger le fichier OBJ ou STL
  (STL → convertir en OBJ avec Blender, gratuit)

══════════════════════════════════════════════════════
  OPTION 3 — Blender + neuroimaging (meilleure qualité)
══════════════════════════════════════════════════════

  1. Télécharger Blender (blender.org)
  2. Importer un fichier NIfTI (.nii) ou FreeSurfer (.pial)
     depuis : https://openneuro.org
  3. Exporter en OBJ → placer dans Assets/brain.obj

══════════════════════════════════════════════════════
  OPTION 4 — BrainVISA / MNI Atlas (open-source)
══════════════════════════════════════════════════════

  URL : https://brainvisa.info/web/
  Télécharger le template MNI152 et exporter en OBJ

══════════════════════════════════════════════════════
  PRÉPARATION DU FICHIER OBJ
══════════════════════════════════════════════════════

  Le chargeur supporte :
    - Triangles et quads (triangulés automatiquement)
    - Groupes (o / g) → chaque groupe = un lobe
    - Matériaux MTL (Kd = couleur diffuse)
    - Normales (vn) — si absentes, calculées automatiquement

  Si les lobes sont des groupes séparés, les nommer :
    frontal, parietal, temporal, occipital, cerebellum,
    brainstem → la heatmap colorera chaque lobe correctement.

  Taille recommandée : 50 000 – 500 000 polygones
  (fichier de quelques Mo, chargement en < 2 secondes)

══════════════════════════════════════════════════════
  SANS FICHIER OBJ
══════════════════════════════════════════════════════

  L'application fonctionne avec un cerveau procédural
  (hémisphères avec gyri mathématiques). Moins réaliste
  visuellement mais entièrement fonctionnel pour l'EEG.

═══════════════════════════════════════════════════════════════
