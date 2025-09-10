
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static B2IndexExtractor.MainWindow;

namespace B2IndexExtractor
{
    internal static class WemUtils
    {
        /// <summary>
        /// Checks if a file path contains "wwiseaudio" folder.
        /// </summary>
        public static bool IsInWwiseAudioFolder(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();

            // Check if the path contains "/wwiseaudio/" or "wwisetriton/"
            return normalizedPath.Contains("/wwiseaudio/") || normalizedPath.StartsWith("wwiseaudio/") || normalizedPath.Contains("/wwisetriton/") || normalizedPath.StartsWith("wwisetriton/");
        }
    }

    public sealed class ExtractOptions
    {
        public string OutputDirectory { get; set; } = "";
        public bool EnableHeaderPath { get; set; } = true;
        public bool EnableContentPath { get; set; } = true;
        public bool SkipWemFiles { get; set; } = false;
        public bool SkipBinkFiles { get; set; } = false;
        public bool SkipExistingFiles { get; set; } = true;
        public bool SkipResAndAce { get; set; } = false;
        public bool SkipConfigFiles { get; set; } = false;
        public bool OnlyAssets { get; set; } = false;
        public Action<double>? Progress { get; set; }
        public Action<string>? Logger { get; set; }
        public LogLevel LogLevel { get; set; } = LogLevel.Full;
    }

    internal static class B2Extractor
    {
        // Oodle fallback guard
        private static int _oodleFailCount = 0;
        private static bool _oodleDisabled = false;
        private static readonly HashSet<string> _existingNames = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _createdDirs = new(StringComparer.OrdinalIgnoreCase);

        // Cache of opened containers - it really makes entire process faster at the cost of the memory
        private static readonly Dictionary<string, FileStream> _containerCache = new(StringComparer.OrdinalIgnoreCase);

        // Unique paths
        private static readonly HashSet<string> _usedRelPaths = new(StringComparer.OrdinalIgnoreCase);

        // --- Helper class for QuickBMS Name search mode ---
        private sealed class NameEntry
        {
            public int FileNumber;      // FILE_NUMBER from name section records
            public long NameOffset;     // NAME_OFF
            public bool IsDirectory;    // (CHILD > 0)
            public string Name = "";    // read from NameOffset (C-string)
        }

