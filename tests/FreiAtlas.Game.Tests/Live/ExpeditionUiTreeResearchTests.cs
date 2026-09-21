using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;
using FreiAtlas.Game.Views;
using FreiAtlas.Platform.Windows.Process;
using FreiAtlas.Platform.Windows.Windows;
using Xunit.Abstractions;

namespace FreiAtlas.Game.Tests.Live;

public sealed class ExpeditionUiTreeResearchTests
{
    private const int MaximumNodes = 30_000;
    private const int MaximumChildrenPerNode = 8_192;
    private readonly ITestOutputHelper _output;

    public ExpeditionUiTreeResearchTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Live")]
    public void Capture_WritesCurrentUiTree()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        var capturePath = Environment.GetEnvironmentVariable("FREI_UI_CAPTURE_PATH");
        var label = Environment.GetEnvironmentVariable("FREI_UI_CAPTURE_LABEL");
        if (string.IsNullOrWhiteSpace(configuredPid)
            || string.IsNullOrWhiteSpace(capturePath)
            || string.IsNullOrWhiteSpace(label))
        {
            _output.WriteLine(
                "Skipped: FREI_LIVE_PID, FREI_UI_CAPTURE_PATH, and "
                + "FREI_UI_CAPTURE_LABEL are required.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            Path.IsPathFullyQualified(capturePath),
            "FREI_UI_CAPTURE_PATH must be an absolute path.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var reader = new GameMemoryReader(memory, profile);
            var locator = new GameStateLocator(profile);
            var session = new GameMemorySession(memory, profile, locator);
            Assert.True(
                session.TryRefresh(out var root, out var area, out var diagnostics),
                string.Join(
                    Environment.NewLine,
                    diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.True(
                reader.TryReadPointer(
                    root.InGameState + profile.InGameState.UiRootOffset,
                    out var uiRoot),
                "The in-game UI root pointer was unavailable.");
            Assert.True(
                reader.TryReadPointer(
                    uiRoot + profile.UiElement.SelfOffset,
                    out var rootSelf)
                && rootSelf == uiRoot,
                "The in-game UI root failed its self-pointer liveness check.");

            var window = new GameWindowTracker().Track(processId);
            Assert.NotNull(window);
            Assert.True(window!.ClientBounds.Width > 0 && window.ClientBounds.Height > 0);

            var geometryProbe = new MapUiCandidateProbe(memory, profile);
            var stations = ReadRuneStations(
                memory,
                reader,
                profile,
                root.AreaInstance,
                area.SessionSequence,
                area.AreaLevel);
            var nodes = ReadNodes(
                memory,
                reader,
                profile,
                geometryProbe,
                stations,
                uiRoot,
                window.ClientBounds.Width,
                window.ClientBounds.Height,
                out var truncated);
            var capture = new UiTreeCapture(
                label,
                DateTimeOffset.UtcNow,
                processId,
                profile.ProfileId,
                area.AreaCode,
                area.AreaHash,
                area.AreaLevel,
                window.ClientBounds.Width,
                window.ClientBounds.Height,
                Hex(uiRoot),
                truncated,
                stations,
                nodes);

            var directory = Path.GetDirectoryName(capturePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                capturePath,
                JsonSerializer.Serialize(
                    capture,
                    new JsonSerializerOptions { WriteIndented = true }));

            _output.WriteLine($"CaptureLabel={label}");
            _output.WriteLine($"CapturePath={capturePath}");
            _output.WriteLine($"ProcessId={processId}");
            _output.WriteLine($"AreaCode={area.AreaCode}");
            _output.WriteLine($"UiRoot={Hex(uiRoot)}");
            _output.WriteLine($"Client={window.ClientBounds.Width}x{window.ClientBounds.Height}");
            _output.WriteLine($"Nodes={nodes.Count}");
            _output.WriteLine($"LocallyVisible={nodes.Count(node => node.LocalVisible)}");
            _output.WriteLine($"EffectiveVisible={nodes.Count(node => node.EffectiveVisible)}");
            _output.WriteLine($"RuneStations={stations.Count}");
            _output.WriteLine($"Truncated={truncated}");
        }
    }

    private static IReadOnlyList<UiNodeCapture> ReadNodes(
        IProcessMemory memory,
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        MapUiCandidateProbe geometryProbe,
        IReadOnlyList<RuneStationCapture> stations,
        nint uiRoot,
        float clientWidth,
        float clientHeight,
        out bool truncated)
    {
        var result = new List<UiNodeCapture>();
        var visited = new HashSet<nint>();
        var visibleQueue = new Queue<UiWorkItem>();
        var hiddenQueue = new Queue<UiWorkItem>();
        visibleQueue.Enqueue(new UiWorkItem(uiRoot, "root", true));
        truncated = false;

        while (visibleQueue.Count > 0 || hiddenQueue.Count > 0)
        {
            if (visited.Count >= MaximumNodes)
            {
                truncated = true;
                break;
            }

            var item = visibleQueue.Count > 0
                ? visibleQueue.Dequeue()
                : hiddenQueue.Dequeue();
            if (!visited.Add(item.Address))
            {
                continue;
            }

            var selfValid = reader.TryReadPointer(
                    item.Address + profile.UiElement.SelfOffset,
                    out var self)
                && self == item.Address;
            _ = reader.TryReadPointer(item.Address, out var typePointer);
            _ = reader.TryReadPointer(
                item.Address + profile.UiElement.ParentOffset,
                out var parent);
            _ = reader.TryReadUInt32(
                item.Address + profile.UiElement.FlagsOffset,
                out var flags);
            var localVisible = (flags & (1u << profile.UiElement.VisibleBit)) != 0;
            var effectiveVisible = item.ParentVisible && localVisible;
            _ = reader.TryReadVector2(
                item.Address + profile.UiElement.RelativePositionOffset,
                out var relativePosition);
            _ = reader.TryReadVector2(
                item.Address + profile.UiElement.WidthOffset,
                out var size);
            _ = reader.TryReadVector2(
                item.Address + profile.UiElement.PositionModifierOffset,
                out var positionModifier);
            _ = reader.TryReadByte(
                item.Address + profile.UiElement.ScaleIndexOffset,
                out var scaleIndex);

            var childrenValid = reader.TryReadStdVector(
                item.Address + profile.UiElement.ChildrenOffset,
                IntPtr.Size,
                MaximumChildrenPerNode,
                out var children);
            var rect = selfValid
                       && localVisible
                       && size.X > 1f
                       && size.Y > 1f
                ? TryReadViewport(
                    geometryProbe,
                    item.Address,
                    clientWidth,
                    clientHeight)
                : null;
            var isPanelNode = item.Path == "root/39"
                              || item.Path.StartsWith("root/39/", StringComparison.Ordinal);
            var strings = isPanelNode && effectiveVisible
                ? ReadStrings(reader, profile, item.Address)
                : [];
            var pointerMatches = isPanelNode
                ? ReadPointerMatches(memory, item.Address, stations)
                : [];

            result.Add(new UiNodeCapture(
                item.Path,
                Hex(item.Address),
                Hex(parent),
                Hex(typePointer),
                selfValid,
                childrenValid,
                childrenValid ? children.Count : -1,
                $"0x{flags:X8}",
                localVisible,
                effectiveVisible,
                Round(relativePosition.X),
                Round(relativePosition.Y),
                Round(size.X),
                Round(size.Y),
                Round(positionModifier.X),
                Round(positionModifier.Y),
                scaleIndex,
                rect,
                strings,
                pointerMatches));

            if (!childrenValid)
            {
                continue;
            }

            for (var index = 0; index < children.Count; index++)
            {
                if (!reader.TryReadPointer(
                        children.First + (index * IntPtr.Size),
                        out var child))
                {
                    continue;
                }

                _ = reader.TryReadUInt32(
                    child + profile.UiElement.FlagsOffset,
                    out var childFlags);
                var childIsEffectivelyVisible = effectiveVisible
                    && (childFlags & (1u << profile.UiElement.VisibleBit)) != 0;
                var targetQueue = childIsEffectivelyVisible
                    ? visibleQueue
                    : hiddenQueue;
                targetQueue.Enqueue(new UiWorkItem(
                    child,
                    $"{item.Path}/{index}",
                    effectiveVisible));
            }
        }

        return result;
    }

    private static IReadOnlyList<RuneStationCapture> ReadRuneStations(
        IProcessMemory memory,
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        nint areaInstance,
        long sessionSequence,
        int areaLevel)
    {
        var tree = new AreaEntityTreeReader(memory, profile);
        Assert.True(
            tree.TryRead(areaInstance, out var entities, out var diagnostic),
            diagnostic?.Message ?? "The AwakeEntities tree was unavailable.");
        var components = new EntityComponentResolver(memory, profile);
        components.BeginSample(sessionSequence);
        var catalog = ExpeditionRecipeCatalog.LoadEmbedded();
        var result = new List<RuneStationCapture>();
        foreach (var entity in entities)
        {
            if (!reader.TryReadPointer(
                    entity.EntityAddress + profile.Entity.DetailsOffset,
                    out var details)
                || !reader.TryReadStdWString(
                    details + profile.EntityDetails.NameOffset,
                    512,
                    out var metadata)
                || !string.Equals(
                    metadata,
                    "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
                    StringComparison.Ordinal)
                || !components.TryResolve(entity, "StateMachine", out var stateMachine)
                || stateMachine == 0
                || !reader.TryReadStdVector(
                    stateMachine + profile.StateMachine.ListenersOffset,
                    IntPtr.Size,
                    256,
                    out var listeners))
            {
                continue;
            }

            nint station = 0;
            for (var index = 0; index < listeners.Count; index++)
            {
                if (!reader.TryReadPointer(
                        listeners.First + (index * IntPtr.Size),
                        out var listener)
                    || !reader.TryReadPointer(listener, out var sub))
                {
                    continue;
                }

                var candidateValue = sub.ToInt64() - profile.RuneStation.ListenerSubOffset;
                if (candidateValue > 0)
                {
                    var candidate = (nint)candidateValue;
                    if (reader.TryReadPointer(
                            candidate + profile.RuneStation.OwnerOffset,
                            out var owner)
                        && owner == entity.EntityAddress)
                    {
                        station = candidate;
                        break;
                    }
                }
            }

            if (station == 0
                || !memory.TryReadInt32(
                    station + profile.RuneStation.HoleCountOffset,
                    out var holeCount)
                || holeCount is <= 0 or > 16)
            {
                continue;
            }

            _ = memory.TryReadInt32(
                station + profile.RuneStation.AnchorPositionOffset,
                out var anchorPosition);
            var isUnique = !reader.TryReadPointer(
                station + profile.RuneStation.AnchorReferenceOffset,
                out var anchorReference);
            var anchorIndex = isUnique
                ? -1
                : ResolveAnchorIndex(reader, profile, station, anchorReference);
            var recipes = catalog.Resolve(
                anchorIndex,
                anchorPosition,
                holeCount,
                isUnique,
                areaLevel);
            var orderedRecipes = recipes
                .OrderByDescending(recipe => recipe.Size)
                .ThenByDescending(recipe => recipe.CatalogRow)
                .ThenBy(recipe => recipe.RecipeId, StringComparer.Ordinal)
                .ToArray();
            result.Add(new RuneStationCapture(
                entity.EntityId,
                Hex(entity.EntityAddress),
                Hex(station),
                holeCount,
                anchorPosition,
                anchorIndex,
                isUnique,
                orderedRecipes.Length,
                orderedRecipes.Select(recipe => recipe.RecipeId).ToArray(),
                orderedRecipes.Select(recipe => string.Join(
                    " + ",
                    recipe.Rewards.Select(reward =>
                        $"{reward.Quantity}x {reward.DisplayName}"))).ToArray()));
        }

        return result;
    }

    private static int ResolveAnchorIndex(
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        nint station,
        nint anchorReference)
    {
        if (!reader.TryReadPointer(
                station + profile.RuneStation.AnchorHolderOffset,
                out var holder)
            || !reader.TryReadPointer(
                holder + profile.RuneStation.RuneTablePointerOffset,
                out var tablePointer)
            || !reader.TryReadPointer(tablePointer, out var tableBase))
        {
            return -1;
        }

        var delta = anchorReference.ToInt64() - tableBase.ToInt64();
        var stride = profile.RuneStation.RuneStride;
        return delta >= 0
               && stride > 0
               && delta % stride == 0
               && delta / stride < profile.RuneStation.RuneCount
            ? (int)(delta / stride)
            : -1;
    }

    private static IReadOnlyList<string> ReadStrings(
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        nint address)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset <= 0x600; offset += sizeof(long))
        {
            if (!reader.TryReadStdWString(address + offset, 128, out var value)
                || string.IsNullOrWhiteSpace(value)
                || value.Any(char.IsControl)
                || !seen.Add(value))
            {
                continue;
            }

            result.Add($"+0x{offset:X}:{value}");
        }

        return result;
    }

