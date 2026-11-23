namespace DictationCore.Tests;

/// <summary>
/// .NET's own lookup runs a tool from the working directory before PATH; this one never does.
/// </summary>
public sealed class ExecutablePathTests : IDisposable
{
    private readonly string name = $"openramble-tool-{Guid.NewGuid():N}";
    private readonly string relative = $"openramble-rel-{Guid.NewGuid():N}";
    private readonly string absolute = Path.Combine(Path.GetTempPath(), $"openramble-bin-{Guid.NewGuid():N}");

    public ExecutablePathTests()
    {
        // Planted where a relative lookup would find it: the working directory and a folder in it.
        Plant(Path.Combine(Environment.CurrentDirectory, name));
        Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, relative));
        Plant(Path.Combine(Environment.CurrentDirectory, relative, name));
        Directory.CreateDirectory(absolute);
    }

    public void Dispose()
    {
        File.Delete(Path.Combine(Environment.CurrentDirectory, name));
        Directory.Delete(Path.Combine(Environment.CurrentDirectory, relative), recursive: true);
        Directory.Delete(absolute, recursive: true);
    }

    [Fact]
    public void TheWorkingDirectoryAndRelativeEntriesAreNeverSearched()
    {
        Assert.Null(ExecutablePath.Find(name, $"::.:{relative}:./{relative}"));
        Assert.Null(ExecutablePath.Find(name, string.Empty));
    }

    [Fact]
    public void AnAbsoluteEntryResolvesToTheFullPath()
    {
        string tool = Path.Combine(absolute, name);
        Plant(tool);

        Assert.Equal(tool, ExecutablePath.Find(name, $".:{relative}:{absolute}"));
    }

    [Fact]
    public void AFileThatCannotBeRunOrAFolderIsPassedOver()
    {
        File.WriteAllText(Path.Combine(absolute, name), "not a program");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(absolute, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        string folder = Path.Combine(Path.GetTempPath(), $"openramble-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, name));
        try
        {
            if (!OperatingSystem.IsWindows()) { Assert.Null(ExecutablePath.Find(name, absolute)); }
            Assert.Null(ExecutablePath.Find(name, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ANameWithASlashIsRefused() =>
        Assert.Null(ExecutablePath.Find($"./{name}", Environment.CurrentDirectory));

    private static void Plant(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
