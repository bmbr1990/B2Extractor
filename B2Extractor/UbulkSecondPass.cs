//
// UbulkSecondPass.cs
// Adds a post-extraction routing pass to place stray *.ubulk / *.ubulkN files
// next to their owning .uasset. Unmatched files go to "_ubulks".
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace B2IndexExtractor
{
    public interface ILogger
    {
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg);
        void Debug(string msg);
    }

    public static class UbulkSecondPass
    {
        // Regex that matches: <BaseName>.ubulk or <BaseName>.ubulk<digits>
        private static readonly Regex UbulkName = new Regex(@"^(?i)(?<base>.+?)\.ubulk(?<idx>\d+)?$", RegexOptions.Compiled);

        /// <summary>
        /// Moves any *.ubulk/*.ubulkN files to the directory that contains their matching .uasset.
        /// Uses the provided uassetFolderMap (key: lowercased base filename without extension; value: absolute folder path).
        /// Orphans are moved into {outputRoot}/_ubulks.
        /// </summary>
        public static void Run(string outputRoot, IReadOnlyDictionary<string, string> uassetFolderMap, ILogger log)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                log?.Warn($"[UBULK-2ndPass] Output root '{outputRoot}' does not exist. Skipping.");
                return;
            }

            var ubulkCandidates = Directory.EnumerateFiles(outputRoot, "*.ubulk*", SearchOption.AllDirectories)
                                           .Where(p => File.Exists(p))
                                           .ToList();

            if (ubulkCandidates.Count == 0)
            {
                log?.Info("[UBULK-2ndPass] No *.ubulk files found. Nothing to do.");
                return;
            }

            // Build a HashSet of uasset folders to avoid moving files that are already in place.
            var uassetFolders = new HashSet<string>(uassetFolderMap.Values.Select(p => NormalizePath(p)));

            string orphanDir = Path.Combine(outputRoot, "_ubulks");
            Directory.CreateDirectory(orphanDir);

            int moved = 0, orphaned = 0, skipped = 0;

            foreach (var srcPath in ubulkCandidates)
            {
                var fileName = Path.GetFileName(srcPath);
                if (fileName == null) { skipped++; continue; }

                var normSrcDir = NormalizePath(Path.GetDirectoryName(srcPath) ?? string.Empty);

                // If the file is already in some known uasset directory, skip.
                if (uassetFolders.Contains(normSrcDir))
                {
                    skipped++;
                    continue;
                }

                // Extract the "base" portion before ".ubulk" and optional digits.
                var baseKey = ExtractBaseKey(fileName);
                if (string.IsNullOrEmpty(baseKey))
                {
                    // Unrecognized pattern -> orphan
                    var dstPathUnknown = MakeUnique(Path.Combine(orphanDir, fileName));
                    MoveFileSafe(srcPath, dstPathUnknown, log);
                    orphaned++;
                    continue;
                }

                if (uassetFolderMap.TryGetValue(baseKey, out var targetFolder) && Directory.Exists(targetFolder))
                {
                    var dstPath = MakeUnique(Path.Combine(targetFolder, fileName));
                    MoveFileSafe(srcPath, dstPath, log);
                    moved++;
                }
                else
                {
                    // Not found in the map -> orphan
                    var dstPath = MakeUnique(Path.Combine(orphanDir, fileName));
                    MoveFileSafe(srcPath, dstPath, log);
                    orphaned++;
                }
            }

            log?.Info($"[UBULK-2ndPass] Done. Moved: {moved}, Orphaned -> _ubulks: {orphaned}, Skipped (already placed): {skipped}.");
        }

        private static string ExtractBaseKey(string fileName)
        {
            var m = UbulkName.Match(fileName);
            if (!m.Success) return "";
            var baseName = m.Groups["base"].Value;
            if (string.IsNullOrWhiteSpace(baseName)) return "";

            // Often ubulk belongs to the same base as the .uasset (e.g., TextureA.uasset <-> TextureA.ubulk)
            // We normalize the key to lower-invariant for dictionary lookups.
            return Path.GetFileNameWithoutExtension(baseName).ToLowerInvariant();
        }

        private static string NormalizePath(string p)
        {
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                      .ToLowerInvariant();
        }

        private static string MakeUnique(string dstPath)
        {
            if (!File.Exists(dstPath)) return dstPath;
            var dir = Path.GetDirectoryName(dstPath) ?? "";
            var name = Path.GetFileNameWithoutExtension(dstPath);
            var ext = Path.GetExtension(dstPath);
            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                i++;
            } while (File.Exists(candidate));
            return candidate;
        }

        private static void MoveFileSafe(string src, string dst, ILogger log)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? string.Empty);
                File.Move(src, dst);
                log?.Debug($"[UBULK-2ndPass] Moved: {src} -> {dst}");
            }
            catch (Exception ex)
            {
                log?.Error($"[UBULK-2ndPass] Failed to move '{src}' -> '{dst}': {ex.Message}");
            }
        }
    }
}
