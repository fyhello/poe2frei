using System.Runtime.InteropServices;
using System.Text.Json;
using FreiAtlas.Core.Area;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FreiAtlas.Platform.Windows.Overlay;

internal sealed class ExpeditionRewardIconCache : IDisposable
{
    private const string ResourceMarker = ".ExpeditionRewards.";
    private const string ManifestSuffix = ".ExpeditionRewards.manifest.json";

    private readonly record struct DecodedIcon(byte[] Bgra, int Width, int Height);

    private readonly Dictionary<string, string> _exactItems;
    private readonly Dictionary<string, string> _descriptiveRewards;
    private readonly Dictionary<string, string> _resourceNames;
    private readonly Dictionary<string, DecodedIcon> _decoded = [];
    private readonly HashSet<string> _decodeFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ID2D1Bitmap?> _bitmaps = [];

    public ExpeditionRewardIconCache()
    {
        var assembly = typeof(ExpeditionRewardIconCache).Assembly;
        _resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal)
                           && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                name => name[(name.IndexOf(ResourceMarker, StringComparison.Ordinal)
                    + ResourceMarker.Length)..],
                name => name,
                StringComparer.Ordinal);

        var manifestName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ManifestSuffix, StringComparison.Ordinal));
        if (manifestName is null)
        {
            _exactItems = new(StringComparer.Ordinal);
            _descriptiveRewards = new(StringComparer.Ordinal);
            return;
        }

        using var stream = assembly.GetManifestResourceStream(manifestName);
        var manifest = stream is null
            ? null
            : JsonSerializer.Deserialize<Manifest>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        _exactItems = manifest?.ExactItems is { } exact
            ? new Dictionary<string, string>(exact, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        _descriptiveRewards = manifest?.DescriptiveRewards is { } descriptive
            ? new Dictionary<string, string>(descriptive, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public int ExactItemCount => _exactItems.Count;

    public int DescriptiveRewardCount => _descriptiveRewards.Count;

    public int UniqueResourceCount => _exactItems.Values
        .Concat(_descriptiveRewards.Values)
        .Distinct(StringComparer.Ordinal)
        .Count();

    public bool TryResolve(
        string itemId,
        string displayName,
        bool isExactItem,
        out string resourceFile)
    {
        var source = isExactItem ? _exactItems : _descriptiveRewards;
        var key = isExactItem ? itemId : displayName;
        if (!string.IsNullOrWhiteSpace(key)
            && source.TryGetValue(key, out var resolved)
            && _resourceNames.ContainsKey(resolved))
        {
            resourceFile = resolved;
            return true;
        }

        resourceFile = string.Empty;
        return false;
    }

    public ID2D1Bitmap? Get(
        ID2D1RenderTarget renderTarget,
        AreaExpeditionReward reward)
    {
        ArgumentNullException.ThrowIfNull(renderTarget);
        ArgumentNullException.ThrowIfNull(reward);
        if (!TryResolve(
                reward.ItemId,
                reward.DisplayName,
                reward.IsExactItem,
                out var resourceFile))
        {
            return null;
        }

        if (_bitmaps.TryGetValue(resourceFile, out var cached))
        {
            return cached;
        }

        if (!TryDecode(resourceFile, out var decoded))
        {
            _bitmaps[resourceFile] = null;
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

        _bitmaps[resourceFile] = bitmap;
        return bitmap;
    }

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap?.Dispose();
        }

        _bitmaps.Clear();
        _decoded.Clear();
        _decodeFailures.Clear();
    }

    private bool TryDecode(string resourceFile, out DecodedIcon decoded)
    {
        if (_decoded.TryGetValue(resourceFile, out decoded))
        {
            return true;
        }
        if (_decodeFailures.Contains(resourceFile)
            || !_resourceNames.TryGetValue(resourceFile, out var resourceName))
        {
            return false;
        }

        try
        {
            using var stream = typeof(ExpeditionRewardIconCache).Assembly
                .GetManifestResourceStream(resourceName);
            using var factory = new IWICImagingFactory();
            if (stream is not null && DecodePng(factory, stream) is { } icon)
            {
                decoded = icon;
                _decoded[resourceFile] = icon;
                return true;
            }
        }
        catch
        {
        }

        _decodeFailures.Add(resourceFile);
        return false;
    }

    private static DecodedIcon? DecodePng(
        IWICImagingFactory factory,
        Stream stream)
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

    private sealed class Manifest
    {
        public Dictionary<string, string>? ExactItems { get; set; }

        public Dictionary<string, string>? DescriptiveRewards { get; set; }
    }
}
