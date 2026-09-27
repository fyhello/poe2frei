using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Content;

/// <summary>
/// Captures raw, read-only evidence for Azmeri wisp entities so their concrete
/// game-state discriminator can be verified against the current client build.
/// </summary>
public sealed class PollenResearchProbe
{
    private const int MaximumMetadataLength = 1024;
    private const int MaximumComponents = 256;
    private const int MaximumComponentNameBytes = 32;
    private const int LimitedLifespanDumpLength = 0xB0;
    private const int AnimatedModelInfoOffset = 0x358;
    private const int ModelInfoFileRecordOffset = 0x18;
    private const int FileRecordNameOffset = 0x08;

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemorySession _session;
    private readonly AreaEntityTreeReader _entityTree;
    private readonly GameMemoryReader _reader;

    public PollenResearchProbe(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _session = new GameMemorySession(
            memory,
            _profile,
            new GameStateLocator(_profile));
        _entityTree = new AreaEntityTreeReader(memory, _profile);
        _reader = new GameMemoryReader(memory, _profile);
    }

    public PollenResearchResult Capture()
    {
        if (!_session.TryRefresh(out var root, out var area, out var diagnostics))
        {
            return new PollenResearchResult(
                _memory.ProcessId,
                _profile.ProfileId,
                null,
                [],
                diagnostics);
        }

        var messages = diagnostics.ToList();
        if (!_entityTree.TryRead(root.AreaInstance, out var entities, out var treeDiagnostic))
        {
            if (treeDiagnostic is not null)
            {
                messages.Add(treeDiagnostic);
            }

            return new PollenResearchResult(
                _memory.ProcessId,
                _profile.ProfileId,
                area,
                [],
                messages);
        }

        var pollen = new List<PollenResearchEntity>();
        foreach (var entity in entities)
        {
            if (!TryReadMetadata(entity, out var metadata)
                || !IsResearchCandidate(metadata))
            {
                continue;
            }

            pollen.Add(ReadPollen(entity, metadata));
        }


        return new PollenResearchResult(
            _memory.ProcessId,
            _profile.ProfileId,
            area,
            pollen.OrderBy(item => item.EntityId).ToArray(),
            messages);
    }

    private bool TryReadMetadata(RawEntityRef entity, out string metadata)
    {
        metadata = string.Empty;
        return _reader.TryReadPointer(
                   entity.EntityAddress + _profile.Entity.DetailsOffset,
                   out var details)
               && _reader.TryReadStdWString(
                   details + _profile.EntityDetails.NameOffset,
                   MaximumMetadataLength,
                   out metadata);
    }

    private PollenResearchEntity ReadPollen(RawEntityRef entity, string metadata)
    {
        var components = ReadComponents(
            entity,
            out var limitedLifespan,
            out var animated,
            out var render);
        var animatedModelPath = ReadAnimatedModelPath(animated);
        if (limitedLifespan == 0)
        {
            return new PollenResearchEntity(
                entity.EntityId,
                entity.EntityAddress.ToInt64(),
                metadata,
                components,
                animatedModelPath,
                ReadReferencedPaths(render),
                [],
                null,
                null,
                null,
                null);
        }

        long? kindSignature = _memory.TryReadPointer(
            limitedLifespan + PollenStateEvidenceReader.KindSignatureOffset,
            out var signature)
            ? signature.ToInt64()
            : null;
        uint? kindFlag = _reader.TryReadUInt32(
            limitedLifespan + PollenStateEvidenceReader.KindFlagOffset,
            out var flag)
            ? flag
            : null;
        var bytes = new byte[LimitedLifespanDumpLength];
        var dump = _memory.TryRead(limitedLifespan, bytes)
            ? Convert.ToHexString(bytes)
            : null;

        return new PollenResearchEntity(
            entity.EntityId,
            entity.EntityAddress.ToInt64(),
            metadata,
            components,
            animatedModelPath,
            ReadReferencedPaths(render),
            ReadReferencedPaths((nint)(kindSignature ?? 0)),
            limitedLifespan.ToInt64(),
            kindSignature,
            kindFlag,
            dump);
    }

