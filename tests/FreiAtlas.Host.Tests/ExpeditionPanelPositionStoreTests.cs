using System.Drawing;
using FreiAtlas.Host;

namespace FreiAtlas.Host.Tests;

public sealed class ExpeditionPanelPositionStoreTests
{
    [Fact]
    public void SaveAndTryLoad_RoundTripsOnlyScreenPosition()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FreiAtlas-position-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "panel-position.json");
        try
        {
            ExpeditionPanelPositionStore.Save(path, new Point(321, 654));

            var loaded = ExpeditionPanelPositionStore.TryLoad(path);
            var json = File.ReadAllText(path);

            Assert.Equal(new Point(321, 654), loaded);
            Assert.Contains("\"x\":321", json, StringComparison.Ordinal);
            Assert.Contains("\"y\":654", json, StringComparison.Ordinal);
            Assert.DoesNotContain("expanded", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scroll", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void TryLoad_InvalidDocumentReturnsNoPosition()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{invalid-json");

            Assert.Null(ExpeditionPanelPositionStore.TryLoad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
