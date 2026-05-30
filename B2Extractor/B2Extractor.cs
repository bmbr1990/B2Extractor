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
        private static readonly Regex WemNameRegex =
            new Regex(@"^WEM\d+(\.[A-Za-z0-9_]+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsWemNumberFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string justName = Path.GetFileNameWithoutExtension(fileName);
            string withExt = Path.GetFileName(fileName);

            if (WemNameRegex.IsMatch(justName) || WemNameRegex.IsMatch(withExt))
                return true;

            return Path.GetExtension(fileName).Equals(".wem", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInWwiseAudioFolder(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();
            return normalizedPath.Contains("/wwiseaudio/")
                || normalizedPath.StartsWith("wwiseaudio/")
                || normalizedPath.Contains("/wwisetriton/")
                || normalizedPath.StartsWith("wwisetriton/");
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
        private const int HeaderSize = 0x80;
        private const int GroupDependencyRecordSize = 20;
        private const int ContainerRecordSize = 56;
        private const int ChunkDescRecordSize = 12;
        private const int BlockRecordSize = 40;
        private const int FileEntryRecordSize = 16;
        private const int PathNodeRecordSize = 16;

        private static readonly Dictionary<string, FileStream> _containerCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _createdDirs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _usedRelPaths = new(StringComparer.OrdinalIgnoreCase);

        private static int _oodleFailCount;
        private static bool _oodleDisabled;
        private static bool _oodleMissingWarned;
        private static bool _oodleEntryPointWarned;

        public static void ExtractAll(string indexPath, ExtractOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            options.Logger?.Invoke($"Opening index: {indexPath}");

            string baseDir = Path.GetDirectoryName(indexPath) ?? Directory.GetCurrentDirectory();
            var availableContainers = new HashSet<string>(
                Directory.EnumerateFiles(baseDir, "*.b2container", SearchOption.TopDirectoryOnly)
                         .Select(Path.GetFileName)
                         .Where(static n => !string.IsNullOrWhiteSpace(n))
                         .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);

            options.Logger?.Invoke($"🗂️ Found {availableContainers.Count} .b2container files next to index.");

            ExistingOutputIndex? existingIndex = null;
            if (options.SkipExistingFiles)
            {
                existingIndex = new ExistingOutputIndex(options.OutputDirectory);
                options.Logger?.Invoke("📝 ExistingOutputIndex ready.");
            }

            _createdDirs.Clear();
            _usedRelPaths.Clear();
            _oodleFailCount = 0;
            _oodleDisabled = false;
            _oodleMissingWarned = false;
            _oodleEntryPointWarned = false;

            try
            {
                byte[] indexData = File.ReadAllBytes(indexPath);
                B2Index index = B2Index.Parse(indexData, options.Logger);

                options.Logger?.Invoke(
                    $"📑 Parsed tables: groups={index.GroupDependencies.Length}, containers={index.Containers.Length}, chunks={index.Chunks.Length}, blocks={index.Blocks.Length}, fileEntries={index.FileEntries.Length}, pathAux={index.PathAux.Length}, pathNodes={index.PathNodes.Length}.");

                int totalFileNodes = index.PathNodes.Count(static n => n.IsFile);
                int processed = 0;
                int written = 0;
                int skipped = 0;
                int failed = 0;

                foreach (var nodeFile in EnumeratePathFiles(index))
                {
                    processed++;
                    options.Progress?.Invoke(100.0 * processed / Math.Max(1, totalFileNodes));

                    string relPath = NormalizeRelPath(nodeFile.VirtualPath);
                    if (string.IsNullOrWhiteSpace(relPath))
                    {
                        skipped++;
                        continue;
                    }

                    if (!TryResolveFile(index, nodeFile.FileEntryIndex, out B2FileEntryRecord fileEntry, out B2BlockRecord block, out B2ContainerRecord container, out string resolveError))
                    {
                        failed++;
                        options.Logger?.Invoke($"⚠️ {relPath}: {resolveError}");
                        continue;
                    }

                    string containerFileName = ResolveContainerFileName(container);
                    if (!availableContainers.Contains(containerFileName))
                    {
                        skipped++;
                        //options.Logger?.Invoke($"⏭️ Missing container {containerFileName}: {relPath}");
                        continue;
                    }

                    if (ShouldSkip(relPath, containerFileName, options, existingIndex))
                    {
                        skipped++;
                        continue;
                    }

                    string containerPath = Path.Combine(baseDir, containerFileName);
                    string outputPath = SafeCombineOutput(options.OutputDirectory, relPath);
                    outputPath = EnsureUniquePath(outputPath);

                    try
                    {
                        FileStream containerStream = GetContainer(containerPath, options);
                        byte[] fileBytes = ExtractFileData(containerStream, index, block, fileEntry.OffsetInBlock, fileEntry.Size, options);

                        string? outDir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(outDir) && _createdDirs.Add(outDir))
                            Directory.CreateDirectory(outDir);

                        File.WriteAllBytes(outputPath, fileBytes);
                        written++;

                        if (written % 1000 == 0)
                            options.Logger?.Invoke($"✔️ Extracted {written} files. Last: {Path.GetRelativePath(options.OutputDirectory, outputPath)}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        options.Logger?.Invoke($"⚠️ Failed: {relPath}: {ex.Message}");
                    }
                }

                options.Progress?.Invoke(100.0);
                options.Logger?.Invoke($"✅ Done. Written={written}, skipped={skipped}, failed={failed}, fileNodes={totalFileNodes}.");
            }
            finally
            {
                CloseAllContainers();
            }
        }

        private static bool TryResolveFile(
            B2Index index,
            int fileEntryIndex,
            out B2FileEntryRecord fileEntry,
            out B2BlockRecord block,
            out B2ContainerRecord container,
            out string error)
        {
            fileEntry = default;
            block = default;
            container = default;
            error = string.Empty;

            if ((uint)fileEntryIndex >= (uint)index.FileEntries.Length)
            {
                error = $"FileEntry index out of range: {fileEntryIndex}";
                return false;
            }

            fileEntry = index.FileEntries[fileEntryIndex];
            if ((uint)fileEntry.BlockIndex >= (uint)index.Blocks.Length)
            {
                error = $"Block index out of range: {fileEntry.BlockIndex}";
                return false;
            }

            block = index.Blocks[fileEntry.BlockIndex];
            if ((uint)block.ContainerIndex >= (uint)index.Containers.Length)
            {
                error = $"Container index out of range: {block.ContainerIndex}";
                return false;
            }

            if ((uint)block.FirstChunkIndex >= (uint)index.Chunks.Length && block.ChunkCount > 0)
            {
                error = $"Chunk index out of range: first={block.FirstChunkIndex}, count={block.ChunkCount}";
                return false;
            }

            if (block.FirstChunkIndex + block.ChunkCount > index.Chunks.Length)
            {
                error = $"Chunk range out of range: first={block.FirstChunkIndex}, count={block.ChunkCount}";
                return false;
            }

            container = index.Containers[block.ContainerIndex];
            return true;
        }

        private static IEnumerable<PathFile> EnumeratePathFiles(B2Index index)
        {
            int rootIndex = index.RootPathNodeIndex;
            if ((uint)rootIndex >= (uint)index.PathNodes.Length)
                rootIndex = 0;

            var stack = new Stack<(int NodeIndex, string ParentPath)>();
            stack.Push((rootIndex, string.Empty));

            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if ((uint)item.NodeIndex >= (uint)index.PathNodes.Length)
                    continue;

                B2PathNodeRecord node = index.PathNodes[item.NodeIndex];
                string name = index.GetString(node.NameOffset);
                string fullPath = CombineVirtualPath(item.ParentPath, name);

                if (node.IsFile)
                {
                    yield return new PathFile(fullPath, unchecked((int)node.FirstChildOrFileEntryIndex));
                    continue;
                }

                if (node.ChildCount < 0)
                    continue;

                int firstChild = unchecked((int)node.FirstChildOrFileEntryIndex);
                int childCount = node.ChildCount;
                if (firstChild < 0 || childCount < 0 || firstChild + childCount > index.PathNodes.Length)
                    continue;

                for (int i = childCount - 1; i >= 0; i--)
                    stack.Push((firstChild + i, fullPath));
            }
        }

        private static string CombineVirtualPath(string parent, string name)
        {
            if (string.IsNullOrEmpty(parent))
                return name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                return parent;
            return parent.TrimEnd('/', '\\') + "/" + name.TrimStart('/', '\\');
        }

        private static bool ShouldSkip(string relPath, string containerFileName, ExtractOptions options, ExistingOutputIndex? existingIndex)
        {
            string normalized = relPath.Replace('\\', '/');
            string fileName = Path.GetFileName(normalized);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();

            bool isUbulk = FileRouting.IsUbulk(fileName) || FileRouting.IsUbulk(ext);
            bool isAsset = isUbulk
                || ext == ".uasset"
                || ext == ".uasset2"
                || ext == ".umap"
                || ext == ".uexp";

            bool isWemExt = ext == ".wem";
            bool isWemNumber = WemUtils.IsWemNumberFile(fileName);
            bool isWemByPath = WemUtils.IsInWwiseAudioFolder(normalized);

            if (options.OnlyAssets)
            {
                if (!isAsset)
                    return true;

                if (isWemExt || isWemNumber || isWemByPath)
                    return true;
            }

            if (LocalizationSkipper.ShouldSkipByLocalization(options.OnlyAssets, options.SkipWemFiles, containerFileName, normalized))
                return true;

            if (options.SkipWemFiles && (isWemExt || isWemNumber || isWemByPath))
                return true;

            if (options.SkipResAndAce && (ext == ".res" || ext == ".ace"))
                return true;

            if (options.SkipConfigFiles && FileRouting.ConfigExts.Contains(ext))
                return true;

            if (options.SkipBinkFiles && (ext == ".bik" || ext == ".bk2"))
                return true;

            if (options.SkipExistingFiles && existingIndex != null)
            {
                if (existingIndex.HasExact(normalized))
                    return true;

                if ((ext == ".uasset" || ext == ".uasset2") && existingIndex.HasAnyOfTriplet(normalized))
                    return true;
            }

            return false;
        }

        private static byte[] ExtractFileData(FileStream containerStream, B2Index index, B2BlockRecord block, uint offsetInBlock, uint fileSize, ExtractOptions options)
        {
            if (fileSize == 0)
                return Array.Empty<byte>();

            if (fileSize > int.MaxValue)
                throw new InvalidDataException($"File too large for byte[] extraction: {fileSize} bytes.");

            long wantedStart = offsetInBlock;
            long wantedEnd = wantedStart + fileSize;
            byte[] result = new byte[(int)fileSize];

            long logicalCursor = 0;
            int written = 0;

            for (int i = 0; i < block.ChunkCount; i++)
            {
                B2ChunkDescRecord chunk = index.Chunks[block.FirstChunkIndex + i];
                long chunkStart = logicalCursor;
                long chunkEnd = chunkStart + chunk.UncompressedSize;

                if (chunkEnd <= wantedStart)
                {
                    logicalCursor = chunkEnd;
                    continue;
                }

                if (chunkStart >= wantedEnd)
                    break;

                long physicalStart = checked((long)block.PhysicalOffset + chunk.CompressedStartOffsetInBlock);
                long compressedSize = checked((long)chunk.CompressedEndOffsetInBlock - chunk.CompressedStartOffsetInBlock);
                if (compressedSize < 0)
                    throw new InvalidDataException($"Negative compressed chunk size. Block={block.PhysicalOffset:X}, chunk={i}.");

                byte[] chunkData = ExtractChunk(containerStream, physicalStart, compressedSize, chunk.UncompressedSize, options);
                if ((uint)chunkData.Length < chunk.UncompressedSize)
                    throw new InvalidDataException($"Decompressed chunk is too small: got={chunkData.Length}, expected={chunk.UncompressedSize}.");

                long copyStart = Math.Max(wantedStart, chunkStart);
                long copyEnd = Math.Min(wantedEnd, chunkEnd);
                int srcOffset = checked((int)(copyStart - chunkStart));
                int dstOffset = checked((int)(copyStart - wantedStart));
                int copyLen = checked((int)(copyEnd - copyStart));

                Buffer.BlockCopy(chunkData, srcOffset, result, dstOffset, copyLen);
                written += copyLen;

                logicalCursor = chunkEnd;
            }

            if ((uint)written != fileSize)
                throw new EndOfStreamException($"Could not assemble full file from block. Written={written}, expected={fileSize}, offsetInBlock={offsetInBlock}.");

            return result;
        }

        private static byte[] ExtractChunk(FileStream cfs, long offset, long compSize, long uncSize, ExtractOptions options)
        {
            if (offset < 0 || offset > cfs.Length)
                throw new InvalidDataException($"Chunk offset outside container: 0x{offset:X}.");

            if (compSize < 0 || compSize > int.MaxValue)
                throw new InvalidDataException($"Invalid compressed chunk size: {compSize}.");

            if (uncSize < 0 || uncSize > int.MaxValue)
                throw new InvalidDataException($"Invalid uncompressed chunk size: {uncSize}.");

            if (offset + compSize > cfs.Length)
                throw new EndOfStreamException($"Chunk exceeds container length. Offset=0x{offset:X}, compSize={compSize}, containerSize={cfs.Length}.");

            byte[] comp = ArrayPool<byte>.Shared.Rent((int)compSize);
            try
            {
                cfs.Seek(offset, SeekOrigin.Begin);
                ReadExactly(cfs, comp, 0, (int)compSize);

                if (compSize == uncSize)
                {
                    byte[] same = new byte[(int)uncSize];
                    Buffer.BlockCopy(comp, 0, same, 0, (int)uncSize);
                    return same;
                }

                if (_oodleDisabled)
                    throw new InvalidDataException("Oodle decompression is disabled after earlier failures.");

                byte[] raw = new byte[(int)uncSize];
                try
                {
                    int ret = NativeMethods.OodleLZ_Decompress(
                        comp, compSize,
                        raw, uncSize,
                        1, 0, 0,
                        IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero,
                        0);

                    if (ret > 0)
                    {
                        _oodleFailCount = 0;
                        return raw;
                    }

                    _oodleFailCount++;
                    if (_oodleFailCount >= 64)
                    {
                        _oodleDisabled = true;
                        options.Logger?.Invoke("⛔ Oodle disabled after 64 failed decompression attempts.");
                    }

                    throw new InvalidDataException($"Oodle returned {ret} for comp={compSize}, raw={uncSize}.");
                }
                catch (DllNotFoundException)
                {
                    if (!_oodleMissingWarned)
                    {
                        _oodleMissingWarned = true;
                        options.Logger?.Invoke("⚠️ oo2core_7_win64.dll is missing. Compressed files cannot be extracted correctly.");
                    }
                    throw;
                }
                catch (EntryPointNotFoundException)
                {
                    if (!_oodleEntryPointWarned)
                    {
                        _oodleEntryPointWarned = true;
                        options.Logger?.Invoke("⚠️ Oodle entry point not found. Check oo2core_7_win64.dll version.");
                    }
                    throw;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(comp);
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                    throw new EndOfStreamException();
                total += read;
            }
        }

        private static string ResolveContainerFileName(B2ContainerRecord container)
        {
            string name = container.Name ?? string.Empty;
            name = name.Replace('\\', '/').Trim();
            name = Path.GetFileName(name);

            if (string.IsNullOrWhiteSpace(name))
                name = "unknown";

            if (!name.EndsWith(".b2container", StringComparison.OrdinalIgnoreCase))
                name += ".b2container";

            return name;
        }

        private static string NormalizeRelPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "unnamed.bin";

            string s = input.Replace('\\', '/').Trim();

            int colon = s.IndexOf(':');
            if (colon >= 0)
                s = s[(colon + 1)..];

            var invalid = Path.GetInvalidFileNameChars();
            var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var safe = new List<string>(parts.Length);

            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Length == 0 || part == "." || part == "..")
                    continue;

                foreach (char ch in invalid)
                    part = part.Replace(ch, '_');

                part = part.TrimEnd(' ', '.');
                if (part.Length == 0)
                    part = "_";

                string upper = part.ToUpperInvariant();
                if (upper is "CON" or "PRN" or "AUX" or "NUL"
                    or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
                    or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
                {
                    part = "_" + part;
                }

                safe.Add(part);
            }

            return safe.Count == 0 ? "unnamed.bin" : string.Join("/", safe);
        }

        private static string SafeCombineOutput(string outputRoot, string relPath)
        {
            string root = Path.GetFullPath(outputRoot);
            string candidate = Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));

            string rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Path traversal detected: {relPath}");

            return candidate;
        }

        private static string EnsureUniquePath(string desiredAbsPath)
        {
            string relKey = desiredAbsPath.ToLowerInvariant();
            if (_usedRelPaths.Add(relKey) && !File.Exists(desiredAbsPath))
                return desiredAbsPath;

            string dir = Path.GetDirectoryName(desiredAbsPath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(desiredAbsPath);
            string ext = Path.GetExtension(desiredAbsPath);

            int i = 1;
            while (true)
            {
                string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
                string key = candidate.ToLowerInvariant();
                if (_usedRelPaths.Add(key) && !File.Exists(candidate))
                    return candidate;
                i++;
            }
        }

        private static FileStream GetContainer(string containerPath, ExtractOptions options)
        {
            if (_containerCache.TryGetValue(containerPath, out FileStream? existing) && existing.CanRead)
                return existing;

            var stream = new FileStream(
                containerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1 << 20,
                options: FileOptions.RandomAccess);

            _containerCache[containerPath] = stream;
            options.Logger?.Invoke($"🗃️ Opening container: {Path.GetFileName(containerPath)}");
            return stream;
        }

        private static void CloseAllContainers()
        {
            foreach (var stream in _containerCache.Values)
            {
                try { stream.Dispose(); } catch { }
            }
            _containerCache.Clear();
        }

        private readonly struct PathFile
        {
            public readonly string VirtualPath;
            public readonly int FileEntryIndex;

            public PathFile(string virtualPath, int fileEntryIndex)
            {
                VirtualPath = virtualPath;
                FileEntryIndex = fileEntryIndex;
            }
        }

        private sealed class B2Index
        {
            public readonly B2Header Header;
            public readonly B2GroupDependencyRecord[] GroupDependencies;
            public readonly B2ContainerRecord[] Containers;
            public readonly B2ChunkDescRecord[] Chunks;
            public readonly B2BlockRecord[] Blocks;
            public readonly B2FileEntryRecord[] FileEntries;
            public readonly int[] PathAux;
            public readonly B2PathNodeRecord[] PathNodes;
            public readonly int RootPathNodeIndex;

            private readonly byte[] _data;
            private readonly Dictionary<ulong, string> _stringCache = new();

            private B2Index(
                byte[] data,
                B2Header header,
                B2GroupDependencyRecord[] groupDependencies,
                B2ContainerRecord[] containers,
                B2ChunkDescRecord[] chunks,
                B2BlockRecord[] blocks,
                B2FileEntryRecord[] fileEntries,
                int[] pathAux,
                B2PathNodeRecord[] pathNodes)
            {
                _data = data;
                Header = header;
                GroupDependencies = groupDependencies;
                Containers = containers;
                Chunks = chunks;
                Blocks = blocks;
                FileEntries = fileEntries;
                PathAux = pathAux;
                PathNodes = pathNodes;
                RootPathNodeIndex = PtrToIndexOrDefault(header.RootPathNodePtr, header.PathNodeTableOffset, PathNodeRecordSize, pathNodes.Length, 0);
            }

            public string GetString(ulong offset)
            {
                if (_stringCache.TryGetValue(offset, out string? cached))
                    return cached;

                string value = ReadCString(_data, offset);
                _stringCache[offset] = value;
                return value;
            }

            public static B2Index Parse(byte[] data, Action<string>? logger)
            {
                B2Header header = ReadHeader(data);
                ValidateHeader(data, header);

                if ((ulong)data.LongLength != header.FileSize)
                    logger?.Invoke($"⚠️ Header file size is 0x{header.FileSize:X}, actual file size is 0x{data.LongLength:X}.");

                B2GroupDependencyRecord[] groupDependencies = ReadGroupDependencies(data, header);
                B2ChunkDescRecord[] chunks = ReadChunks(data, header);
                B2ContainerRecord[] containers = ReadContainers(data, header, checked((int)header.GroupDependencyCount));
                B2BlockRecord[] blocks = ReadBlocks(data, header, containers.Length, chunks.Length);
                B2FileEntryRecord[] fileEntries = ReadFileEntries(data, header, blocks.Length);
                int[] pathAux = ReadPathAux(data, header);
                B2PathNodeRecord[] pathNodes = ReadPathNodes(data, header);

                return new B2Index(data, header, groupDependencies, containers, chunks, blocks, fileEntries, pathAux, pathNodes);
            }

            private static B2Header ReadHeader(byte[] d)
            {
                if (d.Length < HeaderSize)
                    throw new InvalidDataException("File is too small for B2 header.");

                if (d[0] != (byte)'T' || d[1] != (byte)'C' || d[2] != (byte)'B' || d[3] != (byte)'2')
                    throw new InvalidDataException("Not a TCB2 .b2index file.");

                return new B2Header(
                    version: ReadUInt32(d, 0x04),
                    fileSize: ReadUInt64(d, 0x08),
                    rootPathNodePtr: ReadUInt64(d, 0x10),
                    packageInfoCacheFileEntryPtr: ReadUInt64(d, 0x18),
                    groupDependencyTableOffset: ReadUInt64(d, 0x20),
                    groupDependencyCount: ReadUInt32(d, 0x28),
                    containerTableOffset: ReadUInt64(d, 0x2C),
                    containerCount: ReadUInt32(d, 0x34),
                    blockTableOffset: ReadUInt64(d, 0x38),
                    blockCount: ReadUInt32(d, 0x40),
                    fileEntryTableOffset: ReadUInt64(d, 0x44),
                    fileEntryCount: ReadUInt32(d, 0x4C),
                    unknownPathAuxTableOffset: ReadUInt64(d, 0x50),
                    unknownPathAuxCount: ReadUInt32(d, 0x58),
                    pathNodeTableOffset: ReadUInt64(d, 0x5C),
                    pathNodeCount: ReadUInt32(d, 0x64),
                    stringBlobOffset: ReadUInt64(d, 0x68),
                    stringBlobSize: ReadUInt32(d, 0x70),
                    chunkDescTableOffset: ReadUInt64(d, 0x74),
                    chunkDescCount: ReadUInt32(d, 0x7C));
            }

            private static void ValidateHeader(byte[] data, B2Header h)
            {
                ValidateRange(data, h.GroupDependencyTableOffset, h.GroupDependencyCount, GroupDependencyRecordSize, nameof(h.GroupDependencyTableOffset));
                ValidateRange(data, h.ContainerTableOffset, h.ContainerCount, ContainerRecordSize, nameof(h.ContainerTableOffset));
                ValidateRange(data, h.ChunkDescTableOffset, h.ChunkDescCount, ChunkDescRecordSize, nameof(h.ChunkDescTableOffset));
                ValidateRange(data, h.BlockTableOffset, h.BlockCount, BlockRecordSize, nameof(h.BlockTableOffset));
                ValidateRange(data, h.FileEntryTableOffset, h.FileEntryCount, FileEntryRecordSize, nameof(h.FileEntryTableOffset));
                ValidateRange(data, h.UnknownPathAuxTableOffset, h.UnknownPathAuxCount, 4, nameof(h.UnknownPathAuxTableOffset));
                ValidateRange(data, h.PathNodeTableOffset, h.PathNodeCount, PathNodeRecordSize, nameof(h.PathNodeTableOffset));
                ValidateRange(data, h.StringBlobOffset, h.StringBlobSize, 1, nameof(h.StringBlobOffset));
            }

            private static void ValidateRange(byte[] data, ulong offset, uint count, int stride, string name)
            {
                ulong byteCount = checked((ulong)count * (ulong)stride);
                ulong end = checked(offset + byteCount);
                if (offset > (ulong)data.LongLength || end > (ulong)data.LongLength)
                    throw new InvalidDataException($"{name} range is outside file: offset=0x{offset:X}, count={count}, stride={stride}.");
            }


            private static B2GroupDependencyRecord[] ReadGroupDependencies(byte[] d, B2Header h)
            {
                var records = new B2GroupDependencyRecord[checked((int)h.GroupDependencyCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.GroupDependencyTableOffset + i * GroupDependencyRecordSize);
                    ulong nameOffset = ReadUInt64(d, o + 0x00);
                    ulong depsPtr = ReadUInt64(d, o + 0x08);
                    int depsCount = checked((int)ReadUInt32(d, o + 0x10));

                    var deps = Array.Empty<int>();
                    if (depsCount > 0)
                    {
                        deps = new int[depsCount];
                        for (int j = 0; j < depsCount; j++)
                        {
                            ulong depRecordPtr = ReadUInt64(d, checked((int)depsPtr + j * 8));
                            deps[j] = PtrToIndexOrDefault(depRecordPtr, h.GroupDependencyTableOffset, GroupDependencyRecordSize, records.Length, -1);
                        }
                    }

                    records[i] = new B2GroupDependencyRecord(
                        nameOffset: nameOffset,
                        name: ReadCString(d, nameOffset),
                        dependenciesPtr: depsPtr,
                        dependencyIndices: deps);
                }

                return records;
            }

            private static B2ContainerRecord[] ReadContainers(byte[] d, B2Header h, int groupDependencyCount)
            {
                var records = new B2ContainerRecord[checked((int)h.ContainerCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.ContainerTableOffset + i * ContainerRecordSize);
                    records[i] = new B2ContainerRecord(
                        name: ReadCString(d, ReadUInt64(d, o + 0x00)),
                        suffix: ReadCString(d, ReadUInt64(d, o + 0x08)),
                        groupRecordPtr: ReadUInt64(d, o + 0x10),
                        groupIndex: PtrToIndexOrDefault(ReadUInt64(d, o + 0x10), h.GroupDependencyTableOffset, GroupDependencyRecordSize, groupDependencyCount, -1),
                        containerSize: ReadUInt64(d, o + 0x18),
                        firstBlockIndex: PtrToIndexOrDefault(ReadUInt64(d, o + 0x20), h.BlockTableOffset, BlockRecordSize, checked((int)h.BlockCount), -1),
                        blockCount: checked((int)ReadUInt32(d, o + 0x28)),
                        firstChunkIndex: PtrToIndexOrDefault(ReadUInt64(d, o + 0x2C), h.ChunkDescTableOffset, ChunkDescRecordSize, checked((int)h.ChunkDescCount), -1),
                        chunkCount: checked((int)ReadUInt32(d, o + 0x34)));
                }
                return records;
            }

            private static B2ChunkDescRecord[] ReadChunks(byte[] d, B2Header h)
            {
                var records = new B2ChunkDescRecord[checked((int)h.ChunkDescCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.ChunkDescTableOffset + i * ChunkDescRecordSize);
                    records[i] = new B2ChunkDescRecord(
                        uncompressedSize: ReadUInt32(d, o + 0x00),
                        compressedStartOffsetInBlock: ReadUInt32(d, o + 0x04),
                        compressedEndOffsetInBlock: ReadUInt32(d, o + 0x08));
                }
                return records;
            }

            private static B2BlockRecord[] ReadBlocks(byte[] d, B2Header h, int containerCount, int chunkCount)
            {
                var records = new B2BlockRecord[checked((int)h.BlockCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.BlockTableOffset + i * BlockRecordSize);
                    records[i] = new B2BlockRecord(
                        containerIndex: PtrToIndexOrDefault(ReadUInt64(d, o + 0x00), h.ContainerTableOffset, ContainerRecordSize, containerCount, -1),
                        logicalOffset: ReadUInt64(d, o + 0x08),
                        physicalOffset: ReadUInt64(d, o + 0x10),
                        flags: ReadUInt32(d, o + 0x18),
                        firstChunkIndex: PtrToIndexOrDefault(ReadUInt64(d, o + 0x1C), h.ChunkDescTableOffset, ChunkDescRecordSize, chunkCount, -1),
                        chunkCount: checked((int)ReadUInt32(d, o + 0x24)));
                }
                return records;
            }

            private static B2FileEntryRecord[] ReadFileEntries(byte[] d, B2Header h, int blockCount)
            {
                var records = new B2FileEntryRecord[checked((int)h.FileEntryCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.FileEntryTableOffset + i * FileEntryRecordSize);
                    records[i] = new B2FileEntryRecord(
                        blockIndex: PtrToIndexOrDefault(ReadUInt64(d, o + 0x00), h.BlockTableOffset, BlockRecordSize, blockCount, -1),
                        offsetInBlock: ReadUInt32(d, o + 0x08),
                        size: ReadUInt32(d, o + 0x0C));
                }
                return records;
            }

            private static int[] ReadPathAux(byte[] d, B2Header h)
            {
                var records = new int[checked((int)h.UnknownPathAuxCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.UnknownPathAuxTableOffset + i * 4);
                    records[i] = ReadInt32(d, o);
                }
                return records;
            }

            private static B2PathNodeRecord[] ReadPathNodes(byte[] d, B2Header h)
            {
                var records = new B2PathNodeRecord[checked((int)h.PathNodeCount)];
                for (int i = 0; i < records.Length; i++)
                {
                    int o = checked((int)h.PathNodeTableOffset + i * PathNodeRecordSize);
                    records[i] = new B2PathNodeRecord(
                        nameOffset: ReadUInt64(d, o + 0x00),
                        firstChildOrFileEntryIndex: ReadUInt32(d, o + 0x08),
                        childCount: ReadInt32(d, o + 0x0C));
                }
                return records;
            }

            private static int PtrToIndexOrDefault(ulong ptr, ulong tableOffset, int stride, int count, int fallback)
            {
                if (ptr < tableOffset)
                    return fallback;

                ulong delta = ptr - tableOffset;
                if (delta % (ulong)stride != 0)
                    return fallback;

                ulong index = delta / (ulong)stride;
                if (index >= (ulong)count)
                    return fallback;

                return checked((int)index);
            }

            private static uint ReadUInt32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
            private static int ReadInt32(byte[] d, int o) => BitConverter.ToInt32(d, o);
            private static ulong ReadUInt64(byte[] d, int o) => BitConverter.ToUInt64(d, o);

            private static string ReadCString(byte[] d, ulong offset)
            {
                if (offset >= (ulong)d.LongLength)
                    return string.Empty;

                int start = checked((int)offset);
                int end = start;
                while (end < d.Length && d[end] != 0)
                    end++;

                if (end <= start)
                    return string.Empty;

                return Encoding.UTF8.GetString(d, start, end - start);
            }
        }

        private readonly struct B2Header
        {
            public readonly uint Version;
            public readonly ulong FileSize;
            public readonly ulong RootPathNodePtr;
            public readonly ulong PackageInfoCacheFileEntryPtr;
            public readonly ulong GroupDependencyTableOffset;
            public readonly uint GroupDependencyCount;
            public readonly ulong ContainerTableOffset;
            public readonly uint ContainerCount;
            public readonly ulong BlockTableOffset;
            public readonly uint BlockCount;
            public readonly ulong FileEntryTableOffset;
            public readonly uint FileEntryCount;
            public readonly ulong UnknownPathAuxTableOffset;
            public readonly uint UnknownPathAuxCount;
            public readonly ulong PathNodeTableOffset;
            public readonly uint PathNodeCount;
            public readonly ulong StringBlobOffset;
            public readonly uint StringBlobSize;
            public readonly ulong ChunkDescTableOffset;
            public readonly uint ChunkDescCount;

            public B2Header(
                uint version,
                ulong fileSize,
                ulong rootPathNodePtr,
                ulong packageInfoCacheFileEntryPtr,
                ulong groupDependencyTableOffset,
                uint groupDependencyCount,
                ulong containerTableOffset,
                uint containerCount,
                ulong blockTableOffset,
                uint blockCount,
                ulong fileEntryTableOffset,
                uint fileEntryCount,
                ulong unknownPathAuxTableOffset,
                uint unknownPathAuxCount,
                ulong pathNodeTableOffset,
                uint pathNodeCount,
                ulong stringBlobOffset,
                uint stringBlobSize,
                ulong chunkDescTableOffset,
                uint chunkDescCount)
            {
                Version = version;
                FileSize = fileSize;
                RootPathNodePtr = rootPathNodePtr;
                PackageInfoCacheFileEntryPtr = packageInfoCacheFileEntryPtr;
                GroupDependencyTableOffset = groupDependencyTableOffset;
                GroupDependencyCount = groupDependencyCount;
                ContainerTableOffset = containerTableOffset;
                ContainerCount = containerCount;
                BlockTableOffset = blockTableOffset;
                BlockCount = blockCount;
                FileEntryTableOffset = fileEntryTableOffset;
                FileEntryCount = fileEntryCount;
                UnknownPathAuxTableOffset = unknownPathAuxTableOffset;
                UnknownPathAuxCount = unknownPathAuxCount;
                PathNodeTableOffset = pathNodeTableOffset;
                PathNodeCount = pathNodeCount;
                StringBlobOffset = stringBlobOffset;
                StringBlobSize = stringBlobSize;
                ChunkDescTableOffset = chunkDescTableOffset;
                ChunkDescCount = chunkDescCount;
            }
        }


        private readonly struct B2GroupDependencyRecord
        {
            public readonly ulong NameOffset;
            public readonly string Name;
            public readonly ulong DependenciesPtr;
            public readonly int[] DependencyIndices;

            public B2GroupDependencyRecord(ulong nameOffset, string name, ulong dependenciesPtr, int[] dependencyIndices)
            {
                NameOffset = nameOffset;
                Name = name;
                DependenciesPtr = dependenciesPtr;
                DependencyIndices = dependencyIndices;
            }
        }

        private readonly struct B2ContainerRecord
        {
            public readonly string Name;
            public readonly string Suffix;
            public readonly ulong GroupRecordPtr;
            public readonly int GroupIndex;
            public readonly ulong ContainerSize;
            public readonly int FirstBlockIndex;
            public readonly int BlockCount;
            public readonly int FirstChunkIndex;
            public readonly int ChunkCount;

            public B2ContainerRecord(string name, string suffix, ulong groupRecordPtr, int groupIndex, ulong containerSize, int firstBlockIndex, int blockCount, int firstChunkIndex, int chunkCount)
            {
                Name = name;
                Suffix = suffix;
                GroupRecordPtr = groupRecordPtr;
                GroupIndex = groupIndex;
                ContainerSize = containerSize;
                FirstBlockIndex = firstBlockIndex;
                BlockCount = blockCount;
                FirstChunkIndex = firstChunkIndex;
                ChunkCount = chunkCount;
            }
        }

        private readonly struct B2ChunkDescRecord
        {
            public readonly uint UncompressedSize;
            public readonly uint CompressedStartOffsetInBlock;
            public readonly uint CompressedEndOffsetInBlock;

            public B2ChunkDescRecord(uint uncompressedSize, uint compressedStartOffsetInBlock, uint compressedEndOffsetInBlock)
            {
                UncompressedSize = uncompressedSize;
                CompressedStartOffsetInBlock = compressedStartOffsetInBlock;
                CompressedEndOffsetInBlock = compressedEndOffsetInBlock;
            }
        }

        private readonly struct B2BlockRecord
        {
            public readonly int ContainerIndex;
            public readonly ulong LogicalOffset;
            public readonly ulong PhysicalOffset;
            public readonly uint Flags;
            public readonly int FirstChunkIndex;
            public readonly int ChunkCount;

            public B2BlockRecord(int containerIndex, ulong logicalOffset, ulong physicalOffset, uint flags, int firstChunkIndex, int chunkCount)
            {
                ContainerIndex = containerIndex;
                LogicalOffset = logicalOffset;
                PhysicalOffset = physicalOffset;
                Flags = flags;
                FirstChunkIndex = firstChunkIndex;
                ChunkCount = chunkCount;
            }
        }

        private readonly struct B2FileEntryRecord
        {
            public readonly int BlockIndex;
            public readonly uint OffsetInBlock;
            public readonly uint Size;

            public B2FileEntryRecord(int blockIndex, uint offsetInBlock, uint size)
            {
                BlockIndex = blockIndex;
                OffsetInBlock = offsetInBlock;
                Size = size;
            }
        }

        private readonly struct B2PathNodeRecord
        {
            public readonly ulong NameOffset;
            public readonly uint FirstChildOrFileEntryIndex;
            public readonly int ChildCount;

            public bool IsFile => ChildCount == -1;

            public B2PathNodeRecord(ulong nameOffset, uint firstChildOrFileEntryIndex, int childCount)
            {
                NameOffset = nameOffset;
                FirstChildOrFileEntryIndex = firstChildOrFileEntryIndex;
                ChildCount = childCount;
            }
        }
    }
}
