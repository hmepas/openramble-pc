using System.Runtime.Versioning;
using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// The installer links only to this app, replaces only a link to an earlier copy of it, and
/// removes only its own link.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CommandLineToolInstallerTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("openramble-cli-install-").FullName;

    private string Home => Path.Combine(root, "home");

    private string Link => Path.Combine(Home, ".local", "bin", "openramble");

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Executable(string folder)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
        string path = Path.Combine(directory, "OpenRamble.App.Linux");
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Install_creates_the_folder_and_links_to_the_app()
    {
        string executable = Executable("app");

        string link = CommandLineToolInstaller.Install(executable, Home);

        Assert.Equal(Link, link);
        Assert.Equal(executable, new FileInfo(link).LinkTarget);
        Assert.Equal(link, CommandLineToolInstaller.InstalledLink(executable, Home));
    }

    [Fact]
    public void Install_replaces_a_link_to_an_earlier_copy()
    {
        CommandLineToolInstaller.Install(Executable("old"), Home);
        string current = Executable("new");

        CommandLineToolInstaller.Install(current, Home);

        Assert.Equal(current, new FileInfo(Link).LinkTarget);
    }

    [Fact]
    public void Install_refuses_a_file_or_a_foreign_link()
    {
        string executable = Executable("app");
        Directory.CreateDirectory(Path.GetDirectoryName(Link)!);
        File.WriteAllText(Link, "mine");

        Assert.Throws<CommandLineToolInstaller.Failure>(() => CommandLineToolInstaller.Install(executable, Home));
        Assert.Equal("mine", File.ReadAllText(Link));

        File.Delete(Link);
        File.CreateSymbolicLink(Link, "/usr/bin/something-else");
        Assert.Throws<CommandLineToolInstaller.Failure>(() => CommandLineToolInstaller.Install(executable, Home));
        Assert.Equal("/usr/bin/something-else", new FileInfo(Link).LinkTarget);
    }

    [Fact]
    public void Install_refuses_a_missing_or_non_executable_app()
    {
        string executable = Executable("app");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Throws<CommandLineToolInstaller.Failure>(() => CommandLineToolInstaller.Install(executable, Home));
        Assert.Throws<CommandLineToolInstaller.Failure>(
            () => CommandLineToolInstaller.Install(Path.Combine(root, "nowhere"), Home));
        Assert.False(Path.Exists(Link));
    }

    [Fact]
    public void Uninstall_removes_only_a_link_to_this_app()
    {
        string executable = Executable("app");
        CommandLineToolInstaller.Install(Executable("other"), Home);

        CommandLineToolInstaller.Uninstall(executable, Home);
        Assert.NotNull(new FileInfo(Link).LinkTarget);

        CommandLineToolInstaller.Install(executable, Home);
        CommandLineToolInstaller.Uninstall(executable, Home);
        Assert.False(Path.Exists(Link));
        Assert.Null(CommandLineToolInstaller.InstalledLink(executable, Home));
    }

    [Fact]
    public void Path_check_finds_the_link_folder_with_or_without_a_trailing_slash()
    {
        string folder = Path.Combine(Home, ".local", "bin");

        Assert.True(CommandLineToolInstaller.LinkFolderOnPath(Home, $"/usr/bin:{folder}/"));
        Assert.True(CommandLineToolInstaller.LinkFolderOnPath(Home, folder));
        Assert.False(CommandLineToolInstaller.LinkFolderOnPath(Home, "/usr/bin:/bin"));
    }
}
