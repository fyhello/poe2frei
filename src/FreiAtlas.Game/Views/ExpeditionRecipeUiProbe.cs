using System.Collections.Immutable;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Views;

internal enum ExpeditionRecipeUiProbeState
{
    Unavailable,
    Closed,
    OpenUnverified,
    OpenCandidate
}

internal sealed record ExpeditionRecipeUiRowCandidate(
    nint Address,
    int RuneCount,
    int RewardQuantity,
    AreaUiRect Bounds);

internal sealed record ExpeditionRecipeUiProbeResult(
    ExpeditionRecipeUiProbeState State,
    nint PanelAddress,
    nint ViewportAddress,
    nint ContainerAddress,
    AreaUiRect? PanelBounds,
    AreaUiRect? ListClipBounds,
    ImmutableArray<ExpeditionRecipeUiRowCandidate> Rows,
    ImmutableArray<AreaReadDiagnostic> Diagnostics);

internal sealed class ExpeditionRecipeUiProbe
{
    private const int MaximumBranchNodes = 4096;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly UiElementClientGeometryReader _geometry;

    public ExpeditionRecipeUiProbe(IProcessMemory memory, Poe2MemoryProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
        _geometry = new UiElementClientGeometryReader(memory, _profile);
    }

    public ExpeditionRecipeUiProbeResult Probe(nint inGameState, AreaUiRect clientViewport)
    {
        if (!_reader.TryReadPointer(inGameState + _profile.InGameState.UiRootOffset, out var uiRoot)
            || !HasValidSelf(uiRoot))
        {
            return Result(
                ExpeditionRecipeUiProbeState.Unavailable,
                diagnostics: [Diagnostic("runeforge-ui-root-unavailable", "The runeforge UI root is unavailable.")]);
        }

        if (!TryFindBranch(uiRoot, out var branch))
        {
            return Result(ExpeditionRecipeUiProbeState.Unavailable);
        }

        if (!branch.GateVisible)
        {
            return Result(
                ExpeditionRecipeUiProbeState.Closed,
                branch.Gate,
                branch.Viewport,
                branch.Container);
        }

        if (!branch.ContainerVerified)
        {
            return Unverified(
                branch,
                "runeforge-ui-structure-invalid",
                "The runeforge panel fingerprint chain was found, but its recipe container is not structurally valid.");
        }

        if (!_reader.TryReadVector2(branch.Viewport + _profile.RuneforgeUi.ScrollOffset, out var scrollOffset))
        {
            return Unverified(branch, "runeforge-ui-scroll-invalid", "The runeforge list scroll offset is invalid.");
        }

        var diagnostics = ImmutableArray.CreateBuilder<AreaReadDiagnostic>();
        if (!_geometry.TryReadRect(branch.Gate, 0, Vector2.Zero, clientViewport, out var panelRect, out var panelDiagnostic))
        {
            AddGeometryDiagnostic(diagnostics, panelDiagnostic);
            return Unverified(branch, diagnostics);
        }
        if (!_geometry.TryReadRect(branch.Container, branch.Viewport, Vector2.Zero, clientViewport, out var listRect, out var listDiagnostic))
        {
            AddGeometryDiagnostic(diagnostics, listDiagnostic);
            return Unverified(branch, diagnostics);
        }
        var panelBounds = Intersect(panelRect, clientViewport);
        var listClipBounds = Intersect(listRect, clientViewport);
        if (panelBounds is null || listClipBounds is null)
        {
            return Unverified(branch, "runeforge-ui-clip-empty", "The runeforge panel or list clip is outside the client viewport.");
        }

        var rows = ImmutableArray.CreateBuilder<ExpeditionRecipeUiRowCandidate>(branch.Rows.Length);
        foreach (var row in branch.Rows)
        {
            string? rowDiagnostic = null;
            var bounds = default(AreaUiRect);
            if (!TryParseRewardQuantity(row.RewardText, out var rewardQuantity)
                || !_geometry.TryReadRect(row.Address, branch.Viewport, scrollOffset, clientViewport, out bounds, out rowDiagnostic)
                || bounds.Width <= 0f
                || bounds.Height <= 0f)
            {
                AddGeometryDiagnostic(diagnostics, rowDiagnostic);
                return Unverified(branch, diagnostics);
            }
            rows.Add(new ExpeditionRecipeUiRowCandidate(row.Address, row.RuneCount, rewardQuantity, bounds));
        }

        var ordered = rows
            .OrderBy(row => row.Bounds.Y)
            .ThenBy(row => row.Bounds.X)
            .ToImmutableArray();
        if (ordered.Length != branch.Rows.Length || HasDuplicatePosition(ordered))
        {
            return Unverified(branch, "runeforge-ui-rows-invalid", "The runeforge rows are duplicated or inconsistent.");
        }

        return new ExpeditionRecipeUiProbeResult(
            ExpeditionRecipeUiProbeState.OpenCandidate,
            branch.Gate,
            branch.Viewport,
            branch.Container,
            panelBounds,
            listClipBounds,
            ordered,
            diagnostics.ToImmutable());
    }