    private IReadOnlyList<string> ReadComponents(
        RawEntityRef entity,
        out nint limitedLifespan,
        out nint animated,
        out nint render)
    {
        limitedLifespan = 0;
        animated = 0;
        render = 0;
        if (!_reader.TryReadPointer(
                entity.EntityAddress + _profile.Entity.DetailsOffset,
                out var details)
            || !_reader.TryReadPointer(
                details + _profile.EntityDetails.ComponentLookupOffset,
                out var lookup)
            || !_reader.TryReadStdVector(
                entity.EntityAddress + _profile.Entity.ComponentsOffset,
                IntPtr.Size,
                MaximumComponents,
                out var componentPointers)
            || !_reader.TryReadStdVector(
                lookup + _profile.ComponentLookup.BucketOffset,
                _profile.ComponentLookup.EntryStride,
                MaximumComponents,
                out var entries))
        {
            return [];
        }

        var result = new List<string>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries.First + (index * _profile.ComponentLookup.EntryStride);
            if (!_reader.TryReadPointer(entry, out var nameAddress)
                || !_reader.TryReadUInt32(entry + IntPtr.Size, out var rawComponentIndex)
                || rawComponentIndex >= componentPointers.Count
                || !_reader.TryReadUtf8(
                    nameAddress,
                    MaximumComponentNameBytes,
                    out var name)
                || !_reader.TryReadPointer(
                    componentPointers.First + ((int)rawComponentIndex * IntPtr.Size),
                    out var component))
            {
                continue;
            }

            result.Add(name);
            switch (name)
            {
                case PollenStateEvidenceReader.LimitedLifespanComponent:
                    limitedLifespan = component;
                    break;
                case "Animated":
                    animated = component;
                    break;
                case "Render":
                    render = component;
                    break;
            }
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private string? ReadAnimatedModelPath(nint animated)
    {
        if (animated == 0
            || !_reader.TryReadPointer(
                animated + AnimatedModelInfoOffset,
                out var modelInfo)
            || !_reader.TryReadPointer(
                modelInfo + ModelInfoFileRecordOffset,
                out var fileRecord)
            || !_reader.TryReadStdWString(
                fileRecord + FileRecordNameOffset,
                MaximumMetadataLength,
                out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var at = path.IndexOf('@');
        return at >= 0 ? path[..at] : path;
    }

    private IReadOnlyList<string> ReadReferencedPaths(nint address)
    {
        if (address == 0)
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 0x200; offset += IntPtr.Size)
        {
            if (!_reader.TryReadPointer(address + offset, out var reference))
            {
                continue;
            }

            if (_reader.TryReadUtf8(reference, 512, out var utf8)
                && IsResourcePath(utf8))
            {
                paths.Add(utf8);
            }

            if (_memory.TryReadUtf16(reference, 512, out var utf16)
                && IsResourcePath(utf16))
            {
                paths.Add(utf16!);
            }
        }

        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsResourcePath(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length is > 3 and <= 512
           && (value.Contains("Metadata/", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Art/", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Azmeri", StringComparison.OrdinalIgnoreCase));

    private static bool IsResearchCandidate(string metadata)
        => metadata.Contains(
               PollenStateEvidenceReader.PollenMetadata,
               StringComparison.OrdinalIgnoreCase)
           || metadata.Contains(
               "Metadata/Monsters/TormentedSpirits/",
               StringComparison.OrdinalIgnoreCase);
}

public sealed record PollenResearchResult(
    int ProcessId,
    string ProfileId,
    AreaIdentity? Area,
    IReadOnlyList<PollenResearchEntity> Entities,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics);

public sealed record PollenResearchEntity(
    uint EntityId,
    long EntityAddress,
    string MetadataPath,
    IReadOnlyList<string> Components,
    string? AnimatedModelPath,
    IReadOnlyList<string> RenderPaths,
    IReadOnlyList<string> SignaturePaths,
    long? LimitedLifespanAddress,
    long? KindSignature,
    uint? KindFlag,
    string? LimitedLifespanHex)
{
    public string FormatSummary()
        => $"entity:{EntityId};address:0x{EntityAddress:X};"
           + $"lifespan:{FormatHex(LimitedLifespanAddress)};"
           + $"signature:{FormatHex(KindSignature)};flag:{FormatHex(KindFlag)}";

    private static string FormatHex(long? value)
        => value is { } raw ? $"0x{raw:X}" : "unavailable";

    private static string FormatHex(uint? value)
        => value is { } raw ? $"0x{raw:X8}" : "unavailable";
}
