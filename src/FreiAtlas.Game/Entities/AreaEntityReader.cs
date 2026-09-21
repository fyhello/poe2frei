using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Entities;

internal sealed record AreaEntityReadResult(
    IReadOnlyList<AreaEntitySnapshot> Entities,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics);

internal sealed class AreaEntityReader
{
    private const int MaximumMetadataLength = 1024;
    private const int MaximumModifiers = 128;
    private const int MaximumModifierIdLength = 64;

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly EntityComponentResolver _components;
    private readonly EntityNameCatalog _names;

    public AreaEntityReader(
        IProcessMemory memory,
        EntityComponentResolver components,
        EntityNameCatalog names,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
    }

    public AreaEntityReadResult Read(
        long sessionSequence,
        IReadOnlyList<RawEntityRef> entityRefs)
    {
        ArgumentNullException.ThrowIfNull(entityRefs);
        _components.BeginSample(sessionSequence);
        var entities = new List<AreaEntitySnapshot>(entityRefs.Count);
        var diagnostics = new List<AreaReadDiagnostic>();
        var logicalEntities = new HashSet<LogicalEntityIdentity>(
            LogicalEntityIdentityComparer.Instance);
        if (!_names.IsAvailable)
        {
            diagnostics.Add(new AreaReadDiagnostic(
                "entity-name-catalog-unavailable",
                "The embedded entity name catalog is unavailable; metadata short names are in use.",
                AreaDiagnosticSeverity.Warning));
        }

        foreach (var entityRef in entityRefs)
        {
            var outcome = TryReadEntity(entityRef, out var entity);
            if (outcome == EntityReadOutcome.Success)
            {
                if (TryCreateLogicalIdentity(
                        entityRef,
                        entity.MetadataPath,
                        out var identity)
                    && !logicalEntities.Add(identity))
                {
                    diagnostics.Add(new AreaReadDiagnostic(
                        "entity-logical-duplicate",
                        "A duplicate wrapper for the same logical entity was skipped.",
                        AreaDiagnosticSeverity.Info,
                        entityRef.EntityId));
                    continue;
                }

                entities.Add(entity);
                continue;
            }

            if (outcome == EntityReadOutcome.Unpositioned)
            {
                diagnostics.Add(new AreaReadDiagnostic(
                    "entity-unpositioned",
                    "The entity has no Render component and was skipped because it has no world position.",
                    AreaDiagnosticSeverity.Info,
                    entityRef.EntityId));
                continue;
            }

            diagnostics.Add(new AreaReadDiagnostic(
                "entity-read-failed",
                "One entity could not be read consistently and was omitted from this sample.",
                AreaDiagnosticSeverity.Warning,
                entityRef.EntityId));
        }

        return new AreaEntityReadResult(entities, diagnostics);
    }

    private bool TryCreateLogicalIdentity(
        RawEntityRef entityRef,
        string metadata,
        out LogicalEntityIdentity identity)
    {
        identity = default;
        if (!_components.TryResolve(entityRef, "Render", out var render)
            || render == 0
            || !_components.TryResolve(entityRef, "Life", out var life)
            || life == 0
            || !_components.TryResolve(
                entityRef,
                "ObjectMagicProperties",
                out var magicProperties)
            || magicProperties == 0)
        {
            return false;
        }

        identity = new LogicalEntityIdentity(
            metadata,
            render,
            life,
            magicProperties);
        return true;
    }

