using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace BioSynth
{
    /// <summary>
    /// Lecteur minimal de classeurs Excel (.xlsx) sans dépendance externe.
    ///
    /// Un .xlsx est une archive ZIP de fichiers XML (Office Open XML). On lit :
    ///   - xl/workbook.xml + xl/_rels/workbook.xml.rels pour trouver la feuille demandée ;
    ///   - xl/sharedStrings.xml pour les chaînes partagées ;
    ///   - la feuille elle-même, cellule par cellule (référence A1, type, valeur).
    ///
    /// Remplace ClosedXML pour le replay : la bibliothèque n'est plus nécessaire à l'exécution,
    /// ce qui garde le moteur compilable et portable (Windows, Linux, macOS) avec le seul SDK .NET.
    /// Les nombres sont rendus en invariant "R" et les chaînes sont rognées, comme le faisait ReadExcel.
    /// </summary>
    public static class XlsxReader
    {
        private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string NsRel  = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string NsPkg  = "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>
        /// Lit la plage utilisée de la feuille <paramref name="sheetIndex"/> (1 = première feuille du classeur).
        /// Retourne la grille rectangulaire couvrant toutes les cellules non vides ; une cellule vide vaut null.
        /// </summary>
        public static List<string?[]> ReadUsedRange(string path, int sheetIndex = 1)
        {
            using var zip = ZipFile.OpenRead(path);
            var shared    = ReadSharedStrings(zip);
            string sheet  = ResolveSheetPath(zip, sheetIndex);
            var entry     = zip.GetEntry(sheet) ?? throw new InvalidDataException($"Feuille introuvable : {sheet}");

            // Lecture en flux : (ligne, colonne) → texte
            var cells = new Dictionary<(int r, int c), string>();
            int minR = int.MaxValue, maxR = -1, minC = int.MaxValue, maxC = -1;

            using (var s = entry.Open())
            using (var xr = XmlReader.Create(s))
            {
                int implicitRow = 0, implicitCol = 0;
                xr.MoveToContent();
                while (!xr.EOF)
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.NamespaceURI != NsMain) { xr.Read(); continue; }
                    if (xr.LocalName == "row")
                    {
                        implicitRow = int.TryParse(xr.GetAttribute("r"), out int rr) ? rr : implicitRow + 1;
                        implicitCol = 0;
                        xr.Read();
                        continue;
                    }
                    if (xr.LocalName != "c") { xr.Read(); continue; }

                    // Une cellule entière en XElement (ReadFrom avance le lecteur après l'élément)
                    var c = (XElement)XNode.ReadFrom(xr);
                    string? reference = (string?)c.Attribute("r");
                    string  type      = (string?)c.Attribute("t") ?? "n";
                    int row, col;
                    if (reference != null && TryParseRef(reference, out row, out col)) { implicitCol = col; }
                    else { row = implicitRow; col = ++implicitCol; }

                    string? value = CellValue(c, type, shared);
                    if (string.IsNullOrEmpty(value)) continue;

                    cells[(row, col)] = value;
                    if (row < minR) minR = row; if (row > maxR) maxR = row;
                    if (col < minC) minC = col; if (col > maxC) maxC = col;
                }
            }

            var grid = new List<string?[]>();
            if (maxR < 0) return grid;
            int nCols = maxC - minC + 1;
            for (int r = minR; r <= maxR; r++)
            {
                var line = new string?[nCols];
                for (int c = 0; c < nCols; c++)
                    line[c] = cells.TryGetValue((r, minC + c), out var v) ? v : null;
                grid.Add(line);
            }
            return grid;
        }

        // ── Valeur d'une cellule <c> ─────────────────────────────────────────
        private static readonly XNamespace X = NsMain;

        private static string? CellValue(XElement c, string type, List<string> shared)
        {
            string? v = (string?)c.Element(X + "v");
            switch (type)
            {
                case "s":
                    return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < shared.Count
                        ? shared[i].Trim() : null;
                case "inlineStr":
                    var isEl = c.Element(X + "is");
                    return isEl == null ? null : RichText(isEl).Trim();
                case "str":
                case "e":
                    return v?.Trim();
                case "b":
                    return v == "1" ? "TRUE" : v == "0" ? "FALSE" : v;
                default: // nombre (y compris dates stockées en numéro de série)
                    if (v == null) return null;
                    return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        ? d.ToString("R", CultureInfo.InvariantCulture)
                        : v.Trim();
            }
        }

        /// <summary>Concatène les &lt;t&gt; d'un &lt;si&gt; ou &lt;is&gt; (texte riche), sans les annotations phonétiques &lt;rPh&gt;.</summary>
        private static string RichText(XElement e)
            => string.Concat(e.Descendants(X + "t")
                              .Where(t => t.Ancestors(X + "rPh").FirstOrDefault() == null)
                              .Select(t => t.Value));

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list  = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return list;
            using var s  = entry.Open();
            using var xr = XmlReader.Create(s);
            xr.MoveToContent();
            while (!xr.EOF)
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si")
                    list.Add(RichText((XElement)XNode.ReadFrom(xr)));
                else xr.Read();
            }
            return list;
        }

        /// <summary>Chemin de la n-ième feuille dans l'ordre du classeur (et non l'ordre des fichiers dans l'archive).</summary>
        private static string ResolveSheetPath(ZipArchive zip, int sheetIndex)
        {
            var wbEntry = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Classeur Excel invalide (xl/workbook.xml absent).");
            var wb = new XmlDocument();
            using (var s = wbEntry.Open()) wb.Load(s);
            var ns = new XmlNamespaceManager(wb.NameTable);
            ns.AddNamespace("m", NsMain);
            var sheets = wb.SelectNodes("//m:sheets/m:sheet", ns);
            if (sheets == null || sheets.Count < sheetIndex || sheetIndex < 1)
                throw new InvalidDataException($"Le classeur ne contient pas de feuille n° {sheetIndex}.");
            string? rid = ((XmlElement)sheets[sheetIndex - 1]!).GetAttribute("id", NsRel);

            var relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relEntry != null && !string.IsNullOrEmpty(rid))
            {
                var rels = new XmlDocument();
                using (var s = relEntry.Open()) rels.Load(s);
                var rns = new XmlNamespaceManager(rels.NameTable);
                rns.AddNamespace("r", NsPkg);
                if (rels.SelectSingleNode($"//r:Relationship[@Id='{rid}']", rns) is XmlElement rel)
                {
                    string target = rel.GetAttribute("Target").Replace('\\', '/');
                    if (target.StartsWith("/")) return target.TrimStart('/');
                    // Cible relative à xl/ ; normaliser les éventuels "../"
                    var parts = new List<string> { "xl" };
                    foreach (var p in target.Split('/'))
                    {
                        if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                        else if (p != "." && p.Length > 0) parts.Add(p);
                    }
                    return string.Join("/", parts);
                }
            }
            // Repli : nommage conventionnel
            return $"xl/worksheets/sheet{sheetIndex}.xml";
        }

        /// <summary>"AB12" → (12, 28).</summary>
        private static bool TryParseRef(string reference, out int row, out int col)
        {
            row = col = 0;
            int i = 0;
            while (i < reference.Length && char.IsLetter(reference[i]))
            {
                col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
                i++;
            }
            return i > 0 && int.TryParse(reference.AsSpan(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out row);
        }
    }
}
