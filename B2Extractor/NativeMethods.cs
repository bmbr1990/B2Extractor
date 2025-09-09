using System;
using System.IO;
using System.Runtime.InteropServices;

namespace B2IndexExtractor
{
    internal static class NativeMethods
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        public static bool EnsureOodleLoaded()
        {
            try
            {
                var exeDir = AppContext.BaseDirectory;
                var dllPath = Path.Combine(exeDir, "oo2core_7_win64.dll");
                if (File.Exists(dllPath))
                {
                    var h = LoadLibraryW(dllPath);
                    return h != IntPtr.Zero;
                }
            }
            catch { }
            return false;
        }

        [DllImport("oo2core_7_win64.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int OodleLZ_Decompress(
            byte[] compBuf, long compBufSize,
            byte[] rawBuf, long rawLen,
            int fuzzSafe, int checkCrc, int verbosity,
            IntPtr decBufBase, IntPtr decBufSize,
            IntPtr fpCallback, IntPtr callbackUserData,
            IntPtr decoderMemory, IntPtr scratch,
            int threadPhase
        );
    }
}
