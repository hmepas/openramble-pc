namespace OpenRamble.Cli.Tests;

/// <summary>
/// Exit codes and the checks made before any model loads. <see cref="CommandLineTool.Run"/>
/// writes to the process's console and resolves the app's data folder from the environment, so
/// these tests run alone, with both redirected into a temporary folder.
/// </summary>
[Collection(nameof(ConsoleCollection))]
public sealed class RunTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("openramble-cli-").FullName;
    private readonly string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? modelsRoot = Environment.GetEnvironmentVariable("OPENRAMBLE_MODELS_ROOT");
    private readonly TextWriter output = Console.Out;
    private readonly TextWriter error = Console.Error;
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();

    public RunTests()
    {
        // Never the user's own data folder or model.
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable("OPENRAMBLE_MODELS_ROOT", Path.Combine(root, "models"));
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }

    public void Dispose()
    {
        Console.SetOut(output);
        Console.SetError(error);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
        Environment.SetEnvironmentVariable("OPENRAMBLE_MODELS_ROOT", modelsRoot);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void HelpPrintsTheUsageAndSucceeds()
    {
        Assert.Equal(0, CommandLineTool.Run(["--help"]));
        Assert.Contains(CommandLineTool.Usage, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AUsageErrorIs64AndGoesToStandardError()
    {
        Assert.Equal(64, CommandLineTool.Run(["talk.wav", "--format", "docx"]));
        Assert.Equal("Unknown format: docx", stderr.ToString().Trim());
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public void NoInstalledModelIs69()
    {
        Assert.Equal(69, CommandLineTool.Run(["--check-model"]));
        Assert.Contains("Model is not installed", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUninstalledModelIsReportedBeforeAnyInputIsRead()
    {
        Assert.Equal(69, CommandLineTool.Run(["missing.wav"]));
        Assert.DoesNotContain("missing.wav", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void APreparedModelFolderPassesTheCheck()
    {
        string model = ModelFolder("model.gguf");

        Assert.Equal(0, CommandLineTool.Run(["--check-model", "--model-dir", model]));
        Assert.Equal("Model is installed and available.", stderr.ToString().Trim());
    }

    [Theory]
    [InlineData]
    [InlineData("readme.txt")]
    [InlineData("a.gguf", "b.GGUF")]
    public void AModelFolderWithoutExactlyOneModelIs69(params string[] files)
    {
        string model = ModelFolder(files);

        Assert.Equal(69, CommandLineTool.Run(["--check-model", "--model-dir", model]));
        Assert.Equal("The model directory must contain exactly one readable GGUF model.", stderr.ToString().Trim());
    }

    [Fact]
    public void AMissingModelFolderIs69() =>
        Assert.Equal(69, CommandLineTool.Run(["--check-model", "--model-dir", Path.Combine(root, "nowhere")]));

    [Fact]
    public void ExistingTranscriptsAreLeftUnchangedAndTheRunFails()
    {
        string model = ModelFolder("model.gguf");
        string results = Path.Combine(root, "separate results");
        Directory.CreateDirectory(results);
        string first = Path.Combine(results, "spoken fixture.json");
        string second = Path.Combine(results, "spoken fixture-2.json");
        File.WriteAllText(first, "earlier");
        File.WriteAllText(second, "earlier too");

        int code = CommandLineTool.Run(
            ["spoken fixture.wav", "spoken fixture.wav", "--format", "json", "--output-dir", results, "--model-dir", model]);

        Assert.Equal(1, code);
        Assert.Equal("earlier", File.ReadAllText(first));
        Assert.Equal("earlier too", File.ReadAllText(second));
        Assert.Contains($"Already exists, left unchanged: {first}", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Already exists, left unchanged: {second}", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(results, ".openramble-*.tmp"));
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public void CheckingTheModelAloneCreatesNoOutputDirectory()
    {
        string model = ModelFolder("model.gguf");
        string results = Path.Combine(root, "new", "results");

        Assert.Equal(0, CommandLineTool.Run(["--check-model", "--model-dir", model, "--output-dir", results]));
        Assert.False(Directory.Exists(results));
    }

    private string ModelFolder(params string[] files)
    {
        string folder = Path.Combine(root, $"model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (string file in files) { File.WriteAllText(Path.Combine(folder, file), ""); }
        return folder;
    }
}

/// <summary>Tests that redirect the console or change the environment, one at a time.</summary>
[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;
