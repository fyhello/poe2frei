using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Content;

internal sealed record PollenStateEvidenceResult(
    bool Matched,
    AreaPollenDetails Details,
    IReadOnlyList<AreaContentEvidence> Evidence);

/// <summary>
/// Classifies Azmeri wisp ground objects from the model resource loaded by the
/// entity's <c>Animated</c> component. The resource name is part of the
/// entity's visual state and remains meaningful regardless of entity traversal
/// order or transient object addresses.
/// </summary>
internal sealed class PollenStateEvidenceReader
{
    internal const string PollenMetadata =
        "Metadata/MiscellaneousObjects/Azmeri/AzmeriResourceBase";
    internal const string AnimatedComponent = "Animated";
    internal const string LimitedLifespanComponent = "LimitedLifespan";
    internal const string EvidenceSource = "Pollen";
    internal const string ModelPathKey = "ModelPath";
    internal const string ModelKey = "ModelKey";
    internal const string ModelLayoutKey = "ModelLayout";
    internal const string ModelLayoutSourceKey = "ModelLayoutSource";

    // Retained for the research probe only. These fields are explicitly not
    // classification inputs: the pointer differs by load state and the flag
    // does not encode the model kind.
    internal const string KindSignatureKey = "KindSignature";
    internal const string KindFlagKey = "KindFlag";
    internal const int KindSignatureOffset = 0x88;
    internal const int KindFlagOffset = 0xA0;

    private const int MaximumMetadataLength = 1024;
    private const int ModelInfoSearchLimit = 0x600;
    private const int FileRecordSearchLimit = 0x80;
    private const int FileNameSearchLimit = 0x40;

    private static readonly PollenModelLayout ConfiguredModelLayout = new(
        AnimatedModelInfoOffset: 0x358,
        ModelInfoFileRecordOffset: 0x18,
        FileRecordNameOffset: 0x08);

    private readonly GameMemoryReader _reader;
    private readonly EntityComponentResolver _components;
    private PollenModelLayout? _autoDiscoveredModelLayout;
    private long _currentSessionSequence = long.MinValue;
    private long _lastAutoDiscoveryAttemptSession = long.MinValue;

    public PollenStateEvidenceReader(
        IProcessMemory memory,
        EntityComponentResolver components,
        Poe2MemoryProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _reader = new GameMemoryReader(memory, profile);
    }

    public void BeginSample(long sessionSequence)
    {
        _currentSessionSequence = sessionSequence;
    }

    public void Reset()
    {
        _currentSessionSequence = long.MinValue;
        _lastAutoDiscoveryAttemptSession = long.MinValue;
    }

    public PollenStateEvidenceResult Read(
        RawEntityRef rawEntity,
        AreaEntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!IsPollenMetadata(entity.MetadataPath))
        {
            return new PollenStateEvidenceResult(
                false,
                new AreaPollenDetails(AreaPollenKind.Unknown),
                []);
        }

