using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BioSynth
{
    /// <summary>
    /// Parser OBJ propre — vertices partagés, normales calculées par face
    /// si absentes, normalisation automatique du bounding box.
    /// </summary>
    public static class BrainObjLoader
    {
        public class LoadResult
        {
            public Model3DGroup? Model         { get; set; }
            public bool          Success       { get; set; }
            public string        Error         { get; set; } = "";
            public int           VertexCount   { get; set; }
            public int           TriangleCount { get; set; }
            public Rect3D        Bounds        { get; set; }
        }

        // Couleurs anatomiques par nom de groupe/matériau
        private static readonly (string key, Color col)[] AnatomyColors =
        {
            ("frontal",    Color.FromRgb(110, 185, 255)),
            ("parietal",   Color.FromRgb(160,  95, 255)),
            ("temporal",   Color.FromRgb(255, 160,  45)),
            ("occipital",  Color.FromRgb(255,  80,  80)),
            ("cerebellum", Color.FromRgb(155, 118, 138)),
            ("brainstem",  Color.FromRgb(138, 100, 115)),
            ("medulla",    Color.FromRgb(130,  95, 110)),
            ("corpus",     Color.FromRgb(228, 218, 195)),
            ("thalamus",   Color.FromRgb(175, 210, 150)),
            ("hippocampus",Color.FromRgb(220, 195, 115)),
        };

        // ─── Entrée principale ────────────────────────────────────────────────
        public static LoadResult Load(string objPath, Color? defaultColor = null, bool buildPerPartMaterials = false)
        {
            var result = new LoadResult();
            if (!File.Exists(objPath))
            {
                result.Error = $"Fichier introuvable : {objPath}";
                return result;
            }

            try
            {
                // 1. Lire les matériaux MTL
                var mtlColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
                string mtlPath = System.IO.Path.ChangeExtension(objPath, ".mtl");
                if (File.Exists(mtlPath)) ParseMtl(mtlPath, mtlColors);

                // 2. Lire tous les vertices globaux
                var gPos = new List<Point3D>();   // v
                var gNrm = new List<Vector3D>();  // vn
                // (vt ignoré — pas de texture)

                // 3. Groupes courants
                var group    = new Model3DGroup();
                string curMtl  = "";
                string curName = "default";

                // Buffer local pour le groupe courant
                // On utilise un dictionnaire (vi,ni) → index local pour partage de vertices
                var localPos = new List<Point3D>();
                var localNrm = new List<Vector3D>();
                var localIdx = new List<int>();
                var vertCache= new Dictionary<(int,int), int>();

                void FlushGroup()
                {
                    if (localIdx.Count == 0) { localPos.Clear(); localNrm.Clear(); vertCache.Clear(); return; }

                    var mesh = new MeshGeometry3D();
                    mesh.Positions       = new Point3DCollection(localPos);
                    mesh.TriangleIndices = new Int32Collection(localIdx);

                    // Normales : utiliser celles du fichier ou calculer par face
                    if (localNrm.Count == localPos.Count)
                    {
                        mesh.Normals = new Vector3DCollection(localNrm);
                    }
                    else
                    {
                        mesh.Normals = ComputeNormals(localPos, localIdx);
                    }

                    var mat = PickMaterial(curName, curMtl, mtlColors);
                    group.Children.Add(new GeometryModel3D
                    {
                        Geometry     = mesh,
                        Material     = mat,
                        BackMaterial = mat
                    });
                    result.VertexCount   += localPos.Count;
                    result.TriangleCount += localIdx.Count / 3;

                    localPos.Clear(); localNrm.Clear(); localIdx.Clear(); vertCache.Clear();
                }

                foreach (var rawLine in File.ReadLines(objPath))
                {
                    var line = rawLine.AsSpan().Trim();
                    if (line.IsEmpty || line[0] == '#') continue;

                    if (StartsWith(line, "v "))
                    {
                        var v = ParseVec3(line.Slice(2));
                        gPos.Add(new Point3D(v.X, v.Y, v.Z));
                    }
                    else if (StartsWith(line, "vn "))
                    {
                        var n = ParseVec3(line.Slice(3));
                        gNrm.Add(n);
                    }
                    else if (StartsWith(line, "usemtl "))
                    {
                        FlushGroup();
                        curMtl = line.Slice(7).Trim().ToString();
                    }
                    else if (StartsWith(line, "o ") || StartsWith(line, "g "))
                    {
                        FlushGroup();
                        curName = line.Slice(2).Trim().ToString();
                    }
                    else if (StartsWith(line, "f "))
                    {
                        var fverts = ParseFace(line.Slice(2).ToString());
                        if (fverts.Count < 3) continue;

                        // Fan triangulation — ajouter chaque sommet une seule fois (partage)
                        var localFace = new List<int>(fverts.Count);
                        foreach (var (vi, ni) in fverts)
                        {
                            int pi = vi > 0 ? vi - 1 : gPos.Count + vi;
                            int ni2= ni > 0 ? ni - 1 : -1;
                            if (pi < 0 || pi >= gPos.Count) continue;

                            var key = (pi, ni2);
                            if (!vertCache.TryGetValue(key, out int li))
                            {
                                li = localPos.Count;
                                localPos.Add(gPos[pi]);
                                if (ni2 >= 0 && ni2 < gNrm.Count)
                                    localNrm.Add(gNrm[ni2]);
                                vertCache[key] = li;
                            }
                            localFace.Add(li);
                        }

                        for (int k = 1; k < localFace.Count - 1; k++)
                        {
                            localIdx.Add(localFace[0]);
                            localIdx.Add(localFace[k]);
                            localIdx.Add(localFace[k + 1]);
                        }
                    }
                }
                FlushGroup();

                if (group.Children.Count == 0)
                {
                    result.Error = "Aucun mesh chargé — vérifier le fichier OBJ.";
                    return result;
                }

                // 4. Centrer + normaliser à [-1,1]
                var (center, scale) = ComputeNormalization(gPos);
                group.Transform = new Transform3DGroup
                {
                    Children = new Transform3DCollection
                    {
                        new TranslateTransform3D(-center.X, -center.Y, -center.Z),
                        new ScaleTransform3D(scale, scale, scale)
                    }
                };

                result.Model   = group;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = $"Erreur parsing OBJ : {ex.Message}";
            }

            return result;
        }

        // ─── Calcul des normales par face (Phong smooth) ──────────────────────
        private static Vector3DCollection ComputeNormals(List<Point3D> pos, List<int> idx)
        {
            var normals = new Vector3D[pos.Count];

            for (int i = 0; i < idx.Count; i += 3)
            {
                var a = pos[idx[i]];
                var b = pos[idx[i+1]];
                var c = pos[idx[i+2]];
                var ab = b - a;
                var ac = c - a;
                var n  = Vector3D.CrossProduct(ab, ac);
                // Accumulation (lissage Phong)
                normals[idx[i]]   += n;
                normals[idx[i+1]] += n;
                normals[idx[i+2]] += n;
            }

            var result = new Vector3DCollection(pos.Count);
            foreach (var n in normals)
            {
                var nn = n; if (nn.Length > 1e-8) nn.Normalize();
                result.Add(nn);
            }
            return result;
        }

        // ─── Normalisation bounding box ───────────────────────────────────────
        private static (Point3D center, double scale) ComputeNormalization(List<Point3D> pts)
        {
            if (pts.Count == 0) return (new Point3D(0,0,0), 1.0);

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }

            var center = new Point3D((minX+maxX)/2, (minY+maxY)/2, (minZ+maxZ)/2);
            double span = Math.Max(Math.Max(maxX-minX, maxY-minY), maxZ-minZ);
            double scale = span > 1e-8 ? 2.0 / span : 1.0;
            return (center, scale);
        }

        // ─── Parser MTL ───────────────────────────────────────────────────────
        private static void ParseMtl(string path, Dictionary<string, Color> colors)
        {
            string cur = "";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.AsSpan().Trim();
                if (StartsWith(line, "newmtl "))
                    cur = line.Slice(7).Trim().ToString();
                else if (StartsWith(line, "Kd ") && cur.Length > 0)
                {
                    var v = ParseVec3(line.Slice(3));
                    colors[cur] = Color.FromRgb(
                        (byte)Math.Clamp(v.X * 255, 0, 255),
                        (byte)Math.Clamp(v.Y * 255, 0, 255),
                        (byte)Math.Clamp(v.Z * 255, 0, 255));
                }
            }
        }

        // ─── Choix du matériau ────────────────────────────────────────────────
        private static Material PickMaterial(string name, string mtl,
                                             Dictionary<string, Color> mtlColors)
        {
            // 1. Couleur du fichier MTL si disponible
            if (mtlColors.TryGetValue(mtl, out var mc) && mc != default)
                return MakeMat(mc);

            // 2. Couleur anatomique par nom
            string search = (name + " " + mtl).ToLowerInvariant();
            foreach (var (key, col) in AnatomyColors)
                if (search.Contains(key)) return MakeMat(col);

            // 3. Couleur cerveau par défaut
            return MakeMat(Color.FromRgb(195, 148, 160));
        }

        private static Material MakeMat(Color c)
        {
            var mg = new MaterialGroup();
            mg.Children.Add(new DiffuseMaterial(new SolidColorBrush(c)));
            mg.Children.Add(new SpecularMaterial(
                new SolidColorBrush(Color.FromArgb(70, 255, 245, 240)), 35.0));
            return mg;
        }

        // ─── Parsing bas niveau ───────────────────────────────────────────────
        private static bool StartsWith(ReadOnlySpan<char> span, string prefix)
        {
            if (span.Length < prefix.Length) return false;
            return span.Slice(0, prefix.Length).SequenceEqual(prefix.AsSpan());
        }

        private static Vector3D ParseVec3(ReadOnlySpan<char> span)
        {
            Span<double> vals = stackalloc double[3];
            int idx = 0;
            int start = 0;
            bool inToken = false;

            for (int i = 0; i <= span.Length && idx < 3; i++)
            {
                bool isSpace = (i == span.Length) || span[i] == ' ' || span[i] == '\t';
                if (!isSpace && !inToken) { start = i; inToken = true; }
                else if (isSpace && inToken)
                {
                    inToken = false;
                    if (double.TryParse(span.Slice(start, i - start),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                        vals[idx++] = v;
                }
            }
            return new Vector3D(vals[0], vals[1], vals[2]);
        }

        private static List<(int vi, int ni)> ParseFace(string s)
        {
            var result = new List<(int, int)>(4);
            foreach (var tok in s.Split(new[] {' ','\t'}, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tok.Split('/');
                int.TryParse(parts[0], out int vi);
                int ni = 0;
                if (parts.Length >= 3) int.TryParse(parts[2], out ni);
                if (vi != 0) result.Add((vi, ni));
            }
            return result;
        }

        // ─── Lissage des normales ─────────────────────────────────────────
            public static Vector3DCollection SmoothNormals(List<Point3D> pos, List<int> idx)
        {
        var normals = new Vector3D[pos.Count];
        for (int i = 0; i < idx.Count - 2; i += 3)
        {
            if (i + 2 >= idx.Count) break;
            Point3D a = pos[idx[i]]; Point3D b = pos[idx[i+1]]; Point3D c = pos[idx[i+2]];
            var n = Vector3D.CrossProduct(b - a, c - a);
            normals[idx[i]] += n; normals[idx[i+1]] += n; normals[idx[i+2]] += n;
        }
        var result = new Vector3DCollection(pos.Count);
        foreach (var n in normals)
        {
            var nn = n;
            if (nn.LengthSquared > 1e-16) nn.Normalize();
            result.Add(nn);
        }
        return result;
    }
}
}
