// LocalizationSkipper.cs
using System;
using System.Text.RegularExpressions;

namespace B2IndexExtractor
{
    public static class LocalizationSkipper
    {
        static readonly Regex RxLocToken = new Regex(@"(?:^|[\\/])(?:localized|unlocalized|localisation|localization|loc)(?:[\\/]|$)",
                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RxLangSeg = new Regex(@"(?:^|[\\/])(?:en|en-us|de|fr|it|es|pl|ru|pt|pt-br|ja|ko|zh|zh-cn|zh-tw)(?:[\\/]|$)",
                                                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsLikelyLocalized(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return RxLocToken.IsMatch(path) || RxLangSeg.IsMatch(path);
        }

        public static bool ShouldSkipByLocalization(bool assetsOnly, bool skipWem, string containerName, string entryPath)
        {
            if (!(assetsOnly || skipWem)) return false;
            return IsLikelyLocalized(containerName) || IsLikelyLocalized(entryPath);
        }
    }
}