        try
        {
            if (!_components.TryResolve(rawEntity, AnimatedComponent, out var animated)
                || animated == 0
                || !TryReadModelPath(
                    animated,
                    out var modelPath,
                    out var layout,
                    out var layoutSource))
            {
                return UnknownMatched();
            }

            var kind = MapKind(modelPath, out var modelKey);
            var evidence = new AreaContentEvidence[]
            {
                new(EvidenceSource, ModelPathKey, modelPath, 1f),
                new(EvidenceSource, ModelKey, modelKey, 1f),
                new(EvidenceSource, ModelLayoutKey, layout.Format(), 0.9f),
                new(EvidenceSource, ModelLayoutSourceKey, layoutSource, 0.9f)
            };
            return new PollenStateEvidenceResult(
                true,
                new AreaPollenDetails(kind),
                evidence);
        }
        catch
        {
            return UnknownMatched();
        }
    }

    private PollenStateEvidenceResult UnknownMatched()
        => new(
            true,
            new AreaPollenDetails(AreaPollenKind.Unknown),
            []);

    private bool TryReadModelPath(
        nint animated,
        out string modelPath,
        out PollenModelLayout layout,
        out string layoutSource)
    {
        if (TryReadModelPath(
                animated,
                ConfiguredModelLayout,
                out modelPath))
        {
            layout = ConfiguredModelLayout;
            layoutSource = "configured";
            return true;
        }

        if (_autoDiscoveredModelLayout is { } cached
            && TryReadModelPath(animated, cached, out modelPath))
        {
            layout = cached;
            layoutSource = "auto-cached";
            return true;
        }

        if (_currentSessionSequence != long.MinValue
            && _lastAutoDiscoveryAttemptSession == _currentSessionSequence)
        {
            modelPath = string.Empty;
            layout = default;
            layoutSource = "unavailable";
            return false;
        }

        _lastAutoDiscoveryAttemptSession = _currentSessionSequence;
        foreach (var candidate in EnumerateModelLayouts())
        {
            if (candidate == ConfiguredModelLayout
                || candidate == _autoDiscoveredModelLayout
                || !TryReadModelPath(animated, candidate, out modelPath))
            {
                continue;
            }

            _autoDiscoveredModelLayout = candidate;
            layout = candidate;
            layoutSource = "auto-discovered";
            return true;
        }

        modelPath = string.Empty;
        layout = default;
        layoutSource = "unavailable";
        return false;
    }

    private bool TryReadModelPath(
        nint animated,
        PollenModelLayout layout,
        out string modelPath)
    {
        modelPath = string.Empty;
        return _reader.TryReadPointer(
                   animated + layout.AnimatedModelInfoOffset,
                   out var modelInfo)
               && _reader.TryReadPointer(
                   modelInfo + layout.ModelInfoFileRecordOffset,
                   out var fileRecord)
               && _reader.TryReadStdWString(
                   fileRecord + layout.FileRecordNameOffset,
                   MaximumMetadataLength,
                   out var rawPath)
               && TryNormalizeWispModelPath(rawPath, out modelPath);
    }

    private static IEnumerable<PollenModelLayout> EnumerateModelLayouts()
    {
        for (var animatedOffset = IntPtr.Size;
             animatedOffset <= ModelInfoSearchLimit;
             animatedOffset += IntPtr.Size)
        {
            for (var fileRecordOffset = 0;
                 fileRecordOffset <= FileRecordSearchLimit;
                 fileRecordOffset += IntPtr.Size)
            {
                for (var fileNameOffset = 0;
                     fileNameOffset <= FileNameSearchLimit;
                     fileNameOffset += IntPtr.Size)
                {
                    yield return new PollenModelLayout(
                        animatedOffset,
                        fileRecordOffset,
                        fileNameOffset);
                }
            }
        }
    }

    private static bool TryNormalizeWispModelPath(string rawPath, out string modelPath)
    {
        var at = rawPath.IndexOf('@');
        modelPath = at >= 0 ? rawPath[..at] : rawPath;
        return modelPath.StartsWith(
                   "Metadata/Effects/Spells/monsters_effects/League_Azmeri/"
                   + "resources/wisp_doodads/wisp_",
                   StringComparison.OrdinalIgnoreCase)
               && modelPath.EndsWith(".ao", StringComparison.OrdinalIgnoreCase);
    }

    private static AreaPollenKind MapKind(string modelPath, out string modelKey)
    {
        if (modelPath.Contains("/wisp_warden_", StringComparison.OrdinalIgnoreCase))
        {
            modelKey = "warden";
            return AreaPollenKind.Wild;
        }

        if (modelPath.Contains("/wisp_vodoo_", StringComparison.OrdinalIgnoreCase)
            || modelPath.Contains("/wisp_voodoo_", StringComparison.OrdinalIgnoreCase))
        {
            modelKey = "vodoo";
            return AreaPollenKind.Soul;
        }

        if (modelPath.Contains("/wisp_primal_", StringComparison.OrdinalIgnoreCase))
        {
            modelKey = "primal";
            return AreaPollenKind.Primal;
        }

        if (modelPath.Contains("/wisp_sacred_", StringComparison.OrdinalIgnoreCase))
        {
            modelKey = "sacred";
            return AreaPollenKind.Sacred;
        }

        modelKey = "unknown";
        return AreaPollenKind.Unknown;
    }

    private static bool IsPollenMetadata(string metadata)
        => metadata.Contains(PollenMetadata, StringComparison.OrdinalIgnoreCase);

    private readonly record struct PollenModelLayout(
        int AnimatedModelInfoOffset,
        int ModelInfoFileRecordOffset,
        int FileRecordNameOffset)
    {
        public string Format()
            => $"animated+0x{AnimatedModelInfoOffset:X}/modelInfo+0x{ModelInfoFileRecordOffset:X}/fileRecord+0x{FileRecordNameOffset:X}";
    }
}
