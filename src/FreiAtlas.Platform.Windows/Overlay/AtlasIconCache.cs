using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FreiAtlas.Platform.Windows.Overlay;

internal enum AtlasIconVariant
{
    Original,
    Grayscale
}

internal sealed class AtlasIconCache : IDisposable
{
    private readonly record struct DecodedIcon(byte[] Bgra, int Width, int Height);

    private readonly Dictionary<string, Dictionary<AtlasIconVariant, DecodedIcon>> _decoded =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<AtlasIconVariant, ID2D1Bitmap?>> _bitmaps =
        new(StringComparer.OrdinalIgnoreCase);

    public AtlasIconCache()
    {
        LoadEmbeddedPngs();
    }

    public int Count => _decoded.Count;

    internal int DecodedVariantCount => _decoded.Values.Sum(variants => variants.Count);

    public ID2D1Bitmap? Get(
        ID2D1RenderTarget renderTarget,
        string? referenceIconId,
        AtlasIconVariant variant = AtlasIconVariant.Original)
    {
        if (string.IsNullOrWhiteSpace(referenceIconId))
        {
            return null;
        }

        if (_bitmaps.TryGetValue(referenceIconId, out var cachedVariants)
            && cachedVariants.TryGetValue(variant, out var cached))
        {
            return cached;
        }

        if (!_decoded.TryGetValue(referenceIconId, out var decodedVariants)
            || !decodedVariants.TryGetValue(variant, out var decoded))
        {
            CacheBitmap(referenceIconId, variant, null);
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

        CacheBitmap(referenceIconId, variant, bitmap);
        return bitmap;
    }

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps.Values.SelectMany(variants => variants.Values))
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
            var assembly = typeof(AtlasIconCache).Assembly;
            const string marker = ".AtlasIcons.";

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                var markerIndex = resourceName.IndexOf(
                    marker,
                    StringComparison.Ordinal);
                if (markerIndex < 0
                    || !resourceName.EndsWith(
                        ".png",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var referenceIconId = resourceName[
                    (markerIndex + marker.Length)..^4];
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null
                    && DecodePng(factory, stream) is { } decoded)
                {
                    _decoded[referenceIconId] = new Dictionary<AtlasIconVariant, DecodedIcon>
                    {
                        [AtlasIconVariant.Original] = decoded,
                        [AtlasIconVariant.Grayscale] = decoded with
                        {
                            Bgra = CreateGrayscalePremultipliedBgra(decoded.Bgra)
                        }
                    };
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Atlas icon resources could not be decoded: {exception.Message}");
        }
    }

    internal static byte[] CreateGrayscalePremultipliedBgra(
        ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException(
                "Premultiplied BGRA pixels must contain complete four-byte pixels.",
                nameof(pixels));
        }

        var grayscale = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            var alpha = pixels[index + 3];
            var luminance = (red * 77 + green * 150 + blue * 29 + 128) >> 8;
            grayscale[index] = (byte)luminance;
            grayscale[index + 1] = (byte)luminance;
            grayscale[index + 2] = (byte)luminance;
            grayscale[index + 3] = alpha;
        }

        return grayscale;
    }

    private void CacheBitmap(
        string referenceIconId,
        AtlasIconVariant variant,
        ID2D1Bitmap? bitmap)
    {
        if (!_bitmaps.TryGetValue(referenceIconId, out var variants))
        {
            variants = [];
            _bitmaps[referenceIconId] = variants;
        }

        variants[variant] = bitmap;
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
