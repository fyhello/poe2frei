using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreiAtlas.Patcher;

/// <summary>
/// 价格补丁的干净层备份信息。价格层只备份 BaseItemTypes.datc64，不触碰整份索引。
/// </summary>
public sealed record PriceLayerBaseline(
    byte[] Bytes,
    string BaselinePath,
    string ManifestPath,
    PriceLayerManifest Manifest);

/// <summary>价格层备份清单。</summary>
public sealed class PriceLayerManifest
{
    public string GameDirectory { get; set; } = string.Empty;
    public string VirtualPath { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public int RowSize { get; set; }
    public string MetadataSha256 { get; set; } = string.Empty;
    public string BaselineSha256 { get; set; } = string.Empty;
    public string? PatchedSha256 { get; set; }
    public int AppliedCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// 价格层独立备份存储。首次写入前保存干净 dat，应用/清理时用于避免重复叠加和精确还原。
/// </summary>
public sealed class PriceLayerBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _root;

    public PriceLayerBackupStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreiAtlas",
            "price_patch");
    }

    public string Root => _root;

    private static bool IsWordsFile(string virtualPath) =>
        virtualPath.Contains("words", StringComparison.OrdinalIgnoreCase);

    private static PricePatchService.DatLayout DetectLayout(string virtualPath, byte[] bytes) =>
        IsWordsFile(virtualPath)
            ? PricePatchService.DetectWordsLayout(bytes)
            : PricePatchService.DetectBaseItemLayout(bytes);

    private static string ComputeSignature(string virtualPath, byte[] bytes) =>
        IsWordsFile(virtualPath)
            ? PricePatchService.ComputeWordsMetadataSignature(bytes)
            : PricePatchService.ComputeMetadataSignature(bytes);

    private static bool ContainsSuffix(string virtualPath, byte[] bytes) =>
        IsWordsFile(virtualPath)
            ? PricePatchService.ContainsWordsPriceSuffix(bytes)
            : PricePatchService.ContainsPriceSuffix(bytes);

    private static byte[] StripSuffixes(string virtualPath, byte[] bytes) =>
        IsWordsFile(virtualPath)
            ? PricePatchService.StripAllWordsPriceSuffixes(bytes)
            : PricePatchService.StripAllPriceSuffixes(bytes);

    public PriceLayerBaseline EnsureBaseline(
        GameModeInfo info,
        string virtualPath,
        byte[] currentBytes,
        out string? warning)
    {
        Directory.CreateDirectory(_root);
        string key = ComputeKey(info.GameDirectory, virtualPath);
        string baselinePath = Path.Combine(_root, $"baseline_{key}.datc64");
        string manifestPath = Path.Combine(_root, $"baseline_{key}.json");
        var currentLayout = DetectLayout(virtualPath, currentBytes);
        string currentMetadataSignature = ComputeSignature(virtualPath, currentBytes);
        warning = null;

        if (TryLoad(baselinePath, manifestPath, out var existingBytes, out var existingManifest))
        {
            if (existingManifest.RowCount == currentLayout.RowCount
                && existingManifest.RowSize == currentLayout.RowSize
                && string.Equals(existingManifest.MetadataSha256, currentMetadataSignature, StringComparison.Ordinal)
                && Sha256(existingBytes) == existingManifest.BaselineSha256)
            {
                return new PriceLayerBaseline(existingBytes, baselinePath, manifestPath, existingManifest);
            }

            warning = "价格层备份与当前游戏数据表不一致，已重新建立备份。";
        }
        else if (File.Exists(baselinePath) || File.Exists(manifestPath))
        {
            warning = "价格层备份不完整，已重新建立备份。";
        }

        byte[] baselineBytes = currentBytes;
        if (ContainsSuffix(virtualPath, currentBytes))
        {
            baselineBytes = StripSuffixes(virtualPath, currentBytes);
            warning ??= "检测到当前数据表已包含价格后缀，已先剥离旧后缀并建立干净基线。";
        }

        var layout = DetectLayout(virtualPath, baselineBytes);
        var manifest = new PriceLayerManifest
        {
            GameDirectory = info.GameDirectory,
            VirtualPath = virtualPath,
            RowCount = layout.RowCount,
            RowSize = layout.RowSize,
            MetadataSha256 = ComputeSignature(virtualPath, baselineBytes),
            BaselineSha256 = Sha256(baselineBytes),
            PatchedSha256 = null,
            AppliedCount = 0,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        WriteAtomic(baselinePath, baselineBytes);
        WriteManifest(manifestPath, manifest);
        return new PriceLayerBaseline(baselineBytes, baselinePath, manifestPath, manifest);
    }

    public bool TryGetBaseline(
        GameModeInfo info,
        string virtualPath,
        byte[] currentBytes,
        out PriceLayerBaseline baseline)
    {
        string key = ComputeKey(info.GameDirectory, virtualPath);
        string baselinePath = Path.Combine(_root, $"baseline_{key}.datc64");
        string manifestPath = Path.Combine(_root, $"baseline_{key}.json");
        baseline = default!;

        if (!TryLoad(baselinePath, manifestPath, out var bytes, out var manifest)
            || Sha256(bytes) != manifest.BaselineSha256)
        {
            return false;
        }

        var currentLayout = DetectLayout(virtualPath, currentBytes);
        string currentMetadataSignature = ComputeSignature(virtualPath, currentBytes);
        if (manifest.RowCount != currentLayout.RowCount
            || manifest.RowSize != currentLayout.RowSize
            || !string.Equals(manifest.MetadataSha256, currentMetadataSignature, StringComparison.Ordinal))
        {
            return false;
        }

        baseline = new PriceLayerBaseline(bytes, baselinePath, manifestPath, manifest);
        return true;
    }

    public void RecordApplied(PriceLayerBaseline baseline, byte[] patchedBytes, int appliedCount)
    {
        baseline.Manifest.PatchedSha256 = Sha256(patchedBytes);
        baseline.Manifest.AppliedCount = appliedCount;
        baseline.Manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
        WriteManifest(baseline.ManifestPath, baseline.Manifest);
    }

    public void RecordCleaned(PriceLayerBaseline baseline)
    {
        baseline.Manifest.PatchedSha256 = baseline.Manifest.BaselineSha256;
        baseline.Manifest.AppliedCount = 0;
        baseline.Manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
        WriteManifest(baseline.ManifestPath, baseline.Manifest);
    }

    private static bool TryLoad(
        string baselinePath,
        string manifestPath,
        out byte[] bytes,
        out PriceLayerManifest manifest)
    {
        bytes = [];
        manifest = null!;
        if (!File.Exists(baselinePath) || !File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<PriceLayerManifest>(
                File.ReadAllText(manifestPath, Encoding.UTF8),
                JsonOptions);
            if (loaded is null)
            {
                return false;
            }

            bytes = File.ReadAllBytes(baselinePath);
            manifest = loaded;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void WriteManifest(string path, PriceLayerManifest manifest)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string ComputeKey(string gameDirectory, string virtualPath)
    {
        string normalized = Path.GetFullPath(gameDirectory)
            .TrimEnd('\\', '/')
            .ToLowerInvariant() + "|" + virtualPath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));
}
