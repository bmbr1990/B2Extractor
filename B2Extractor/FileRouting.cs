using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace B2IndexExtractor
{
    /// <summary>
    /// Centralized routing logic for extracted files.
    /// Rules:
    /// - *.ubulk, *.ubulk0, *.ubulk1, ... → place next to remembered Material *.uasset with the same stem.
    /// - *.json, *.ini, *.cfg, *.xml, *.toml, *.yaml, *.yml, *.properties, *.conf → OutputRoot/Configs/
    /// - files with NO extension → SKIP (return null).
    /// - everything else → OutputRoot/misc/(optional relative subpath)
    /// 
    /// Use RememberMaterialLocation() after saving a Material *.uasset (or pass isMaterialUAsset=true to ResolveOutputPath).
    /// </summary>
    internal static class FileRouting
    {
        private static readonly Regex UbulkRegex = new Regex(@"\.ubulk(\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TrailingIndex = new Regex(@"([_\-\.](lod)?\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static readonly HashSet<string> ConfigExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ini", ".json", ".cfg", ".xml", ".toml", ".yaml", ".yml", ".properties", ".conf"
        };

        /// <summary>
        /// Map: asset stem (filename without extension) → absolute directory where the material *.uasset was written.
        /// </summary>
        private static readonly Dictionary<string, string> MaterialDirsByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> AnyAssetDirsByStem = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsUbulk(string nameOrExt)
        {
            if (string.IsNullOrEmpty(nameOrExt)) return false;
            if (!nameOrExt.StartsWith(".")) // full file name
                return UbulkRegex.IsMatch(nameOrExt);
            // extension case: treat ".ubulk" and ".ubulk<number>" as ubulk
            return nameOrExt.Equals(".ubulk", StringComparison.OrdinalIgnoreCase) || UbulkRegex.IsMatch(nameOrExt);
        }

        public static void RememberMaterialLocation(string uassetFileName, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(uassetFileName) || string.IsNullOrWhiteSpace(targetDirectory)) return;
            string stem = Path.GetFileNameWithoutExtension(uassetFileName);
            if (string.IsNullOrEmpty(stem)) return;
            MaterialDirsByStem[stem] = targetDirectory;
        }

        /// <summary>
        /// Compute final output path for a given source file name.
        /// </summary>
        /// <param name="projectRoot">Absolute output root.</param>
        /// <param name="relativeSuggestedPath">Optional relative subfolder suggestion (e.g., 'Game/Materials'); can be null/empty.</param>
        /// <param name="sourceName">Original filename (no directories), e.g., 'Foo.uasset', 'Foo.ubulk0', 'Bar.json'.</param>
        /// <param name="isMaterialUAsset">True when this .uasset is a Material/MIC (so we remember its folder).</param>
        /// <returns>Absolute destination file path; or null to skip (no extension).</returns>
        public static string? ResolveOutputPath(
            string projectRoot,
            string? relativeSuggestedPath,
            string sourceName,
            bool isMaterialUAsset)
        {
            if (string.IsNullOrWhiteSpace(sourceName)) return null;

            string ext = Path.GetExtension(sourceName);

            // Skip files with NO extension
            if (string.IsNullOrEmpty(ext)) return null;

            // Normalize root
            projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? Directory.GetCurrentDirectory() : projectRoot;

            // Config files
            if (ConfigExts.Contains(ext))
            {
                string dir = Path.Combine(projectRoot, "Configs");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, Path.GetFileName(sourceName));
            }

            // UBULK family → try place next to remembered material by stem
            if (IsUbulk(sourceName) || IsUbulk(ext))
            {
                string stem = Path.GetFileNameWithoutExtension(sourceName);
                if (!string.IsNullOrEmpty(stem))
                {
                    if (MaterialDirsByStem.TryGetValue(stem, out var matDir))
                    {
                        Directory.CreateDirectory(matDir);
                        return Path.Combine(matDir, Path.GetFileName(sourceName));
                    }
                }
                // Fallback → misc/
                string fallback = Path.Combine(projectRoot, "_ubulks");
                Directory.CreateDirectory(fallback);
                return Path.Combine(fallback, Path.GetFileName(sourceName));
            }

            // Material .uasset → prefer suggested subfolder (if any), else "Materials"
            if (isMaterialUAsset && ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase))
            {
                string targetDir = !string.IsNullOrWhiteSpace(relativeSuggestedPath)
                    ? Path.Combine(projectRoot, relativeSuggestedPath)
                    : Path.Combine(projectRoot, "Materials");
                Directory.CreateDirectory(targetDir);
                RememberMaterialLocation(sourceName, targetDir);
                return Path.Combine(targetDir, Path.GetFileName(sourceName));
            }

            // Everything else → misc (preserve relative path if provided, but nest under misc/)
            string miscBase = Path.Combine(projectRoot, "misc");
            string finalDir = miscBase;
            if (!string.IsNullOrWhiteSpace(relativeSuggestedPath))
                finalDir = Path.Combine(projectRoot, relativeSuggestedPath);

            Directory.CreateDirectory(finalDir);
            return Path.Combine(finalDir, Path.GetFileName(sourceName));
        }
    
      /// <summary>
        /// Second pass: move orphan UBULKs from OutputRoot/_ubulks next to their matching materials.
        /// Also populates MaterialDirsByStem by scanning the output tree for *.uasset/*.uasset2.
        /// </summary>
        public static void ReconcileOrphanUbulks(string projectRoot, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return;

            try
            {
                AnyAssetDirsByStem.Clear();

                // 1) Populate/refresh material map from disk
                foreach (var asset in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(asset);
                    if (!ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".uasset2", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string file = Path.GetFileName(asset);
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string dir = Path.GetDirectoryName(asset) ?? projectRoot;
                    if (string.IsNullOrEmpty(stem)) continue;

                    AnyAssetDirsByStem[stem] = dir;

                    // Jeśli wygląda na materiał, a nie mamy go w mapie — dopisz
                    if (!MaterialDirsByStem.ContainsKey(stem) && LooksLikeMaterial(stem, dir))
                        MaterialDirsByStem[stem] = dir;
                }

                // 2) Move orphans from _ubulks to material folders
                string stagedDir = Path.Combine(projectRoot, "_ubulks");
                if (!Directory.Exists(stagedDir)) return;

                int moved = 0, left = 0;
                foreach (var ub in Directory.EnumerateFiles(stagedDir, "*", SearchOption.TopDirectoryOnly).Where(IsUbulk))
                {
                    string name = Path.GetFileName(ub);
                    string stem = Path.GetFileNameWithoutExtension(name);
                    if (string.IsNullOrEmpty(stem)) { left++; continue; }

                    // a) exact match to material file
                    if (TryMove(ub, stem, MaterialDirsByStem, projectRoot, out _, log)) { moved++; continue; }

                    // b) material variations names to match
                    foreach (var cand in GenerateStemCandidates(stem))
                        if (TryMove(ub, cand, MaterialDirsByStem, projectRoot, out _, log)) { moved++; goto NEXT; }

                    // c) exact match to any asset
                    if (TryMove(ub, stem, AnyAssetDirsByStem, projectRoot, out _, log)) { moved++; continue; }

                    // d) variants for assets
                    foreach (var cand in GenerateStemCandidates(stem))
                        if (TryMove(ub, cand, AnyAssetDirsByStem, projectRoot, out _, log)) { moved++; goto NEXT; }

                    left++;
                NEXT:;
                }

                // 3) Remove _ubulks if empty
                if (!Directory.EnumerateFileSystemEntries(stagedDir).Any())
                {
                    try { Directory.Delete(stagedDir); } catch { /* ignore */ }
                }
                log?.Invoke($"Reconcile: moved {moved} UBULK, remained {left} w _ubulks");

            }
            catch (Exception ex)
            {
                log?.Invoke("⚠️ ReconcileOrphanUbulks: " + ex.Message);
            }
        }
        private static bool TryMove(string ubulkPath, string stem, Dictionary<string, string> map, string root, out string? movedTo, Action<string>? log)
        {
            movedTo = null;
            if (map.TryGetValue(stem, out string? targetDir) && Directory.Exists(targetDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(ubulkPath));
                dest = EnsureUnique(dest);
                File.Move(ubulkPath, dest);
                movedTo = dest;
                log?.Invoke($"🔁 UBULK → {Path.GetRelativePath(root, dest)}");
                return true;
            }
            return false;
        }

        // Generate stem variants: remove suffixes like _1, -2, .3, _lod1, -lod2, .lod3 
        private static IEnumerable<string> GenerateStemCandidates(string stem)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void add(string s) { if (!string.IsNullOrWhiteSpace(s)) seen.Add(s); }

            add(stem);
            string s = stem;
            for (int i = 0; i < 3; i++)
            {
                var m = TrailingIndex.Match(s);
                if (m.Success)
                {
                    s = s.Substring(0, m.Index);
                    add(s);
                }
                else break;
            }
            return seen;
        }

        // Heuristics: What does look like a material
        private static bool LooksLikeMaterial(string stem, string dir)
        {
            string up = (stem ?? "").ToUpperInvariant();
            if (up.StartsWith("M_") || up.StartsWith("MI_") || up.StartsWith("MIC_") || up.StartsWith("MF_"))
                return true;

            string lowDir = (dir ?? "").Replace('\\', '/').ToLowerInvariant();
            return lowDir.Contains("/material") || lowDir.Contains("/materials");
        }

        private static string EnsureUnique(string desiredPath)
        {
            if (!File.Exists(desiredPath)) return desiredPath;
            string dir = Path.GetDirectoryName(desiredPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(desiredPath);
            string ext = Path.GetExtension(desiredPath);
            int i = 1;
            while (true)
            {
                string cand = Path.Combine(dir, $"{name}_{i}{ext}");
                if (!File.Exists(cand)) return cand;
                i++;
            }
        }
    }
}