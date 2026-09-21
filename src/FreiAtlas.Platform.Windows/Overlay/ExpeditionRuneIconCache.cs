using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FreiAtlas.Platform.Windows.Overlay;

internal sealed class ExpeditionRuneIconCache : IDisposable
{
    private readonly record struct DecodedIcon(byte[] Bgra, int Width, int Height);

    private readonly Dictionary<int, DecodedIcon> _decoded = [];
    private readonly Dictionary<int, ID2D1Bitmap?> _bitmaps = [];

    public ExpeditionRuneIconCache()
    {
        LoadEmbeddedPngs();
    }

    public int Count => _decoded.Count;

    public bool Contains(int runeIndex)
        => _decoded.ContainsKey(runeIndex);

    public ID2D1Bitmap? Get(
        ID2D1RenderTarget renderTarget,
        int runeIndex)
    {
        if (_bitmaps.TryGetValue(runeIndex, out var cached))
        {
            return cached;
        }

        if (!_decoded.TryGetValue(runeIndex, out var decoded))
        {
            _bitmaps[runeIndex] = null;
            return null;
        }

        ID2D1Bitmap? bitmap = null;
        var properties = new BitmapProperties(
            new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied));
        var pinned = GCHandle.Alloc(decoded.Bgra, GCHandleType.Pinned);
        try
        {
            bitmap = renderTarget.CreateBitmap(
                new SizeI(decoded.Width, decoded.Height),
                pinned.AddrOfPinnedObject(),
                (uint)(decoded.Width * 4),
                properties);
        }
        catch
        {
            bitmap = null;
        }
        finally
        {
            pinned.Free();
        }

        _bitmaps[runeIndex] = bitmap;
        return bitmap;
    }

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap?.Dispose();
        }

        _bitmaps.Clear();
    }

    private void LoadEmbeddedPngs()
    {
        try
        {
            using var factory = new IWICImagingFactory();
            var assembly = typeof(ExpeditionRuneIconCache).Assembly;
            const string marker = ".Runeshape.";
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                var markerIndex = resourceName.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex < 0
                    || !resourceName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = resourceName[(markerIndex + marker.Length)..^4];
                var separator = fileName.IndexOf('-');
                if (separator <= 0
                    || !int.TryParse(fileName[..separator], out var runeIndex))
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null && DecodePng(factory, stream) is { } decoded)
                {
                    _decoded[runeIndex] = decoded;
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Expedition rune icons could not be decoded: {exception.Message}");
        }
    }

    private static DecodedIcon? DecodePng(
        IWICImagingFactory factory,
        Stream stream)
    {
        try
        {
            using var decoder = factory.CreateDecoderFromStream(
                stream,
                DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            using var converter = factory.CreateFormatConverter();
            converter.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA);
            converter.GetSize(out var unsignedWidth, out var unsignedHeight);

            var width = (int)unsignedWidth;
            var height = (int)unsignedHeight;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            {
                return null;
            }

            var stride = (uint)(width * 4);
            var pixels = new byte[stride * height];
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                converter.CopyPixels(
                    new RectI(0, 0, width, height),
                    stride,
                    (uint)pixels.Length,
                    pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }

            return new DecodedIcon(pixels, width, height);
        }
        catch
        {
            return null;
        }
    }
}
