// ExistingOutputIndex.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace B2IndexExtractor
{
    public sealed class ExistingOutputIndex
    {
        private readonly HashSet<string> _relPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _root;

        public ExistingOutputIndex(string outputRoot)
        {
            _root = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(_root)) return;

            foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_root, path);
                _relPaths.Add(Norm(rel));
                _fileNames.Add(Path.GetFileName(path));
            }
        }

        private static string Norm(string rel) => rel.Replace('\\', '/');

        public bool HasExact(string relativePath) => _relPaths.Contains(Norm(relativePath));
        public bool HasByFileName(string fileName) => _fileNames.Contains(fileName);

        public bool HasAnyOfTriplet(string relativeUassetPath)
        {
            string fileNameNoExt = Path.GetFileNameWithoutExtension(relativeUassetPath);
            string dir = Path.GetDirectoryName(relativeUassetPath) ?? string.Empty;

            string Make(string ext) => Norm(Path.Combine(dir, fileNameNoExt + ext));

            return _relPaths.Contains(Norm(relativeUassetPath))
                || _relPaths.Contains(Make(".uexp"))
                || _relPaths.Contains(Make(".ubulk"));
        }
    }
}