        public static void ExtractAll(string indexPath, ExtractOptions options)
        {
            options.Logger?.Invoke($"Opening index: {indexPath}");
            if (options.SkipExistingFiles)
                BuildExistingNameIndex(options.OutputDirectory, options.Logger);

            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            long fileSize = fs.Length;

            try
            {
                // ---- header from .b2index ----
                fs.Seek(68, SeekOrigin.Begin);
                int entryOff = br.ReadInt32();
                int entryCountCandidate = ReadInt32Safe(br, 72);
                fs.Seek(92, SeekOrigin.Begin);
                int nameMapOff = br.ReadInt32();
                int nameCountCandidate = ReadInt32Safe(br, 96);

                options.Logger?.Invoke($"entryOff=0x{entryOff:X}, nameMapOff=0x{nameMapOff:X}");

                var quickList = ParseNameEntriesQuickBms(fs, br, nameMapOff, fileSize, options.Logger);
                var quickFiles = quickList.Where(ne => !ne.IsDirectory).ToList();

                _usedRelPaths.Clear();
                int processed = -1;
                int total = Math.Max(1, quickFiles.Count);

                foreach (var ne in quickFiles)
                {
                    processed++;
                    int index = ne.FileNumber;

                    // ---- Name & path ----
                    string name = ne.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"file_{index:00000000}.bin";

                    if (options.OnlyAssets)
                    {
                        bool isAsset = FileRouting.IsUbulk(name)
                            || name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith(".uasset2", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

                        if (!isAsset)
                        {
                            options.Logger?.Invoke($"⏭️ Skipping (Only Assets Mode): {name}");
                            continue;
                        }
                    }
                    else
                    {
                        // pre-skipy via existing name in output directory
                        if (options.SkipExistingFiles)
                        {
                            string fileNameOnly = Path.GetFileName(NormalizeRelPath(name));
                            if (_existingNames.Contains(fileNameOnly))
                            {
                                options.Logger?.Invoke($"⏭️ {fileNameOnly} already exists in output — Skip.");
                                continue;
                            }
                        }

                        if (options.SkipResAndAce &&
                            (name.EndsWith(".res", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(".ace", StringComparison.OrdinalIgnoreCase)))
                        {
                            options.Logger?.Invoke($"⏭️ Skipping RES/ACE File: {name}");
                            continue;
                        }

                        string extLower = Path.GetExtension(name).ToLowerInvariant();
                        if (options.SkipConfigFiles && FileRouting.ConfigExts.Contains(extLower))
                        {
                            options.Logger?.Invoke($"⏭️ Skipping Config File: {name}");
                            continue;
                        }

                        if (options.SkipBinkFiles &&
                            (name.EndsWith(".bik", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase)))
                        {
                            options.Logger?.Invoke($"⏭️ Skipping Bink file: {name}");
                            continue;
                        }
                    }

                    long fileOff = entryOff + (long)index * 16;
                    options.Progress?.Invoke(100.0 * processed / total);

                    if (fileOff < 0 || fileOff + 16 > fileSize)
                    {
                        options.Logger?.Invoke($"⏭️ Skipping entry #{index} (out of table range, off=0x{fileOff:X})");
                        continue;
                    }

                    fs.Seek(fileOff, SeekOrigin.Begin);
                    int blockOff = br.ReadInt32();
                    int blank = br.ReadInt32();
                    int absOff = br.ReadInt32();
                    int absSize = br.ReadInt32();

                    if (blockOff <= 0 || blockOff >= fileSize)
                    {
                        options.Logger?.Invoke($"⏭️ Skipping entry #{index} (blockOff=0x{blockOff:X})");
                        continue;
                    }

                    try
                    {
                        FileStream containerStream;
                        string containerPath = ResolveContainerPath(fs, br, indexPath, blockOff);
                        try { containerStream = GetContainer(containerPath, options); }
                        catch (Exception ex)
                        {
                            options.Logger?.Invoke($"❓ Missing/locked container: {containerPath} (#{index}) — {ex.Message}");
                            continue;
                        }
                        /*
                        if (!File.Exists(containerPath))
                        {
                            options.Logger?.Invoke($"❓ Missing Container: {containerPath} (entry #{index})");
                            processed++;
                            continue;
                        }*/
                        // read base block metadata
                        fs.Seek(blockOff + 16, SeekOrigin.Begin);
                        ulong offset = br.ReadUInt64();
                        int bid = br.ReadInt32();
                        ulong sizeOff = br.ReadUInt64();
                        int extraFileCountMinus1 = br.ReadInt32();
                        int extraCount = Math.Max(0, extraFileCountMinus1);

                        fs.Seek((long)sizeOff, SeekOrigin.Begin);
                        ulong baseUncSize = br.ReadUInt64();
                        int baseCSize = br.ReadInt32();

                        var chunks = new List<(ulong off, int csize, int unc)>
                        {
                            (offset, baseCSize, (int)baseUncSize)
                        };
                        int totalUnc = (int)baseUncSize;

                        for (int i = 0; i < extraCount; i++)
                        {
                            int eUncSize = br.ReadInt32();
                            int eStart = br.ReadInt32();
                            int eEnd = br.ReadInt32();
                            int eCSize = eEnd - eStart;
                            ulong eOffset = offset + (ulong)eStart;
                            chunks.Add((eOffset, eCSize, eUncSize));
                            if (eUncSize > 0 && eUncSize < int.MaxValue - totalUnc) totalUnc += eUncSize;
                        }

                        long needLen = Math.Max(0, (long)absOff + Math.Max(0, absSize));
                        if (totalUnc > 0 && needLen > totalUnc) needLen = totalUnc;
                        var full = AssembleWindow(containerStream, chunks, needLen, options);

                        if (absOff < 0 || absSize < 0 || absOff + absSize > full.Length)
                        {
                            options.Logger?.Invoke($"⏭️ Entry #{index}: Bad data range (absOff={absOff}, absSize={absSize}, full={full.Length})");
                            continue;
                        }

                        byte[] absData = new byte[Math.Max(0, absSize)];
                        if (absSize > 0) Buffer.BlockCopy(full, absOff, absData, 0, absSize);

                        string assetBase = Path.GetFileNameWithoutExtension(name);
                        string destRel = NormalizeRelPath(name);

                        string? headerPath = null;
                        string? headerKind = null;

                        bool isUassetLike =
                            name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".uasset2", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

                        if (options.EnableHeaderPath && isUassetLike)
                        {
                            TryBuildPathFromUAssetHeader(absData, out headerPath, out headerKind, assetBase);
                            if (!string.IsNullOrEmpty(headerPath))
                            {
                                var ext = Path.GetExtension(name);
                                destRel = NormalizeRelPath(headerPath + ext);
                                options.Logger?.Invoke($"📦 Path generated from Header ({headerKind ?? "Matching name"}): {destRel}");
                            }
                        }

                        if (headerPath == null && options.EnableContentPath && isUassetLike)
                        {
                            var guessed = TryGuessPathFromContent(absData, assetBase);
                            if (!string.IsNullOrEmpty(guessed))
                            {
                                var ext = Path.GetExtension(name);
                                destRel = NormalizeRelPath(guessed + ext);
                                options.Logger?.Invoke($"🧭 Path generated from analyzing content: {destRel}");
                            }
                        }

                        // Check for WWise audio folder AFTER the path ishas been determined
                        if (options.SkipWemFiles && WemUtils.IsInWwiseAudioFolder(destRel))
                        {
                            options.Logger?.Invoke($"⏭️ Skipping WWise Audio file: {destRel}");
                            processed++;
                            continue;
                        }

                        bool looksDir = LooksLikeDirectoryName(destRel);

                        if (looksDir || absSize == 0)
                        {
                            var dirPath = Path.Combine(options.OutputDirectory, destRel)
                                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            Directory.CreateDirectory(dirPath);
                            options.Logger?.Invoke($"📁 {destRel.TrimEnd('/', '\\')} (Directory)");
                        }
                        else
                        {
                            // routing
                            string fileNameOnly = Path.GetFileName(destRel);
                            string? suggestedSubdir = Path.GetDirectoryName(destRel)?.Replace('\\', '/');

                            bool isMaterialUasset = LooksLikeMaterialUasset(fileNameOnly, destRel, headerKind);

                            string? outPath = FileRouting.ResolveOutputPath(
                                options.OutputDirectory,
                                suggestedSubdir,
                                fileNameOnly,
                                isMaterialUasset
                            );

                            if (outPath == null)
                            {
                                options.Logger?.Invoke($"⏭️ Skipping (missing extension - probably a directory): {fileNameOnly}");
                                continue;
                            }

                            string dir = Path.GetDirectoryName(outPath)!;
                            if (_createdDirs.Add(dir))
                                Directory.CreateDirectory(dir);

                            string relKey = Path.GetRelativePath(options.OutputDirectory, outPath);
                            outPath = EnsureUniqueFast(outPath, relKey);

                            File.WriteAllBytes(outPath, absData);
                            options.Logger?.Invoke($"✔️ {Path.GetRelativePath(options.OutputDirectory, outPath)} ({absSize} B)");
                        }
                    }
                    catch (Exception ex)
                    {
                        options.Logger?.Invoke($"⚠️ Bad Entry #{index}: {ex.Message}");
                    }

                    options.Progress?.Invoke(100.0 * processed / total);
                }

                // After QuickBMS — reconcile ubulks and exit
                try { FileRouting.ReconcileOrphanUbulks(options.OutputDirectory, options.Logger); } catch { }
            }
            finally
            {
                // Zawsze zamknij wszystkie kontenery
                CloseAllContainers();
            }
        }

        /// <summary>
        /// parses name section like quickbms: record 16-byte (u64 nameOff, i32 fileNo, i32 child).
        /// If child > 0 → treat as directory and skip.
        /// return NameEntry list, with 'Name' already parsed (C-string spod nameOff).
        /// </summary>
        private static List<NameEntry> ParseNameEntriesQuickBms(
            FileStream fs, BinaryReader br,
            long namesSectionOff, long fileSize,
            Action<string>? logger)
        {
            var list = new List<NameEntry>();
            if (namesSectionOff <= 0 || namesSectionOff >= fileSize) return list;

            long pos = namesSectionOff;
            int safetyBad = 0;
            const int MAX_BAD = 4096;

            while (pos + 16 <= fileSize)
            {
                fs.Seek(pos, SeekOrigin.Begin);

                ulong nameOff = br.ReadUInt64();       // NAME_OFF
                int fileNo = br.ReadInt32();           // FILE_NUMBER
                int child = br.ReadInt32();            // CHILD (signed)

                bool looksValid = (nameOff > 0 && (long)nameOff < fileSize && fileNo >= 0);
                if (!looksValid)
                {
                    safetyBad++;
                    if (safetyBad > MAX_BAD) break;
                    pos += 16;
                    continue;
                }

                string name = TryReadCString(fs, (long)nameOff);
                if (string.IsNullOrEmpty(name))
                {
                    safetyBad++;
                    if (safetyBad > MAX_BAD) break;
                    pos += 16;
                    continue;
                }

                safetyBad = 0; // looks like a record

                list.Add(new NameEntry
                {
                    FileNumber = fileNo,
                    NameOffset = (long)nameOff,
                    IsDirectory = (child > 0),
                    Name = name
                });

                pos += 16;
            }

            logger?.Invoke($"Name map (quickbms-style): {list.Count} records, directories: {list.Count(x => x.IsDirectory)}.");
            return list;
        }

        /// Joins decompressed data into one buffer, without O(n^2) times of copying.
        /// copies only the size of 'needLen' (typowo absOff+absSize), so you dont need to allocate too much.
        private static byte[] AssembleWindow(FileStream cfs, List<(ulong off, int csize, int unc)> chunks, long needLen, ExtractOptions options)
        {
            if (needLen < 0) needLen = 0;
            var outBuf = new byte[needLen];
            long cursor = 0;
            foreach (var ch in chunks)
            {
                var part = ExtractFromContainer(cfs, ch.off, (ulong)ch.csize, (ulong)ch.unc, options);
                int copyLen = part.Length;
                if (cursor >= needLen) break;
                if (cursor + copyLen > needLen) copyLen = (int)(needLen - cursor);
                if (copyLen > 0)
                {
                    Buffer.BlockCopy(part, 0, outBuf, (int)cursor, copyLen);
                    cursor += copyLen;
                }
                else
                {
                    cursor += part.Length;
                }
            }
            return outBuf;
        }

        // ---- Heuristics: detecting material (.uasset/.uasset2) ----
        private static bool LooksLikeMaterialUasset(string fileName, string destRel, string? headerKind)
        {
            bool isUasset = fileName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".uasset2", StringComparison.OrdinalIgnoreCase);
            if (!isUasset) return false;

            if (string.Equals(headerKind, "material", StringComparison.OrdinalIgnoreCase))
                return true;

            string rel = (destRel ?? "").Replace('\\', '/').ToLowerInvariant();
            if (rel.Contains("/material") || rel.Contains("/materials"))
                return true;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string up = (stem ?? "").ToUpperInvariant();
            if (up.StartsWith("M_") || up.StartsWith("MI_") || up.StartsWith("MIC_") || up.StartsWith("MF_"))
                return true;

            return false;
        }

        // ---- HEADER path building (generic separators) ----
        private static bool TryBuildPathFromUAssetHeader(byte[] data, out string? fullPath, out string? kind, string assetBase)
        {
            fullPath = null; kind = null;
            try
            {
                var r = new Buf(data);
                int fileTag = r.ReadInt();
                int fileVersion = r.ReadInt();
                bool isUE4 = fileVersion < 0;
                int ue4Legacy = fileVersion;
                if (isUE4 && ue4Legacy != -4) { fileVersion = r.ReadInt(); }
                int fv = fileVersion;
                short ver = (short)(fv & 0xFFFF);
                short lic = (short)((fv >> 16) & 0xFFFF);
                int ue4Ver = r.ReadInt();
                int ue4Lic = r.ReadInt();

                // FIX: handle custom versions for Gears 5 and Tactics
                if (ue4Ver == 502 && ue4Lic == 67)
                {
                    int customCount = r.ReadInt();
                    for (int i = 0; i < customCount; i++)
                    {
                        _ = r.ReadInt(); // guid A
                        _ = r.ReadInt(); // guid B
                        _ = r.ReadInt(); // guid C
                        _ = r.ReadInt(); // guid D
                        _ = r.ReadInt(); // version
                    }
                }

                int totalHeaderSize = r.ReadInt();

                string folderName = r.ReadFString();
                if (!string.IsNullOrEmpty(folderName))
                {
                    var folder = folderName.TrimEnd('\0').Trim().Replace('\\', '/');
                    if (folder.Length > 0 && folder != "/" && folder != "\\")
                    {
                        fullPath = Path.Combine(folder, assetBase);
                    }
                }

                uint pkgFlags = r.ReadUInt();

                // Name table (count + offset, order may vary)
                int a = r.ReadInt();
                int b = r.ReadInt();
                int nameCount, nameOff;
                if (a > 0 && b > 0)
                {
                    nameCount = a;
                    nameOff = b;
                }
                else
                {
                    nameCount = b; nameOff = a;
                }

                if (ue4Ver > 459 && ue4Ver != 499) { _ = r.ReadFString(); } // LocalizationID
                if (ue4Ver > 459) { r.Skip(8); } // GatherableTextDataCount/Offset

                if (ue4Ver == 502 && ue4Lic == 67) { r.Skip(4); } // special skip

                int exportCount = r.ReadInt();
                int exportOff = r.ReadInt();
                int importCount = r.ReadInt();
                int importOff = r.ReadInt();

                // Scan NameTable for ANY path-like strings (not limited to Game/Engine)
                if (nameOff > 0 && nameOff < data.Length)
                {
                    var names = ScanNameTableStrings(data, nameOff, nameCount);
                    var candidates = names
                        .Select(NormalizePathLike)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Cast<string>()
                        .ToList();
                    string cls = ClassifyFromNames(names);
                    kind = cls;

                    var best = PickBestPathCandidate(candidates, assetBase, cls);
                    if (!string.IsNullOrEmpty(best)) { fullPath = best; return true; }
                }

                return fullPath != null;
            }
            catch { return false; }
        }

        private static string ClassifyFromNames(IEnumerable<string> names)
        {
            string s = string.Join(";", names).ToLowerInvariant();
            if (s.Contains("materialexpression") || s.Contains("texture2d") || s.Contains("shader") || s.Contains("material"))
                return "material";
            if (s.Contains("agggeom") || s.Contains("staticmesh") || s.Contains("skeletalmesh"))
                return "mesh";
            return "unknown";
        }

        private static IEnumerable<string> ScanNameTableStrings(byte[] data, int nameOff, int nameCount)
        {
            var list = new List<string>();
            int pos = nameOff;
            for (int i = 0; i < Math.Max(0, nameCount) && pos >= 0 && pos < data.Length - 4; i++)
            {
                int len = BitConverter.ToInt32(data, pos); pos += 4;
                if (len == 0) { list.Add(""); continue; }
                bool isUnicode = len < 0; int count = Math.Abs(len);
                int bytes = isUnicode ? count * 2 : count;
                if (pos + bytes > data.Length) break;
                string s = isUnicode ? Encoding.Unicode.GetString(data, pos, bytes) : Encoding.UTF8.GetString(data, pos, bytes);
                pos += bytes;
                list.Add(s.TrimEnd('\0'));
                if (pos + 4 <= data.Length) pos += 4; // extra field
            }
            return list;
        }

        private static string? NormalizePathLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace('\\', '/');
            if (!s.Contains('/')) return null;
            int dot = s.LastIndexOf('.');
            if (dot > 0)
            {
                var last = s.Substring(s.LastIndexOf('/') + 1);
                var afterDot = s.Substring(dot + 1);
                if (string.Equals(last, afterDot, StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(0, dot);
            }
            while (s.StartsWith("//")) s = s.Substring(1);
            return s.Trim();
        }

        private static string PickBestPathCandidate(IEnumerable<string> candidates, string assetBase, string cls)
        {
            int Score(string p)
            {
                int score = 0;
                var last = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
                if (string.Equals(last, assetBase, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (p.StartsWith("/")) score += 3;
                if (p.Contains("/Game/") || p.Contains("/Engine/")) score += 2;
                if (cls == "material" && p.ToLowerInvariant().Contains("material")) score += 2;
                if (cls == "mesh" && (p.ToLowerInvariant().Contains("mesh") || p.ToLowerInvariant().Contains("agggeom"))) score += 2;
                score += Math.Min(10, p.Count(c => c == '/'));
                score += Math.Min(10, p.Length);
                return score;
            }
            string best = candidates.OrderByDescending(Score).FirstOrDefault() ?? "";
            return best;
        }

        // ---- Content bytes heuristic: ANY path-like token ----
        private static string? TryGuessPathFromContent(byte[] data, string assetBaseName)
        {
            var paths = new List<string>();
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'/' && data[i] != (byte)'\\') continue;
                int start = i;
                int j = i + 1;
                int segments = 0;
                while (j < data.Length)
                {
                    byte b = data[j];
                    if (b == 0 || b < 0x20 || b == (byte)'\"' || b == (byte)'\'' || b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') break;
                    if (b == (byte)'/' || b == (byte)'\\') segments++;
                    j++;
                    if (j - i > 512) break;
                }
                if (segments >= 1 && j - start > 2)
                {
                    string s = Encoding.ASCII.GetString(data, start, j - start).Replace('\\', '/');
                    var norm = NormalizePathLike(s);
                    if (!string.IsNullOrEmpty(norm)) paths.Add(norm);
                }
                i = j;
            }
            if (paths.Count == 0) return null;
            string best = paths.OrderByDescending(p => (p.EndsWith("/" + assetBaseName, StringComparison.OrdinalIgnoreCase) ? 10 : 0) + p.Count(c => c == '/')).First();
            return best;
        }

        // Heuristics: does buffer looks like decompressed
        private static bool LikelyDecompressed(byte[] raw)
        {
            if (raw.Length == 0) return false;
            int nonZero = 0;
            int[] hist = new int[256];
            int step = Math.Max(1, raw.Length / 1024);
            for (int i = 0; i < raw.Length; i += step)
            {
                byte b = raw[i];
                hist[b]++;
                if (b != 0) nonZero++;
            }
            int unique = 0;
            for (int i = 0; i < 256; i++) if (hist[i] > 0) unique++;
            return nonZero > 0 && unique > 8;
        }

        // ---- Utilities ----
        private sealed class Buf
        {
            private readonly byte[] _d; private int _p;
            public Buf(byte[] d) { _d = d; _p = 0; }
            public void Skip(int n) { _p = Math.Min(_d.Length, _p + n); }
            public int ReadInt() { if (_p + 4 > _d.Length) throw new EndOfStreamException(); int v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
            public uint ReadUInt() { if (_p + 4 > _d.Length) throw new EndOfStreamException(); uint v = BitConverter.ToUInt32(_d, _p); _p += 4; return v; }
            public string ReadFString()
            {
                if (_p + 4 > _d.Length) return "";
                int len = BitConverter.ToInt32(_d, _p); _p += 4;
                if (len == 0) return "";
                bool isUnicode = len < 0; int count = Math.Abs(len);
                int bytes = isUnicode ? count * 2 : count;
                if (_p + bytes > _d.Length) { _p = _d.Length; return ""; }
                string s = isUnicode ? Encoding.Unicode.GetString(_d, _p, bytes) : Encoding.UTF8.GetString(_d, _p, bytes);
                _p += bytes;
                return s.TrimEnd('\0');
            }
        }

        private static Dictionary<int, long> ParseNameMap(FileStream fs, BinaryReader br, int nameMapOff, int nameCountCandidate, long fileSize, ExtractOptions options)
        {
            var map = new Dictionary<int, long>();
            if (nameMapOff <= 0 || nameMapOff >= fileSize) return map;

            bool usedCount = false;
            if (nameCountCandidate > 0 && nameMapOff + (long)nameCountCandidate * 12 <= fileSize)
            {
                usedCount = true;
                fs.Seek(nameMapOff, SeekOrigin.Begin);
                for (int i = 0; i < nameCountCandidate; i++)
                {
                    long nameOff = (long)br.ReadUInt64();
                    int idx = br.ReadInt32();
                    if (nameOff > 0 && nameOff < fileSize && idx >= 0 && !map.ContainsKey(idx))
                    {
                        string s = TryReadCString(fs, nameOff);
                        if (!string.IsNullOrEmpty(s)) map[idx] = nameOff;
                    }
                }
                if (map.Count == 0)
                {
                    options.Logger?.Invoke("Bad use of nameCountCandidate — switching to Heuristics.");
                    usedCount = false;
                }
            }

            if (!usedCount)
            {
                fs.Seek(nameMapOff, SeekOrigin.Begin);
                long pos = nameMapOff;
                int safety = 0;
                while (pos + 12 <= fileSize && safety < 2_000_000)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    long nameOff = (long)br.ReadUInt64();
                    int idx = br.ReadInt32();
                    if (nameOff <= 0 || nameOff >= fileSize || idx < 0)
                    {
                        safety++;
                        pos += 12;
                        continue;
                    }
                    string s = TryReadCString(fs, nameOff);
                    if (!string.IsNullOrEmpty(s) && !map.ContainsKey(idx))
                    {
                        map[idx] = nameOff;
                        safety = 0;
                    }
                    else safety++;
                    pos += 12;
                    if (map.Count > 0 && safety > 4096) break;
                }
            }
            options.Logger?.Invoke($"Name Map: {map.Count} positions.");
            return map;
        }

        private static string ResolveContainerPath(FileStream fs, BinaryReader br, string indexPath, int blockOff)
        {
            fs.Seek(blockOff, SeekOrigin.Begin);
            ulong archiveSpecs = br.ReadUInt64();
            fs.Seek((long)archiveSpecs, SeekOrigin.Begin);
            int archiveOff = br.ReadInt32();
            fs.Seek(archiveOff, SeekOrigin.Begin);
            string archName = ReadCString(br);
            if (!archName.EndsWith(".b2container", StringComparison.OrdinalIgnoreCase))
                archName += ".b2container";
            string baseDir = Path.GetDirectoryName(indexPath)!;
            return Path.Combine(baseDir, archName);
        }

        // Nowa wersja – przyjmuje gotowy stream z cache
        private static byte[] ExtractFromContainer(FileStream cfs, ulong offset, ulong compSize, ulong uncSize, ExtractOptions options)
        {
            if ((long)offset < 0 || (long)offset >= cfs.Length) throw new InvalidDataException("Offset outside of the container");
            cfs.Seek((long)offset, SeekOrigin.Begin);
            if ((long)compSize < 0 || (long)compSize > (cfs.Length - (long)offset)) throw new InvalidDataException("End of file reached, data is too big.");

            var comp = ArrayPool<byte>.Shared.Rent((int)compSize);
            try
            {
                int read = cfs.Read(comp, 0, (int)compSize);
                if (read != (int)compSize) throw new EndOfStreamException();

                if (compSize == uncSize)
                {
                    var same = new byte[(int)uncSize];
                    Buffer.BlockCopy(comp, 0, same, 0, (int)uncSize);
                    return same;
                }

                var raw = new byte[(int)uncSize];
                try
                {
                    if (!_oodleDisabled)
                    {
                        int ret = NativeMethods.OodleLZ_Decompress(comp, (long)compSize, raw, (long)uncSize, 1, 0, 0,
                            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);

                        if (ret > 0 || (ret == 0 && LikelyDecompressed(raw)))
                        {
                            _oodleFailCount = 0;
                            return raw;
                        }

                        _oodleFailCount++;
                        if (_oodleFailCount >= 999999999)
                        {
                            _oodleDisabled = true;
                            options.Logger?.Invoke("⛔ turn off Oodle (999999999 failed attempts). Extracting compressed data.");
                        }
                        else
                        {
                            options.Logger?.Invoke("⚠️ Oodle returned error — Compressed data saved.");
                        }
                    }
                    var compCopy = new byte[(int)compSize];
                    Buffer.BlockCopy(comp, 0, compCopy, 0, (int)compSize);
                    return compCopy;

                }
                catch (DllNotFoundException)
                {
                    options.Logger?.Invoke("⚠️ Oodle DLL not loaded — saving compressed data.");
                    var compCopy = new byte[(int)compSize];
                    Buffer.BlockCopy(comp, 0, compCopy, 0, (int)compSize);
                    return compCopy;
                }
                catch (EntryPointNotFoundException)
                {
                    options.Logger?.Invoke("⚠️ Could not find entry point for oodle. Saving compressed data");
                    var compCopy = new byte[(int)compSize];
                    Buffer.BlockCopy(comp, 0, compCopy, 0, (int)compSize);
                    return compCopy;
                }
                catch (Exception ex)
                {
                    options.Logger?.Invoke("⚠️ Oodle decompression failed: " + ex.Message + " — saving compressed data.");
                    var compCopy = new byte[(int)compSize];
                    Buffer.BlockCopy(comp, 0, compCopy, 0, (int)compSize);
                    return compCopy;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(comp);
            }
        }

        private static string TryReadCString(FileStream fs, long offset)
        {
            try { return ReadCString(fs, offset); } catch { return ""; }
        }
        private static string ReadCString(BinaryReader br)
        {
            var bytes = new List<byte>();
            byte b; while ((b = br.ReadByte()) != 0) bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        private static string ReadCString(FileStream fs, long offset)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            int b; var bytes = new List<byte>();
            while ((b = fs.ReadByte()) > 0) bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static int ReadInt32Safe(BinaryReader br, long absoluteOffset)
        {
            long cur = br.BaseStream.Position;
            try { br.BaseStream.Seek(absoluteOffset, SeekOrigin.Begin); return br.ReadInt32(); }
            catch { return -1; }
            finally { br.BaseStream.Seek(cur, SeekOrigin.Begin); }
        }

        // quick, memory based method for unique names
        private static string EnsureUniqueFast(string desiredAbsPath, string relKey)
        {
            string dir = Path.GetDirectoryName(desiredAbsPath)!;
            string name = Path.GetFileNameWithoutExtension(desiredAbsPath);
            string ext = Path.GetExtension(desiredAbsPath);

            if (_usedRelPaths.Add(relKey) && !File.Exists(desiredAbsPath))
                return desiredAbsPath;

            int i = 1;
            while (true)
            {
                string rel = $"{name}_{i}{ext}";
                if (_usedRelPaths.Add(rel))
                {
                    string abs = Path.Combine(dir, rel);
                    if (!File.Exists(abs)) return abs;
                }
                i++;
            }
        }

        private static string NormalizeRelPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "unnamed.bin";
            var s = input.Replace('\\', '/');
            while (s.StartsWith("/")) s = s.Substring(1);
            int colon = s.IndexOf(':');
            if (colon >= 0) { s = s.Substring(colon + 1); if (s.StartsWith("/")) s = s.Substring(1); }
            var invalid = Path.GetInvalidFileNameChars();
            var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                foreach (var ch in invalid) p = p.Replace(ch.ToString(), "_");
                var u = p.ToUpperInvariant();
                if (u == "CON" || u == "PRN" || u == "AUX" || u == "NUL" ||
                    u == "COM1" || u == "COM2" || u == "COM3" || u == "COM4" || u == "COM5" || u == "COM6" || u == "COM7" || u == "COM8" || u == "COM9" ||
                    u == "LPT1" || u == "LPT2" || u == "LPT3" || u == "LPT4" || u == "LPT5" || u == "LPT6" || u == "LPT7" || u == "LPT8" || u == "LPT9")
                    p = "_" + p;
                parts[i] = p;
            }
            var joined = string.Join("/", parts);
            if (string.IsNullOrEmpty(joined)) joined = "unnamed.bin";
            return joined;
        }

        private static bool LooksLikeDirectoryName(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return true;
            if (rel.EndsWith("/") || rel.EndsWith("\\")) return true;
            var trimmed = rel.TrimEnd('/', '\\');
            var fileName = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(fileName)) return true;
            return false;
        }

        // --- Cache helpers ---
        private static FileStream GetContainer(string containerPath, ExtractOptions options)
        {
            if (_containerCache.TryGetValue(containerPath, out var fs) && fs.CanRead)
                return fs;

            var stream = new FileStream(
                containerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1 << 16,
                options: FileOptions.RandomAccess);

            _containerCache[containerPath] = stream;
            options.Logger?.Invoke($"🗃️ Opening container: {containerPath}");
            return stream;
        }

        private static void CloseAllContainers()
        {
            foreach (var kv in _containerCache)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _containerCache.Clear();
        }

        private static void BuildExistingNameIndex(string outputDir, Action<string>? log)
        {
            _existingNames.Clear();
            if (!Directory.Exists(outputDir)) return;

            int cnt = 0;
            foreach (var abs in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(abs);
                if (string.IsNullOrEmpty(name)) continue;
                _existingNames.Add(name);
                cnt++;
            }
            log?.Invoke($"📝 Loaded existing file index: {_existingNames.Count} unique names ({cnt} files).");
        }
    }
}
