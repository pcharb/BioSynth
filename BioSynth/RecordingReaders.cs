using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace BioSynth
{
    /// <summary>
    /// Enregistrement réel chargé en mémoire, indépendant du format d'origine.
    /// Produit par RecordingLoader à partir d'un CSV générique, d'un fichier Excel
    /// ou d'un jeu BrainVision (.vhdr + .vmrk + .eeg/.dat).
    ///
    /// Les temps sont en secondes, relatifs au premier échantillon.
    /// SampleRate est null quand les échantillons sont irréguliers (ex. eye-tracking
    /// horodaté) : la lecture se cale alors sur Times[] plutôt que sur une fréquence.
    /// </summary>
    public class Recording
    {
        public string     Name        { get; init; } = "recording";
        public string[]   ChannelNames{ get; init; } = Array.Empty<string>();
        public double?    SampleRate  { get; init; }
        public double[]   Times       { get; init; } = Array.Empty<double>();
        public double[][] Data        { get; init; } = Array.Empty<double[]>();
        public string     Unit        { get; init; } = "";
        public List<(double Time, string Label)> Markers { get; init; } = new();

        public int    ChannelCount => ChannelNames.Length;
        public int    SampleCount  => Times.Length;
        public double Duration     => SampleCount > 0 ? Times[^1] - Times[0] : 0.0;

        /// <summary>Fréquence effective pour les consommateurs qui en exigent une (LSL, affichage).</summary>
        public int EffectiveSampleRate
        {
            get
            {
                if (SampleRate is double fs && fs > 0) return (int)Math.Round(fs);
                if (SampleCount > 1 && Duration > 0) return Math.Max(1, (int)Math.Round((SampleCount - 1) / Duration));
                return 256;
            }
        }

        /// <summary>
        /// Réordonne les canaux selon <paramref name="targetChannels"/> (comparaison insensible à la casse).
        /// Un canal absent de cet enregistrement est ajouté et vaut 0 sur toute la durée ;
        /// les canaux absents de la cible sont retirés. Retourne l'objet tel quel si rien ne change.
        /// </summary>
        public Recording RemapChannels(string[] targetChannels)
        {
            if (ChannelNames.SequenceEqual(targetChannels, StringComparer.OrdinalIgnoreCase)) return this;

            var index = new int[targetChannels.Length];
            for (int c = 0; c < targetChannels.Length; c++)
                index[c] = Array.FindIndex(ChannelNames, n => string.Equals(n, targetChannels[c], StringComparison.OrdinalIgnoreCase));

            var data = new double[SampleCount][];
            for (int s = 0; s < SampleCount; s++)
            {
                var row = new double[targetChannels.Length];
                var src = Data[s];
                for (int c = 0; c < row.Length; c++)
                    row[c] = index[c] >= 0 && index[c] < src.Length ? src[index[c]] : 0.0;
                data[s] = row;
            }
            return new Recording
            {
                Name = Name, ChannelNames = (string[])targetChannels.Clone(), SampleRate = SampleRate,
                Times = Times, Data = data, Unit = Unit, Markers = Markers,
            };
        }

        public string Summary()
        {
            string fs = SampleRate is double f ? $"{f:F1} Hz" : $"irrégulier (~{EffectiveSampleRate} Hz)";
            return $"{Name} — {ChannelCount} canaux, {fs}, {SampleCount:N0} échantillons, {Duration:F1} s, {Markers.Count} marqueurs";
        }
    }

    /// <summary>
    /// Point d'entrée : détecte le format d'après l'extension et retourne un Recording.
    /// </summary>
    public static class RecordingLoader
    {
        /// <summary>Extensions prises en charge par ce chargeur (hors format BIN maison de BioSynth).</summary>
        public static bool IsSupported(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".vhdr" or ".xlsx" or ".xlsm" or ".csv" or ".tsv" or ".txt") return true;
            if (ext is ".dat" or ".eeg" or ".vmrk") return File.Exists(Path.ChangeExtension(path, ".vhdr"));
            return false;
        }

        /// <summary>
        /// Vrai si le CSV suit le format maison de BioSynth ("Timestamp_us, Ch1, ...", µs entiers),
        /// auquel cas EEGDataReplay conserve son parseur historique.
        /// </summary>
        public static bool IsLegacyBioSynthCsv(string path)
        {
            try
            {
                using var sr = new StreamReader(path, Encoding.UTF8);
                string? header = sr.ReadLine();
                if (header == null) return false;
                string h0 = header.Split(',')[0].Trim().ToLowerInvariant();
                // En-tête maison "Timestamp_us,Ch1,..." ou fichier sans en-tête (µs entiers en première colonne)
                return (h0.Contains("timestamp") && h0.Contains("us")) || long.TryParse(h0, out _);
            }
            catch { return false; }
        }

        public static Recording Open(string path, double fallbackSampleRate = 256)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".vhdr":
                    return BrainVisionReader.Read(path);
                case ".dat":
                case ".eeg":
                case ".vmrk":
                {
                    string vhdr = Path.ChangeExtension(path, ".vhdr");
                    if (!File.Exists(vhdr))
                        throw new FileNotFoundException($"En-tête BrainVision introuvable : {vhdr}");
                    return BrainVisionReader.Read(vhdr);
                }
                case ".xlsx":
                case ".xlsm":
                    return TabularReader.ReadExcel(path, fallbackSampleRate);
                case ".csv":
                case ".tsv":
                case ".txt":
                    return TabularReader.ReadCsv(path, fallbackSampleRate);
                default:
                    throw new NotSupportedException($"Format non pris en charge : {ext}");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // CSV générique et Excel
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lecteur de tables (CSV, Excel). Colonne de temps, colonne de marqueurs et
    /// canaux sont déduits des en-têtes ; tout est surchargeable via les propriétés
    /// statiques TimeColumn / MarkerColumn si l'heuristique se trompe.
    /// </summary>
    public static class TabularReader
    {
        static readonly string[] TimeNames   = { "time", "timestamp", "millisec", "ms", "sec", "seconds", "t" };
        static readonly string[] MarkerNames = { "marker", "event", "trigger", "label", "var8" };
        static readonly string[] SkipNames   = { "frame", "datapoint", "index", "samplinginterval" };

        /// <summary>Nom de colonne de temps forcé (null = détection automatique).</summary>
        public static string? TimeColumn   { get; set; }
        /// <summary>Nom de colonne de marqueurs forcé (null = détection automatique).</summary>
        public static string? MarkerColumn { get; set; }

        public static Recording ReadCsv(string path, double fallbackSampleRate = 256)
        {
            var lines = File.ReadLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count == 0) throw new InvalidDataException("Fichier vide");

            char sep = DetectSeparator(lines[0]);
            // Virgule décimale probable si le séparateur est ';' ou tab et que les données contiennent des virgules
            bool decimalComma = sep != ',' && lines.Skip(1).Take(20).Any(l => l.Contains(','));

            var headers = lines[0].Split(sep).Select(h => h.Trim().Trim('"')).ToArray();
            var rows = new List<string?[]>(lines.Count - 1);
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(sep).Select(c => (string?)c.Trim().Trim('"')).ToArray();
                if (decimalComma)
                    for (int i = 0; i < cells.Length; i++) cells[i] = cells[i]?.Replace(',', '.');
                rows.Add(cells);
            }
            return FromTable(Path.GetFileNameWithoutExtension(path), headers, rows, fallbackSampleRate);
        }

        public static Recording ReadExcel(string path, double fallbackSampleRate = 256, int sheetIndex = 1)
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet(sheetIndex);
            var used = ws.RangeUsed();
            if (used == null) throw new InvalidDataException("Feuille vide");

            int firstRow = used.FirstRow().RowNumber();
            int lastRow  = used.LastRow().RowNumber();
            int firstCol = used.FirstColumn().ColumnNumber();
            int lastCol  = used.LastColumn().ColumnNumber();
            int nCols    = lastCol - firstCol + 1;

            var headers = new string[nCols];
            for (int c = 0; c < nCols; c++)
                headers[c] = ws.Cell(firstRow, firstCol + c).GetString().Trim();

            var rows = new List<string?[]>(lastRow - firstRow);
            for (int r = firstRow + 1; r <= lastRow; r++)
            {
                var cells = new string?[nCols];
                bool any = false;
                for (int c = 0; c < nCols; c++)
                {
                    var cell = ws.Cell(r, firstCol + c);
                    if (cell.IsEmpty()) { cells[c] = null; continue; }
                    cells[c] = cell.DataType == XLDataType.Number
                        ? cell.GetDouble().ToString("R", CultureInfo.InvariantCulture)
                        : cell.GetString().Trim();
                    any = true;
                }
                if (any) rows.Add(cells);
            }
            return FromTable(Path.GetFileNameWithoutExtension(path), headers, rows, fallbackSampleRate);
        }

        // ────────────────────────────────────────────────────────────────

        static char DetectSeparator(string header)
        {
            var candidates = new[] { ',', ';', '\t' };
            return candidates.OrderByDescending(c => header.Count(ch => ch == c)).First();
        }

        static bool TryDouble(string? s, out double v)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        static int FindColumn(string[] headers, string? forced, string[] names, List<string?[]>? rowsForNonEmpty = null)
        {
            if (forced != null)
            {
                int idx = Array.FindIndex(headers, h => string.Equals(h, forced, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) return idx;
            }
            var lower = headers.Select(h => h.ToLowerInvariant()).ToArray();
            foreach (var n in names)
            {
                int idx = Array.IndexOf(lower, n);
                if (idx < 0) continue;
                if (rowsForNonEmpty == null || rowsForNonEmpty.Any(r => idx < r.Length && !string.IsNullOrEmpty(r[idx])))
                    return idx;
            }
            return -1;
        }

        internal static Recording FromTable(string name, string[] headers, List<string?[]> rows, double fallbackSampleRate)
        {
            int timeCol   = FindColumn(headers, TimeColumn, TimeNames);
            int markerCol = FindColumn(headers, MarkerColumn, MarkerNames, rows);

            // Unité de temps : ms si le nom l'indique, sinon secondes
            double scale = 1.0;
            if (timeCol >= 0)
            {
                string tn = headers[timeCol].ToLowerInvariant();
                if (tn.Contains("milli") || tn == "ms") scale = 1e-3;
                else if (tn.Contains("_us") || tn == "us") scale = 1e-6;
            }

            // Colonnes canaux : numériques dans plus de la moitié des lignes, hors temps/marqueurs/index
            var channelCols = new List<int>();
            for (int c = 0; c < headers.Length; c++)
            {
                if (c == timeCol || c == markerCol) continue;
                string h = headers[c].ToLowerInvariant();
                if (SkipNames.Contains(h) || TimeNames.Contains(h)) continue;
                int numeric = rows.Count(r => c < r.Length && TryDouble(r[c], out _));
                if (numeric > rows.Count / 2) channelCols.Add(c);
            }
            if (channelCols.Count == 0) throw new InvalidDataException("Aucune colonne numérique détectée");

            // Temps
            var times = new List<double>(rows.Count);
            var data  = new List<double[]>(rows.Count);
            var markers = new List<(double, string)>();
            double? fs = null;
            double t0 = double.NaN;

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                double t;
                if (timeCol >= 0)
                {
                    if (timeCol >= row.Length || !TryDouble(row[timeCol], out t)) continue;
                    t *= scale;
                    if (double.IsNaN(t0)) t0 = t;
                    t -= t0;
                }
                else
                {
                    t = times.Count / fallbackSampleRate;
                }

                var v = new double[channelCols.Count];
                for (int i = 0; i < channelCols.Count; i++)
                {
                    int c = channelCols[i];
                    v[i] = (c < row.Length && TryDouble(row[c], out double x)) ? x : double.NaN;
                }
                times.Add(t);
                data.Add(v);

                if (markerCol >= 0 && markerCol < row.Length && !string.IsNullOrWhiteSpace(row[markerCol]))
                    markers.Add((t, row[markerCol]!.Trim()));
            }

            if (timeCol < 0)
                fs = fallbackSampleRate;
            else if (times.Count > 2)
            {
                // Fréquence déclarée seulement si les pas sont quasi constants (écart-type < 5 % de la médiane)
                var d = new double[times.Count - 1];
                for (int i = 1; i < times.Count; i++) d[i - 1] = times[i] - times[i - 1];
                var pos = d.Where(x => x > 0).OrderBy(x => x).ToArray();
                if (pos.Length > 0)
                {
                    double median = pos[pos.Length / 2];
                    double mean = pos.Average();
                    double std = Math.Sqrt(pos.Sum(x => (x - mean) * (x - mean)) / pos.Length);
                    if (std / median < 0.05) fs = 1.0 / median;
                }
            }

            return new Recording
            {
                Name = name,
                ChannelNames = channelCols.Select(c => headers[c]).ToArray(),
                SampleRate = fs,
                Times = times.ToArray(),
                Data = data.ToArray(),
                Markers = markers,
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // BrainVision (.vhdr + .vmrk + .eeg/.dat)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lecteur BrainVision Core Data Format. Gère ASCII et BINARY (INT_16, INT_32,
    /// IEEE_FLOAT_32), orientation MULTIPLEXED ou VECTORIZED, résolution par canal,
    /// et les marqueurs du .vmrk s'il existe.
    /// </summary>
    public static class BrainVisionReader
    {
        public static Recording Read(string vhdrPath)
        {
            var ini    = ReadIni(vhdrPath);
            var common = ini["Common Infos"];
            string dir = Path.GetDirectoryName(vhdrPath) ?? ".";

            int    nCh  = int.Parse(common["NumberOfChannels"], CultureInfo.InvariantCulture);
            double fs   = 1e6 / double.Parse(common["SamplingInterval"], CultureInfo.InvariantCulture);
            string dataFile    = Path.Combine(dir, common["DataFile"]);
            string orientation = common.TryGetValue("DataOrientation", out var o) ? o.ToUpperInvariant() : "MULTIPLEXED";
            string format      = common.TryGetValue("DataFormat", out var f) ? f.ToUpperInvariant() : "BINARY";

            // Canaux : Ch<n>=<nom>,<réf>,<résolution>,<unité>
            var names = new string[nCh];
            var res   = new double[nCh];
            var units = new string[nCh];
            var chInfos = ini["Channel Infos"];
            for (int i = 0; i < nCh; i++)
            {
                var parts = chInfos[$"Ch{i + 1}"].Split(',');
                names[i] = parts[0].Replace("\\1", ",");
                res[i]   = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr) ? rr : 1.0;
                units[i] = parts.Length > 3 ? parts[3].Trim() : "";
            }

            double[][] data = format == "ASCII"
                ? ReadAscii(dataFile, nCh, orientation, ini.TryGetValue("ASCII Infos", out var ai) ? ai : new())
                : ReadBinary(dataFile, nCh, orientation, ini["Binary Infos"], res);

            var times = new double[data.Length];
            for (int i = 0; i < times.Length; i++) times[i] = i / fs;

            // Marqueurs
            var markers = new List<(double, string)>();
            string vmrk = common.TryGetValue("MarkerFile", out var mf)
                ? Path.Combine(dir, mf)
                : Path.ChangeExtension(vhdrPath, ".vmrk");
            if (File.Exists(vmrk))
            {
                var mi = ReadIni(vmrk);
                if (mi.TryGetValue("Marker Infos", out var mk))
                {
                    // Mk<n>=<type>,<description>,<position en points>,<taille>,<canal>
                    foreach (var kv in mk)
                    {
                        var p = kv.Value.Split(',');
                        if (p.Length >= 3 && long.TryParse(p[2].Trim(), out long pos))
                        {
                            string label = $"{p[0]}/{p[1]}".Trim('/').Replace("\\1", ",");
                            markers.Add(((pos - 1) / fs, label));
                        }
                    }
                }
            }

            string unit = units.Length > 0 && units.All(u => u == units[0]) ? units[0] : "";
            return new Recording
            {
                Name = Path.GetFileNameWithoutExtension(vhdrPath),
                ChannelNames = names,
                SampleRate = fs,
                Times = times,
                Data = data,
                Unit = unit,
                Markers = markers,
            };
        }

        static double[][] ReadAscii(string path, int nCh, string orientation, Dictionary<string, string> info)
        {
            int skipLines = info.TryGetValue("SkipLines", out var sl) ? int.Parse(sl) : 0;
            int skipCols  = info.TryGetValue("SkipColumns", out var sc) ? int.Parse(sc) : 0;
            char dec      = info.TryGetValue("DecimalSymbol", out var ds) && ds.Length > 0 ? ds[0] : '.';

            var rows = new List<double[]>();
            foreach (var line in File.ReadLines(path).Skip(skipLines))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var vals = new double[parts.Length - skipCols];
                for (int i = skipCols; i < parts.Length; i++)
                {
                    string s = dec == '.' ? parts[i] : parts[i].Replace(dec, '.');
                    vals[i - skipCols] = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                rows.Add(vals);
            }

            if (orientation == "VECTORIZED")
            {
                // Une ligne par canal : transposer vers (échantillons × canaux)
                int n = rows.Count > 0 ? rows[0].Length : 0;
                var data = new double[n][];
                for (int s = 0; s < n; s++)
                {
                    data[s] = new double[nCh];
                    for (int c = 0; c < nCh; c++) data[s][c] = rows[c][s];
                }
                return data;
            }
            return rows.ToArray();
        }

        static double[][] ReadBinary(string path, int nCh, string orientation, Dictionary<string, string> info, double[] res)
        {
            string bf = info.TryGetValue("BinaryFormat", out var b) ? b.ToUpperInvariant() : "INT_16";
            int size = bf switch { "INT_16" => 2, "INT_32" => 4, "IEEE_FLOAT_32" => 4, "IEEE_FLOAT_64" => 8, _ => throw new NotSupportedException(bf) };

            byte[] bytes = File.ReadAllBytes(path);
            long total = bytes.Length / size;
            int nSamples = (int)(total / nCh);
            var raw = new double[total];
            for (long i = 0; i < total; i++)
            {
                int off = (int)(i * size);
                raw[i] = bf switch
                {
                    "INT_16"        => BitConverter.ToInt16(bytes, off),
                    "INT_32"        => BitConverter.ToInt32(bytes, off),
                    "IEEE_FLOAT_32" => BitConverter.ToSingle(bytes, off),
                    _               => BitConverter.ToDouble(bytes, off),
                };
            }

            var data = new double[nSamples][];
            for (int s = 0; s < nSamples; s++)
            {
                data[s] = new double[nCh];
                for (int c = 0; c < nCh; c++)
                {
                    long idx = orientation == "VECTORIZED" ? (long)c * nSamples + s : (long)s * nCh + c;
                    data[s][c] = raw[idx] * res[c];
                }
            }
            return data;
        }

        /// <summary>Parseur INI minimal : sections [X], clés a=b, commentaires ';'. La clé conserve sa casse.</summary>
        internal static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? current = null;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("Brain Vision") || line.StartsWith("BrainVision")) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[line[1..^1]] = current;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && current != null)
                    current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return result;
        }
    }
}