    private static IReadOnlyList<string> ReadPointerMatches(
        IProcessMemory memory,
        nint address,
        IReadOnlyList<RuneStationCapture> stations)
    {
        var targets = stations
            .SelectMany(station => new[]
            {
                (Address: ParseHex(station.EntityAddress), Kind: $"entity:{station.EntityId}"),
                (Address: ParseHex(station.StationAddress), Kind: $"station:{station.EntityId}")
            })
            .ToDictionary(item => item.Address, item => item.Kind);
        var bytes = new byte[0x800];
        if (targets.Count == 0 || !memory.TryRead(address, bytes))
        {
            return [];
        }

        var result = new List<string>();
        for (var offset = 0; offset <= bytes.Length - sizeof(long); offset += sizeof(long))
        {
            var candidate = BitConverter.ToInt64(bytes, offset);
            if (targets.TryGetValue(candidate, out var kind))
            {
                result.Add($"+0x{offset:X}:{kind}");
            }
        }

        return result;
    }

    private static long ParseHex(string value)
        => long.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static AreaUiRect? TryReadViewport(
        MapUiCandidateProbe probe,
        nint address,
        float width,
        float height)
    {
        var method = typeof(MapUiCandidateProbe)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "TryReadViewport"
                && candidate.GetParameters().Length == 4);
        object?[] arguments =
        [
            address,
            width,
            height,
            new AreaUiRect(0f, 0f, 0f, 0f)
        ];
        return method.Invoke(probe, arguments) is true
            ? (AreaUiRect)arguments[3]!
            : null;
    }

    private static string Hex(nint value)
        => value == 0 ? "0x0" : $"0x{value.ToInt64():X}";

    private static float Round(float value)
        => float.IsFinite(value) ? MathF.Round(value, 3) : 0f;

    private readonly record struct UiWorkItem(
        nint Address,
        string Path,
        bool ParentVisible);

    private sealed record UiTreeCapture(
        string Label,
        DateTimeOffset CapturedAt,
        int ProcessId,
        string ProfileId,
        string AreaCode,
        uint AreaHash,
        int AreaLevel,
        int ClientWidth,
        int ClientHeight,
        string UiRoot,
        bool Truncated,
        IReadOnlyList<RuneStationCapture> RuneStations,
        IReadOnlyList<UiNodeCapture> Nodes);

    private sealed record RuneStationCapture(
        uint EntityId,
        string EntityAddress,
        string StationAddress,
        int HoleCount,
        int AnchorPosition,
        int AnchorIndex,
        bool IsUnique,
        int RecipeCount,
        IReadOnlyList<string> RecipeIds,
        IReadOnlyList<string> OrderedRewardTexts);

    private sealed record UiNodeCapture(
        string Path,
        string Address,
        string Parent,
        string TypePointer,
        bool SelfValid,
        bool ChildrenValid,
        int ChildCount,
        string Flags,
        bool LocalVisible,
        bool EffectiveVisible,
        float RelativeX,
        float RelativeY,
        float Width,
        float Height,
        float PositionModifierX,
        float PositionModifierY,
        byte ScaleIndex,
        AreaUiRect? Rect,
        IReadOnlyList<string> Strings,
        IReadOnlyList<string> PointerMatches);
}