    private EntityReadOutcome TryReadEntity(
        RawEntityRef entityRef,
        out AreaEntitySnapshot entity)
    {
        entity = default!;
        if (!_reader.TryReadPointer(
                entityRef.EntityAddress + _profile.Entity.DetailsOffset,
                out var details)
            || !_reader.TryReadStdWString(
                details + _profile.EntityDetails.NameOffset,
                MaximumMetadataLength,
                out var metadata)
            || string.IsNullOrWhiteSpace(metadata))
        {
            return EntityReadOutcome.Failed;
        }

        if (!_components.TryResolve(entityRef, "Render", out var render))
        {
            return EntityReadOutcome.Failed;
        }

        if (render == 0)
        {
            return EntityReadOutcome.Unpositioned;
        }

        if (!_reader.TryReadVector3(
                render + _profile.Render.WorldPositionOffset,
                out var worldPosition))
        {
            return EntityReadOutcome.Failed;
        }

        var category = Categorize(metadata);
        if (!TryReadDisposition(entityRef, out var disposition)
            || !TryReadLife(entityRef, out var currentLife, out var maximumLife)
            || !TryReadMagicProperties(
                entityRef,
                out var rarity,
                out var modIds)
            || !TryReadMinimapIcon(
                entityRef,
                out var hasMinimapIcon,
                out var isMinimapIconComplete)
            || !TryReadChestState(entityRef, category, out var chestState))
        {
            return EntityReadOutcome.Failed;
        }

        entity = new AreaEntitySnapshot(
            entityRef.EntityId,
            metadata,
            _names.ResolveOrShorten(metadata),
            category,
            worldPosition,
            new Vector2(
                worldPosition.X / _profile.WorldToGridRatio,
                worldPosition.Y / _profile.WorldToGridRatio),
            disposition,
            rarity,
            currentLife,
            maximumLife,
            hasMinimapIcon,
            isMinimapIconComplete,
            chestState,
            modIds);
        return EntityReadOutcome.Success;
    }

    private enum EntityReadOutcome
    {
        Success,
        Unpositioned,
        Failed
    }

    private readonly record struct LogicalEntityIdentity(
        string Metadata,
        nint Render,
        nint Life,
        nint ObjectMagicProperties);

