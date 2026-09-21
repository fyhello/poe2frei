namespace FreiAtlas.Settings.Tests;

internal static class TestPaths
{
    public static string WorkspaceRoot { get; } = FindWorkspaceRoot();

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreiAtlas.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the FreiAtlas workspace root.");
    }
}
