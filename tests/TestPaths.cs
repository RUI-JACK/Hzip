internal static class TestPaths
{
    public static string Repository { get; } = FindRepository();

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hzip.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the Hzip test fixtures from the test executable directory.");
    }
}