    private sealed class LogicalEntityIdentityComparer
        : IEqualityComparer<LogicalEntityIdentity>
    {
        public static LogicalEntityIdentityComparer Instance { get; } = new();

        public bool Equals(
            LogicalEntityIdentity x,
            LogicalEntityIdentity y)
            => string.Equals(
                   x.Metadata,
                   y.Metadata,
                   StringComparison.OrdinalIgnoreCase)
               && x.Render == y.Render
               && x.Life == y.Life
               && x.ObjectMagicProperties == y.ObjectMagicProperties;

        public int GetHashCode(LogicalEntityIdentity identity)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Metadata),
                identity.Render,
                identity.Life,
                identity.ObjectMagicProperties);
    }

    private bool TryReadDisposition(
        RawEntityRef entity,
        out AreaEntityDisposition disposition)
    {
        disposition = AreaEntityDisposition.Unknown;
        if (!_components.TryResolve(entity, "Positioned", out var positioned))
        {
            return true;
        }

        if (positioned == 0)
        {
            return true;
        }

        if (!_reader.TryReadByte(
                positioned + _profile.Positioned.ReactionOffset,
                out var reaction))
        {
            return true;
        }

        disposition = (reaction & 0x7F) switch
        {
            0 => AreaEntityDisposition.Hostile,
            1 => AreaEntityDisposition.Friendly,
            _ => AreaEntityDisposition.Neutral
        };
        return true;
    }

    private bool TryReadLife(
        RawEntityRef entity,
        out int current,
        out int maximum)
    {
        current = 0;
        maximum = 0;
        if (!_components.TryResolve(entity, "Life", out var life))
        {
            return true;
        }

        if (life == 0)
        {
            return true;
        }

        var health = life + _profile.Life.HealthOffset;
        if (!_memory.TryReadInt32(
                health + _profile.Vital.CurrentOffset,
                out current)
            || !_memory.TryReadInt32(
                health + _profile.Vital.MaximumOffset,
                out maximum))
        {
            current = 0;
            maximum = 0;
        }

        return true;
    }

    private bool TryReadMagicProperties(
        RawEntityRef entity,
        out AreaEntityRarity rarity,
        out IReadOnlyList<string> modIds)
    {
        rarity = AreaEntityRarity.NonMonster;
        modIds = [];
        if (!_components.TryResolve(
                entity,
                "ObjectMagicProperties",
                out var magicProperties))
        {
            return true;
        }

        if (magicProperties == 0)
        {
            return true;
        }

        if (!_memory.TryReadInt32(
                magicProperties + _profile.ObjectMagicProperties.RarityOffset,
                out var rawRarity))
        {
            return true;
        }

        rarity = rawRarity is >= 0 and <= 3
            ? (AreaEntityRarity)rawRarity
            : AreaEntityRarity.NonMonster;

        if (!_reader.TryReadStdVector(
                magicProperties + _profile.ObjectMagicProperties.ModsOffset,
                _profile.ObjectMagicProperties.ModElementStride,
                MaximumModifiers,
                out var modifiers))
        {
            return true;
        }

        if (modifiers.Count == 0)
        {
            return true;
        }

        var ids = new List<string>(modifiers.Count);
        for (var index = 0; index < modifiers.Count; index++)
        {
            var element = modifiers.First
                          + (index * _profile.ObjectMagicProperties.ModElementStride);
            if (!_reader.TryReadPointer(
                    element + _profile.ObjectMagicProperties.ModRecordOffset,
                    out var record)
                || !_reader.TryReadPointer(
                    record + _profile.ObjectMagicProperties.ModIdPointerOffset,
                    out var idAddress)
                || !_memory.TryReadUtf16(
                    idAddress,
                    MaximumModifierIdLength,
                    out var id)
                || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            ids.Add(id);
        }

        modIds = ids;
        return true;
    }

    private bool TryReadMinimapIcon(
        RawEntityRef entity,
        out bool hasIcon,
        out bool isComplete)
    {
        hasIcon = false;
        isComplete = false;
        if (!_components.TryResolve(entity, "MinimapIcon", out var icon))
        {
            return true;
        }

        if (icon == 0)
        {
            return true;
        }

        hasIcon = true;
        if (!_memory.TryReadInt32(
                icon + _profile.MinimapIcon.CompletedOffset,
                out var completedState))
        {
            return true;
        }

        isComplete = completedState != 0;
        return true;
    }

    private bool TryReadChestState(
        RawEntityRef entity,
        AreaEntityCategory category,
        out AreaChestState chestState)
    {
        chestState = category == AreaEntityCategory.Chest
            ? AreaChestState.Unknown
            : AreaChestState.NotApplicable;
        if (!_components.TryResolve(entity, "Chest", out var chest))
        {
            return true;
        }

        if (chest == 0)
        {
            return true;
        }

        if (!_reader.TryReadByte(
                chest + _profile.Chest.OpenStateOffset,
                out var openState))
        {
            return true;
        }

        chestState = openState == 0
            ? AreaChestState.Closed
            : AreaChestState.Opened;
        return true;
    }

    private static AreaEntityCategory Categorize(string metadata)
    {
        if (metadata.Contains("/Characters/", StringComparison.Ordinal))
        {
            return AreaEntityCategory.Player;
        }

        if (metadata.Contains("/Monsters/", StringComparison.Ordinal)
            && !metadata.Contains("/NPC/", StringComparison.Ordinal)
            && !IsNonCombat(metadata))
        {
            return AreaEntityCategory.Monster;
        }

        if (metadata.Contains("/NPC/", StringComparison.Ordinal))
        {
            return AreaEntityCategory.Npc;
        }

        if (metadata.Contains("/Chests", StringComparison.Ordinal)
            && !IsBreakableProp(metadata))
        {
            return AreaEntityCategory.Chest;
        }

        if (metadata.Contains("Transition", StringComparison.Ordinal))
        {
            return AreaEntityCategory.Transition;
        }

        if (metadata.Contains("/Terrain/", StringComparison.Ordinal)
            || metadata.Contains(
                "/MiscellaneousObjects/",
                StringComparison.Ordinal))
        {
            return AreaEntityCategory.Object;
        }

        return AreaEntityCategory.Other;
    }

    private static bool IsNonCombat(string metadata)
        => metadata.Contains("MonsterMods", StringComparison.Ordinal)
           || metadata.Contains("Summoned", StringComparison.Ordinal)
           || metadata.Contains("/Daemon/", StringComparison.Ordinal)
           || metadata.Contains("Invisible", StringComparison.Ordinal);

    private static bool IsBreakableProp(string metadata)
        => metadata.Contains("Urn", StringComparison.Ordinal)
           || metadata.Contains("Vase", StringComparison.Ordinal)
           || metadata.Contains("Pot", StringComparison.Ordinal)
           || metadata.Contains("Jar", StringComparison.Ordinal)
           || metadata.Contains("Sack", StringComparison.Ordinal)
           || metadata.Contains("Barrel", StringComparison.Ordinal)
           || metadata.Contains("Crate", StringComparison.Ordinal)
           || metadata.Contains("Debris", StringComparison.Ordinal)
           || metadata.Contains("Rubble", StringComparison.Ordinal)
           || metadata.Contains("Basket", StringComparison.Ordinal)
           || metadata.Contains("Coffin", StringComparison.Ordinal);
}
