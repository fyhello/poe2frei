using System.IO;
using System.Runtime.InteropServices;

namespace FreiAtlas.Patcher;

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

    public static byte[] DecompressToAlloc(byte[] src, int srcLen, int dstSize)
    {
        byte[] array = new byte[dstSize + 64];
        int num = Decompress(src, srcLen, array, dstSize);
        if (num < 0)
        {
            throw new InvalidOperationException($"Oodle 解压失败，返回码 {num}");
        }
        Array.Resize(ref array, dstSize);
        return array;
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

    public static byte[] CompressChunk(byte[] chunk)
    {
        if (chunk.Length == 0)
        {
            return Array.Empty<byte>();
        }
        byte[] array = new byte[chunk.Length + 548];
        int num = OodleLZ_Compress(OODLE_MERMAID_COMPRESSOR, chunk, chunk.Length, array, OODLE_COMPRESS_LEVEL, IntPtr.Zero, 0L, 0L, IntPtr.Zero, 0L);
        if (num <= 0)
        {
            throw new InvalidOperationException($"Oodle 压缩失败，返回码 {num}（输入 {chunk.Length} 字节）");
        }
        Array.Resize(ref array, num);
        return array;
    }
}
