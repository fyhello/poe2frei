using System.Globalization;
using System.Collections.Immutable;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Content;

internal sealed record ExpeditionStateEvidenceResult(
    bool Matched,
    bool Resolved,
    int? HoleCount,
    IReadOnlyList<AreaContentEvidence> Evidence,
    ImmutableArray<AreaExpeditionRecipe> Recipes = default);

internal sealed class ExpeditionStateEvidenceReader
{
    private const int MaximumSelectedRecipeIdLength = 128;
    private const string ExpeditionMetadata =
        "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly EntityComponentResolver _components;
    private readonly bool _includeResearchEvidence;
    private readonly ExpeditionRecipeCatalog _recipeCatalog;

    public ExpeditionStateEvidenceReader(
        IProcessMemory memory,
        EntityComponentResolver components,
        Poe2MemoryProfile? profile = null,
        bool includeResearchEvidence = false,
        ExpeditionRecipeCatalog? recipeCatalog = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
        _includeResearchEvidence = includeResearchEvidence;
        _recipeCatalog = recipeCatalog ?? ExpeditionRecipeCatalog.LoadEmbedded();
    }

    public ExpeditionStateEvidenceResult Read(
        RawEntityRef device,
        AreaEntitySnapshot entity,
        int areaLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!string.Equals(
                entity.MetadataPath,
                ExpeditionMetadata,
                StringComparison.OrdinalIgnoreCase))
        {
            return new ExpeditionStateEvidenceResult(false, false, null, []);
        }

        var includeResearchEvidence = _includeResearchEvidence
            && string.Equals(
                entity.MetadataPath,
                ExpeditionMetadata,
                StringComparison.Ordinal);
        var evidence = new List<AreaContentEvidence>();
        if (!_components.TryResolve(device, "StateMachine", out var stateMachine)
            || stateMachine == 0)
        {
            evidence.Add(new AreaContentEvidence(
                "StateMachine",
                "StateMachine.Resolved",
                "false",
                0.2f));
            AppendUnresolvedSelectedRecipePresence(evidence);
            return new ExpeditionStateEvidenceResult(true, false, null, evidence);
        }

        if (includeResearchEvidence)
        {
            AppendResearchScalars(
                evidence,
                "StateMachine",
                stateMachine,
                0x100);
        }

        if (_memory.TryReadInt32(
                stateMachine + _profile.StateMachine.StateOffset,
                out var state))
        {
            evidence.Add(new AreaContentEvidence(
                "StateMachine",
                "StateMachine.State",
                FormatHex(state),
                0.2f));
        }

        if (!_reader.TryReadStdVector(
                stateMachine + _profile.StateMachine.ListenersOffset,
                IntPtr.Size,
                maximumCount: 256,
                out var listeners))
        {
            evidence.Add(new AreaContentEvidence(
                "StateMachine",
                "StateMachine.ListenerVec.Resolved",
                "false",
                0.2f));
            AppendUnresolvedSelectedRecipePresence(evidence);
            return new ExpeditionStateEvidenceResult(true, false, null, evidence);
        }

        evidence.Add(new AreaContentEvidence(
            "StateMachine",
            "StateMachine.ListenerVec.Count",
            FormatHex(listeners.Count),
            0.4f));

        nint station = 0;
        for (var index = 0; index < listeners.Count; index++)
        {
            if (!_reader.TryReadPointer(
                    listeners.First + (index * IntPtr.Size),
                    out var listener)
                || !_reader.TryReadPointer(listener, out var sub))
            {
                continue;
            }

            var candidateValue = sub.ToInt64() - _profile.RuneStation.ListenerSubOffset;
            if (candidateValue <= 0)
            {
                continue;
            }

            var candidate = (nint)candidateValue;
            if (_reader.TryReadPointer(
                    candidate + _profile.RuneStation.OwnerOffset,
                    out var owner)
                && owner == device.EntityAddress)
            {
                station = candidate;
                break;
            }
        }

        if (station == 0)
        {
            evidence.Add(new AreaContentEvidence(
                "RuneStation",
                "RuneStation.Resolved",
                "false",
                0.2f));
            AppendUnresolvedSelectedRecipePresence(evidence);
            return new ExpeditionStateEvidenceResult(true, false, null, evidence);
        }

        if (includeResearchEvidence)
        {
            AppendResearchScalars(
                evidence,
                "RuneStation",
                station,
                0x180);
        }

        AppendSelectedRecipePresence(
            evidence,
            station,
            includeResearchEvidence);

        if (!_memory.TryReadInt32(
                station + _profile.RuneStation.HoleCountOffset,
                out var holeCount)
            || holeCount is <= 0 or > 16)
        {
            evidence.Add(new AreaContentEvidence(
                "RuneStation",
                "RuneStation.Resolved",
                "false",
                0.3f));
            return new ExpeditionStateEvidenceResult(true, false, null, evidence);
        }

        evidence.Add(new AreaContentEvidence(
            "RuneStation",
            "RuneStation.Resolved",
            "true",
            0.9f));
        evidence.Add(new AreaContentEvidence(
            "RuneStation",
            "RuneStation.HoleCount",
            FormatHex(holeCount),
            0.95f));

        if (_memory.TryReadInt32(
                station + _profile.RuneStation.AnchorPositionOffset,
                out var anchorPosition))
        {
            evidence.Add(new AreaContentEvidence(
                "RuneStation",
                "RuneStation.AnchorPos",
                FormatHex(anchorPosition),
                0.8f));
        }

