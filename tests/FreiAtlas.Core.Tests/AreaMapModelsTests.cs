using System.Numerics;
using System.Reflection;
using System.Collections.Immutable;
using System.Text.Json;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Core.Tests;

public sealed class AreaMapModelsTests
{
    [Fact]
    public void AreaMapSnapshot_DefaultsNativeRecipePanelToUnavailable()
    {
        var snapshot = AreaMapSnapshot.Loading(29368, "profile", 1);

        Assert.Equal(
            AreaExpeditionRecipePanelSnapshot.Unavailable,
            snapshot.ExpeditionRecipePanel);
    }

    [Fact]
    public void NativeRecipePanel_VerifiedRowsPreserveRecipeIdentityAndClientGeometry()
    {
        var row = new AreaExpeditionRecipePanelRow(
            "recipe-42",
            73,
            new AreaUiRect(31.5f, 99f, 333f, 31.5f),
            true);
        var panel = new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            new AreaUiRect(20f, 70f, 360f, 390f),
            new AreaUiRect(31.5f, 99f, 333f, 346.5f),
            "expedition:1:1052",
            [row]);

        Assert.Equal("recipe-42", Assert.Single(panel.Rows).RecipeId);
        Assert.Equal(73, panel.Rows[0].CatalogRow);
        Assert.Equal(31.5f, panel.Rows[0].Bounds.Height);
    }

    [Fact]
    public void NativeRecipePanel_VerifiedOpenRequiresIdentityGeometryAndRows()
    {
        var panelBounds = new AreaUiRect(20f, 70f, 360f, 390f);
        var listClipBounds = new AreaUiRect(31.5f, 99f, 333f, 346.5f);
        var row = new AreaExpeditionRecipePanelRow(
            "recipe-42",
            73,
            new AreaUiRect(31.5f, 99f, 333f, 31.5f),
            true);

        Assert.Throws<ArgumentException>(() => new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            null,
            listClipBounds,
            "expedition:1:1052",
            [row]));
        Assert.Throws<ArgumentException>(() => new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            panelBounds,
            null,
            "expedition:1:1052",
            [row]));
        Assert.Throws<ArgumentException>(() => new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            panelBounds,
            listClipBounds,
            null,
            [row]));
        Assert.Throws<ArgumentException>(() => new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            panelBounds,
            listClipBounds,
            "expedition:1:1052"));
    }

    [Fact]
    public void NativeRecipePanel_OmittedRowsBecomeEmptyForNonVerifiedOpenStates()
    {
        var unavailable = new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Unavailable,
            false,
            null,
            null,
            null);
        var closed = AreaExpeditionRecipePanelSnapshot.Closed;
        var unverifiedOpen = AreaExpeditionRecipePanelSnapshot.UnverifiedOpen;

        Assert.False(unavailable.Rows.IsDefault);
        Assert.Empty(unavailable.Rows);
        Assert.Empty(closed.Rows);
        Assert.Empty(unverifiedOpen.Rows);
    }

    [Fact]
    public void NativeRecipePanel_InvariantPropertiesCannotBeChangedAfterConstruction()
    {
        var invariantProperties = new[]
        {
            nameof(AreaExpeditionRecipePanelSnapshot.Availability),
            nameof(AreaExpeditionRecipePanelSnapshot.IsOpen),
            nameof(AreaExpeditionRecipePanelSnapshot.PanelBounds),
            nameof(AreaExpeditionRecipePanelSnapshot.ListClipBounds),
            nameof(AreaExpeditionRecipePanelSnapshot.InstanceId),
            nameof(AreaExpeditionRecipePanelSnapshot.Rows)
        };

        foreach (var propertyName in invariantProperties)
        {
            var property = typeof(AreaExpeditionRecipePanelSnapshot).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }

    [Fact]
    public void AreaContentSnapshot_ExpeditionDetailsAreOptionalAndImmutable()
    {
        var snapshot = new AreaContentSnapshot(
            "instance-1",
            "expedition-cache",
            "Expedition Cache",
            AreaContentKind.Expedition,
            AreaContentPhase.Available,
            Vector3.Zero,
            Vector2.Zero,
            1f,
            42,
            []);

        Assert.Null(snapshot.ExpeditionDetails);

        var withDetails = snapshot with { ExpeditionDetails = new AreaExpeditionDetails(6) };

        Assert.Equal(6, withDetails.ExpeditionDetails?.HoleCount);

        var setMethod = typeof(AreaExpeditionDetails)
            .GetProperty(nameof(AreaExpeditionDetails.HoleCount))!
            .SetMethod;
        Assert.True(
            setMethod is null ||
            setMethod.ReturnParameter.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)));
    }

    [Fact]
    public void AreaExpeditionDetails_PreservesOrderedImmutableRecipesAndRewardQuantities()
    {
        var recipes = ImmutableArray.Create(
            new AreaExpeditionRecipe(
                "recipe-20",
                20,
                3,
                [new AreaExpeditionRune(2, "Lightning")],
                [
                    new AreaExpeditionReward(
                        "Metadata/Items/Currency/TestA",
                        "Test Currency A",
                        4,
                        true),
                    new AreaExpeditionReward(
                        string.Empty,
                        "Random reward",
                        1,
                        false)
                ]),
            new AreaExpeditionRecipe(
                "recipe-21",
                21,
                4,
                [new AreaExpeditionRune(3, "Tempest")],
                [
                    new AreaExpeditionReward(
                        "Metadata/Items/Currency/TestB",
                        "Test Currency B",
                        2,
                        true)
                ]));

        var details = new AreaExpeditionDetails(6, recipes);

        Assert.Equal(["recipe-20", "recipe-21"], details.Recipes.Select(item => item.RecipeId));
        Assert.Equal(4, details.Recipes[0].Rewards[0].Quantity);
        Assert.False(details.Recipes[0].Rewards[1].IsExactItem);
        Assert.True(typeof(AreaExpeditionDetails)
            .GetProperty(nameof(AreaExpeditionDetails.Recipes))!
            .PropertyType.IsValueType);
    }

    [Fact]
    public void AreaExpeditionDetails_OmittedRecipesBecomeSerializableEmptyArray()
    {
        var details = new AreaExpeditionDetails(6);

        Assert.False(details.Recipes.IsDefault);
        Assert.Empty(details.Recipes);
        Assert.Contains("\"recipes\":[]", JsonSerializer.Serialize(details, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    [Fact]
    public void AreaLandmarkKind_PreservesExistingValuesAndAppendsBossHint()
    {
        Assert.Equal(0, (int)AreaLandmarkKind.Unknown);
        Assert.Equal(1, (int)AreaLandmarkKind.BossArena);
        Assert.Equal(2, (int)AreaLandmarkKind.Waypoint);
        Assert.Equal(3, (int)AreaLandmarkKind.Transition);
        Assert.Equal(4, (int)AreaLandmarkKind.Mechanic);
        Assert.True(Enum.TryParse<AreaLandmarkKind>("BossHint", out var bossHint));
        Assert.Equal(5, (int)bossHint);
    }

    [Fact]
    public void PublicSnapshot_DoesNotExposeNativeAddressesOrHandles()
    {
        var forbidden = new[] { "Address", "Pointer", "Handle" };
        var properties = typeof(AreaMapSnapshot).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == "FreiAtlas.Core.Area")
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance));

        Assert.DoesNotContain(properties, property =>
            forbidden.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void LoadingSnapshot_HasNoStaleAreaPayload()
    {
        var snapshot = AreaMapSnapshot.Loading(7928, "poe2-2026.07.16", 4);

        Assert.Equal(AreaMapSnapshotStatus.Loading, snapshot.Status);
        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Contents);
        Assert.Empty(snapshot.Landmarks);
        Assert.Null(snapshot.Terrain);
        Assert.Equal(4, snapshot.Area.SessionSequence);
    }

    [Fact]
    public void AreaMapApi_DoesNotExposeProcessMemoryOrNativeAddressMembers()
    {
        var forbidden = new[] { "StartProcess", "ReadMemory", "Address", "Pointer", "Handle" };
        var members = typeof(IAreaMapApi).GetMembers(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(members, member =>
            forbidden.Any(word => member.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
