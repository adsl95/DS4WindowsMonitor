namespace DS4WindowsMonitor;

internal static class AppPaths
{
    public static string Root { get; private set; } = string.Empty;
    public static bool UsingFallbackRoot { get; private set; }

    public static void Initialize()
    {
        string exeRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (TryUseRoot(exeRoot))
        {
            Root = exeRoot;
            UsingFallbackRoot = false;
            return;
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DS4WindowsMonitor");
        Directory.CreateDirectory(fallback);
        Root = fallback;
        UsingFallbackRoot = true;
    }

    public static string DirectoryFor(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryUseRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            string testPath = Path.Combine(root, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(testPath, "ok");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
