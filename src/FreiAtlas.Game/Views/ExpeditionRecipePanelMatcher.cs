using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Views;

internal sealed class ExpeditionRecipePanelMatcher
{
    private string? _stableKey;
    private int _stableSamples;

    public AreaExpeditionRecipePanelSnapshot Observe(
        long sessionSequence,
        ExpeditionRecipeUiProbeResult probe,
        IReadOnlyList<AreaContentSnapshot> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        switch (probe.State)
        {
            case ExpeditionRecipeUiProbeState.Unavailable:
                Reset();
                return AreaExpeditionRecipePanelSnapshot.Unavailable;
            case ExpeditionRecipeUiProbeState.Closed:
                Reset();
                return AreaExpeditionRecipePanelSnapshot.Closed;
            case ExpeditionRecipeUiProbeState.OpenUnverified:
                Reset();
                return AreaExpeditionRecipePanelSnapshot.UnverifiedOpen;
            case ExpeditionRecipeUiProbeState.OpenCandidate:
                break;
            default:
                Reset();
                return AreaExpeditionRecipePanelSnapshot.Unavailable;
        }

        if (probe.PanelBounds is not { } panelBounds
            || probe.ListClipBounds is not { } listClipBounds
            || probe.Rows.IsDefaultOrEmpty)
        {
            Reset();
            return AreaExpeditionRecipePanelSnapshot.UnverifiedOpen;
        }

        var orderedRawRows = probe.Rows
            .OrderBy(row => row.Bounds.Y)
            .ThenBy(row => row.Bounds.X)
            .ToImmutableArray();
        var orderedProbe = probe with { Rows = orderedRawRows };

        var matches = contents
            .Where(content =>
                content.Kind == AreaContentKind.Expedition
                && content.ExpeditionDetails is { Recipes.IsDefaultOrEmpty: false })
            .Select(content => TryPrepare(content, orderedProbe))
            .Where(prepared => prepared is not null)
            .Select(prepared => prepared!)
            .ToArray();

        if (matches.Length != 1)
        {
            Reset();
            return AreaExpeditionRecipePanelSnapshot.UnverifiedOpen;
        }

        var match = matches[0];
        var key = BuildStableKey(sessionSequence, orderedProbe, match);
        if (!string.Equals(_stableKey, key, StringComparison.Ordinal))
        {
            _stableKey = key;
            _stableSamples = 1;
            return AreaExpeditionRecipePanelSnapshot.UnverifiedOpen;
        }

        _stableSamples++;
        if (_stableSamples < 2)
        {
            return AreaExpeditionRecipePanelSnapshot.UnverifiedOpen;
        }

        var rows = ImmutableArray.CreateBuilder<AreaExpeditionRecipePanelRow>(match.Recipes.Length);
        for (var index = 0; index < match.Recipes.Length; index++)
        {
            var rawRow = orderedRawRows[index];
            rows.Add(new AreaExpeditionRecipePanelRow(
                match.Recipes[index].RecipeId,
                match.Recipes[index].CatalogRow,
                rawRow.Bounds,
                Intersects(rawRow.Bounds, listClipBounds)));
        }

        return new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            panelBounds,
            listClipBounds,
            match.InstanceId,
            rows.ToImmutable());
    }

    public void Reset()
    {
        _stableKey = null;
        _stableSamples = 0;
    }

    private static PreparedMatch? TryPrepare(
        AreaContentSnapshot content,
        ExpeditionRecipeUiProbeResult probe)
    {
        var details = content.ExpeditionDetails;
        if (details is null
            || details.Recipes.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(content.InstanceId))
        {
            return null;
        }

        var recipes = details.Recipes
            .OrderByDescending(recipe => recipe.Size)
            .ThenByDescending(recipe => recipe.CatalogRow)
            .ThenBy(recipe => recipe.RecipeId, StringComparer.Ordinal)
            .ToImmutableArray();

        if (recipes.Any(recipe => recipe.Rewards.Length != 1)
            || recipes.Length != probe.Rows.Length)
        {
            return null;
        }

        for (var index = 0; index < recipes.Length; index++)
        {
            var reward = recipes[index].Rewards[0];
            var row = probe.Rows[index];
            if (recipes[index].Size != row.RuneCount
                || reward.Quantity != row.RewardQuantity)
            {
                return null;
            }
        }

        return new PreparedMatch(content.InstanceId, recipes);
    }

    private static string BuildStableKey(
        long sessionSequence,
        ExpeditionRecipeUiProbeResult probe,
        PreparedMatch match)
    {
        var builder = new StringBuilder();
        Append(builder, sessionSequence);
        Append(builder, probe.PanelAddress);
        Append(builder, probe.ContainerAddress);
        Append(builder, match.InstanceId);
        foreach (var row in probe.Rows)
        {
            Append(builder, row.Address);
            Append(builder, row.RuneCount);
            Append(builder, row.RewardQuantity);
        }
        foreach (var recipe in match.Recipes)
        {
            Append(builder, recipe.RecipeId);
            Append(builder, recipe.CatalogRow);
            Append(builder, recipe.Size);
        }
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, nint value)
        => Append(builder, value.ToInt64());

    private static void Append(StringBuilder builder, int value)
        => Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, long value)
        => Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }

    private static bool Intersects(AreaUiRect row, AreaUiRect clip)
    {
        var left = MathF.Max(row.X, clip.X);
        var top = MathF.Max(row.Y, clip.Y);
        var right = MathF.Min(row.X + row.Width, clip.X + clip.Width);
        var bottom = MathF.Min(row.Y + row.Height, clip.Y + clip.Height);
        return float.IsFinite(left)
            && float.IsFinite(top)
            && float.IsFinite(right)
            && float.IsFinite(bottom)
            && right > left
            && bottom > top;
    }

    private sealed record PreparedMatch(
        string InstanceId,
        ImmutableArray<AreaExpeditionRecipe> Recipes);
}
