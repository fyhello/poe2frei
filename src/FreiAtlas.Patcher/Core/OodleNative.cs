using System.IO;
using System.Runtime.InteropServices;

namespace FreiAtlas.Patcher.Core;

internal static class OodleNative
{
    public const int OODLE_MERMAID_COMPRESSOR = 9;
    public const int OODLE_COMPRESS_LEVEL = 1;

    private static bool _initialized;
    private static readonly object _initLock = new();

    static OodleNative()
    {
        EnsureLoaded();
    }

    public static void EnsureLoaded(string? fallbackDirectory = null)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            string[] searchPaths = [
                Path.Combine(AppContext.BaseDirectory, "oo2core.dll"),
                Path.Combine(AppContext.BaseDirectory, "libs", "oo2core.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), "libs", "oo2core.dll"),
                fallbackDirectory != null ? Path.Combine(fallbackDirectory, "oo2core.dll") : ""
            ];

            foreach (var path in searchPaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    if (NativeLibrary.TryLoad(path, out _))
                    {
                        _initialized = true;
                        return;
                    }
                }
            }

            // Fallback to default resolution
            try
            {
                NativeLibrary.Load("oo2core.dll");
                _initialized = true;
            }
            catch
            {
                // Ignored - will throw on DllImport if missing
            }
        }
    }

    [DllImport("oo2core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OodleLZ_Decompress(
        byte[] buffer,
        int bufferSize,
        byte[] result,
        long outputBufferSize,
        int a,
        int b,
        int c,
        nint d,
        long e,
        nint f,
        nint g,
        nint h,
        long i,
        int ThreadModule);

    public static int Decompress(byte[] src, int srcLen, byte[] dst, int dstSize)
    {
        return OodleLZ_Decompress(src, srcLen, dst, dstSize, 0, 0, 0, IntPtr.Zero, 0L, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0L, 3);
    }


    [DllImport("oo2core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OodleLZ_Compress(
        int format,
        byte[] buffer,
        long bufferSize,
        byte[] outputBuffer,
        int level,
        nint opts,
        long offs,
        long unused,
        nint scratch,
        long scratch_size);

}
