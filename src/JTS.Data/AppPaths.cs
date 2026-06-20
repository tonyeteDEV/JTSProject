namespace JTS.Data;

public static class AppPaths
{
    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JTS");

    public static string ModelsDirectory { get; } = Path.Combine(AppDataRoot, "models");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(ModelsDirectory);
    }
}