    public void Reset()
    {
    }

    private bool TryFindBranch(nint root, out RecipeBranch branch)
    {
        branch = default;
        RecipeBranch? fallback = null;
        if (!TryReadChildren(root, MaximumBranchNodes, out var rootChildren))
        {
            return false;
        }

        foreach (var visible in new[] { true, false })
        {
            foreach (var candidate in rootChildren)
            {
                if (!TryReadFlags(candidate, out var flags)
                    || IsVisible(flags) != visible
                    || !MatchesFingerprint(flags, _profile.RuneforgeUi.PanelFlagFingerprints[0]))
                {
                    continue;
                }

                var nodes = new nint[_profile.RuneforgeUi.PanelFlagFingerprints.Count];
                nodes[0] = candidate;
                var visited = 1;
                if (TryMatchRemaining(candidate, 1, nodes, ref visited, out var rows))
                {
                    branch = new RecipeBranch(
                        nodes[_profile.RuneforgeUi.GateStep],
                        nodes[_profile.RuneforgeUi.ViewportStep],
                        nodes[^1],
                        IsVisible(flags),
                        rows,
                        ContainerVerified: true);
                    return true;
                }

                if (fallback is null)
                {
                    var shapeNodes = new nint[_profile.RuneforgeUi.PanelFlagFingerprints.Count];
                    shapeNodes[0] = candidate;
                    var shapeVisited = 1;
                    if (TryMatchFingerprintChain(candidate, 1, shapeNodes, ref shapeVisited))
                    {
                        fallback = new RecipeBranch(
                            shapeNodes[_profile.RuneforgeUi.GateStep],
                            shapeNodes[_profile.RuneforgeUi.ViewportStep],
                            shapeNodes[^1],
                            IsVisible(flags),
                            [],
                            ContainerVerified: false);
                    }
                }
            }
        }
        if (fallback is not null)
        {
            branch = fallback.Value;
            return true;
        }

        return false;
    }