        if (!_reader.TryReadPointer(
                station + _profile.RuneStation.AnchorReferenceOffset,
                out var anchorReference)
            || anchorReference == 0)
        {
            evidence.Add(new AreaContentEvidence(
                "RuneStation",
                "RuneStation.IsUnique",
                "true",
                0.7f));
            evidence.Add(new AreaContentEvidence(
                "RuneStation",
                "RuneStation.AnchorIndex",
                "-1",
                0.5f));
            return new ExpeditionStateEvidenceResult(
                true,
                true,
                holeCount,
                evidence,
                _recipeCatalog.Resolve(-1, -1, holeCount, true, areaLevel));
        }

        evidence.Add(new AreaContentEvidence(
            "RuneStation",
            "RuneStation.IsUnique",
            "false",
            0.7f));
        var anchorIndex = ResolveAnchorIndex(station, anchorReference);
        evidence.Add(new AreaContentEvidence(
            "RuneStation",
            "RuneStation.AnchorIndex",
            anchorIndex.ToString(CultureInfo.InvariantCulture),
            anchorIndex >= 0 ? 0.9f : 0.3f));
        return new ExpeditionStateEvidenceResult(
            true,
            true,
            holeCount,
            evidence,
            _recipeCatalog.Resolve(
                anchorIndex,
                anchorPosition,
                holeCount,
                false,
                areaLevel));
    }

    private void AppendResearchScalars(
        List<AreaContentEvidence> evidence,
        string componentName,
        nint component,
        int maximumOffset)
    {
        for (var offset = 0; offset <= maximumOffset; offset += sizeof(int))
        {
            if (IsSelectedRecipePointerLane(componentName, offset)
                || !_memory.TryReadInt32(component + offset, out var value)
                || value is < -1 or > 4096
                || IsReadablePointerLane(component, offset))
            {
                continue;
            }

            evidence.Add(new AreaContentEvidence(
                componentName,
                $"Research.{componentName}.+0x{offset:X2}",
                value.ToString(CultureInfo.InvariantCulture),
                0.1f));
        }
    }

    private void AppendUnresolvedSelectedRecipePresence(
        List<AreaContentEvidence> evidence)
        => evidence.Add(new AreaContentEvidence(
            "RuneStation",
            ExpeditionEvidenceKeys.SelectedRecipePresence,
            "unresolved",
            0.2f));

    private void AppendSelectedRecipePresence(
        List<AreaContentEvidence> evidence,
        nint station,
        bool includeResearchEvidence)
    {
        string value;
        if (!_memory.TryReadPointer(
                station + _profile.RuneStation.SelectedRecipeOffset,
                out var selectedRecipe))
        {
            value = "unresolved";
        }
        else
        {
            value = selectedRecipe == 0
                ? "false"
                : TryReadSelectedRecipeId(selectedRecipe)
                    ? "true"
                    : "unresolved";
        }

        evidence.Add(new AreaContentEvidence(
            "RuneStation",
            ExpeditionEvidenceKeys.SelectedRecipePresence,
            value,
            value == "unresolved" ? 0.2f : 0.95f));
        if (includeResearchEvidence)
        {
            evidence.Add(new AreaContentEvidence(
                "RuneStation",
                ExpeditionEvidenceKeys.ResearchSelectedRecipePresence,
                value,
                0.1f));
        }
    }

    private bool TryReadSelectedRecipeId(nint selectedRecipe)
        => _reader.TryReadPointer(selectedRecipe, out var idAddress)
           && _memory.TryReadUtf16(
               idAddress,
               MaximumSelectedRecipeIdLength,
               out var id)
           && !string.IsNullOrWhiteSpace(id)
           && id.All(character => !char.IsControl(character));

    private bool IsReadablePointerLane(nint component, int offset)
        => offset % sizeof(long) == 0
            ? IsReadablePointer(component + offset)
            : offset % sizeof(long) == sizeof(int)
              && IsReadablePointer(component + offset - sizeof(int));

    private bool IsSelectedRecipePointerLane(
        string componentName,
        int offset)
        => string.Equals(componentName, "RuneStation", StringComparison.Ordinal)
           && offset >= _profile.RuneStation.SelectedRecipeOffset
           && offset < _profile.RuneStation.SelectedRecipeOffset + IntPtr.Size;

    private bool IsReadablePointer(nint address)
    {
        if (!_reader.TryReadPointer(address, out var pointer))
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[1];
        return _memory.TryRead(pointer, probe);
    }

    private int ResolveAnchorIndex(nint station, nint anchorReference)
    {
        if (!_reader.TryReadPointer(
                station + _profile.RuneStation.AnchorHolderOffset,
                out var holder)
            || holder == 0
            || !_reader.TryReadPointer(
                holder + _profile.RuneStation.RuneTablePointerOffset,
                out var tablePointer)
            || tablePointer == 0
            || !_reader.TryReadPointer(tablePointer, out var tableBase)
            || tableBase == 0)
        {
            return -1;
        }

        var delta = anchorReference.ToInt64() - tableBase.ToInt64();
        var stride = _profile.RuneStation.RuneStride;
        if (delta < 0 || stride <= 0 || delta % stride != 0)
        {
            return -1;
        }

        var index = delta / stride;
        return index >= 0 && index < _profile.RuneStation.RuneCount
            ? (int)index
            : -1;
    }

    private static string FormatHex(int value)
        => $"0x{value:X8}";
}