    private bool TryMatchFingerprintChain(
        nint parent,
        int step,
        nint[] nodes,
        ref int visited)
    {
        if (visited >= MaximumBranchNodes
            || !TryReadChildren(parent, MaximumBranchNodes, out var children))
        {
            return false;
        }

        foreach (var visible in new[] { true, false })
        {
            foreach (var child in children)
            {
                if (++visited > MaximumBranchNodes
                    || !TryReadFlags(child, out var flags)
                    || IsVisible(flags) != visible
                    || !MatchesFingerprint(flags, _profile.RuneforgeUi.PanelFlagFingerprints[step]))
                {
                    continue;
                }

                nodes[step] = child;
                if (step == nodes.Length - 1
                    || TryMatchFingerprintChain(child, step + 1, nodes, ref visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryMatchRemaining(
        nint parent,
        int step,
        nint[] nodes,
        ref int visited,
        out ImmutableArray<RecipeRow> rows)
    {
        rows = [];
        if (visited >= MaximumBranchNodes
            || !TryReadChildren(parent, MaximumBranchNodes, out var children))
        {
            return false;
        }

        foreach (var visible in new[] { true, false })
        {
            foreach (var child in children)
            {
                if (++visited > MaximumBranchNodes
                    || !TryReadFlags(child, out var flags)
                    || IsVisible(flags) != visible
                    || !MatchesFingerprint(flags, _profile.RuneforgeUi.PanelFlagFingerprints[step]))
                {
                    continue;
                }

                nodes[step] = child;
                if (step == nodes.Length - 1)
                {
                    if (TryReadContainer(child, out rows))
                    {
                        return true;
                    }
                    continue;
                }
                if (TryMatchRemaining(child, step + 1, nodes, ref visited, out rows))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryReadContainer(nint container, out ImmutableArray<RecipeRow> rows)
    {
        rows = [];
        if (!TryReadChildren(container, _profile.RuneforgeUi.MaximumRows, out var rowAddresses)
            || rowAddresses.Length is < 1
            || rowAddresses.Length > _profile.RuneforgeUi.MaximumRows)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<RecipeRow>(rowAddresses.Length);
        foreach (var rowAddress in rowAddresses)
        {
            if (!TryReadFlags(rowAddress, out var rowFlags))
            {
                return false;
            }
            if (!IsVisible(rowFlags))
            {
                continue;
            }

            var children = ImmutableArray<nint>.Empty;
            if (!HasValidSelf(rowAddress)
                || !TryReadChildren(rowAddress, _profile.RuneforgeUi.MaximumChildrenPerRow, out children)
                || children.Length is < 2
                || children.Length > _profile.RuneforgeUi.MaximumChildrenPerRow
                || children.Length - 1 is < 1 or > 16
                || !HasValidSelf(children[0])
                || !_reader.TryReadStdWString(
                    children[0] + _profile.RuneforgeUi.RewardTextOffset,
                    _profile.RuneforgeUi.MaximumRewardTextLength,
                    out var rewardText)
                || !TryValidateRuneIcons(children.AsSpan()[1..]))
            {
                return false;
            }
            builder.Add(new RecipeRow(rowAddress, children.Length - 1, rewardText));
        }

        if (builder.Count == 0)
        {
            return false;
        }

        rows = builder.ToImmutable();
        return true;
    }

    private bool TryValidateRuneIcons(ReadOnlySpan<nint> icons)
    {
        uint? iconType = null;
        foreach (var icon in icons)
        {
            if (!HasValidSelf(icon) || !TryReadFlags(icon, out var flags))
            {
                return false;
            }
            var normalized = flags & ~(1u << _profile.UiElement.VisibleBit);
            if (iconType is null)
            {
                iconType = normalized;
            }
            else if (iconType.Value != normalized)
            {
                return false;
            }
        }
        return true;
    }

    private bool TryReadChildren(nint address, int maximumCount, out ImmutableArray<nint> children)
    {
        children = [];
        if (!_reader.TryReadStdVector(address + _profile.UiElement.ChildrenOffset, IntPtr.Size, maximumCount, out var range))
        {
            return false;
        }
        var builder = ImmutableArray.CreateBuilder<nint>(range.Count);
        for (var index = 0; index < range.Count; index++)
        {
            if (!_reader.TryReadPointer(range.First + (index * IntPtr.Size), out var child))
            {
                return false;
            }
            builder.Add(child);
        }
        children = builder.ToImmutable();
        return true;
    }

    private bool HasValidSelf(nint address)
        => _reader.TryReadPointer(address + _profile.UiElement.SelfOffset, out var self) && self == address;

    private bool TryReadFlags(nint address, out uint flags)
        => _reader.TryReadUInt32(address + _profile.UiElement.FlagsOffset, out flags);

    private bool IsVisible(uint flags)
        => (flags & (1u << _profile.UiElement.VisibleBit)) != 0;

    private bool MatchesFingerprint(uint flags, uint fingerprint)
    {
        var visibleMask = 1u << _profile.UiElement.VisibleBit;
        return (flags & ~visibleMask) == (fingerprint & ~visibleMask);
    }

    private static bool TryParseRewardQuantity(string text, out int quantity)
    {
        quantity = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        if (text[0] is not (>= '0' and <= '9'))
        {
            quantity = 1;
            return true;
        }

        var index = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9')
        {
            var digit = text[index] - '0';
            if (quantity > 999 || (quantity == 999 && digit > 9))
            {
                return false;
            }
            quantity = (quantity * 10) + digit;
            index++;
        }
        if (index == 0
            || quantity is < 1 or > 9999
            || index >= text.Length
            || text[index] is not ('x' or 'X'))
        {
            return false;
        }
        index++;
        return index < text.Length && !string.IsNullOrWhiteSpace(text[index..]);
    }

    private static bool HasDuplicatePosition(ImmutableArray<ExpeditionRecipeUiRowCandidate> rows)
    {
        for (var index = 1; index < rows.Length; index++)
        {
            if (rows[index - 1].Bounds.X == rows[index].Bounds.X
                && rows[index - 1].Bounds.Y == rows[index].Bounds.Y)
            {
                return true;
            }
        }
        return false;
    }

    private static AreaUiRect? Intersect(AreaUiRect left, AreaUiRect right)
    {
        var x = MathF.Max(left.X, right.X);
        var y = MathF.Max(left.Y, right.Y);
        var rightEdge = MathF.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = MathF.Min(left.Y + left.Height, right.Y + right.Height);
        var width = rightEdge - x;
        var height = bottomEdge - y;
        return float.IsFinite(x)
               && float.IsFinite(y)
               && float.IsFinite(width)
               && float.IsFinite(height)
               && width > 0f
               && height > 0f
            ? new AreaUiRect(x, y, width, height)
            : null;
    }

    private static void AddGeometryDiagnostic(ImmutableArray<AreaReadDiagnostic>.Builder diagnostics, string? code)
    {
        if (code is not null)
        {
            diagnostics.Add(Diagnostic(code, "The runeforge UI geometry is invalid."));
        }
    }

    private static AreaReadDiagnostic Diagnostic(string code, string message)
        => new(code, message, AreaDiagnosticSeverity.Warning);

    private static ExpeditionRecipeUiProbeResult Result(
        ExpeditionRecipeUiProbeState state,
        nint panel = 0,
        nint viewport = 0,
        nint container = 0,
        ImmutableArray<AreaReadDiagnostic> diagnostics = default)
        => new(state, panel, viewport, container, null, null, [], diagnostics.IsDefault ? [] : diagnostics);

    private static ExpeditionRecipeUiProbeResult Unverified(RecipeBranch branch, string code, string message)
        => Unverified(branch, ImmutableArray.Create(Diagnostic(code, message)));

    private static ExpeditionRecipeUiProbeResult Unverified(
        RecipeBranch branch,
        ImmutableArray<AreaReadDiagnostic>.Builder diagnostics)
        => Unverified(branch, diagnostics.ToImmutable());

    private static ExpeditionRecipeUiProbeResult Unverified(
        RecipeBranch branch,
        ImmutableArray<AreaReadDiagnostic> diagnostics)
        => new(
            ExpeditionRecipeUiProbeState.OpenUnverified,
            branch.Gate,
            branch.Viewport,
            branch.Container,
            null,
            null,
            [],
            diagnostics);

    private readonly record struct RecipeRow(nint Address, int RuneCount, string RewardText);

    private readonly record struct RecipeBranch(
        nint Gate,
        nint Viewport,
        nint Container,
        bool GateVisible,
        ImmutableArray<RecipeRow> Rows,
        bool ContainerVerified);
}
