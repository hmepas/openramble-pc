namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The m4a a person sends someone (upstream MeetingAudioExporterTests, as ffmpeg arguments: the
/// mixing and encoding are ffmpeg's, what is ours is asking for the right file and leaving
/// nothing behind).
/// </summary>
public sealed class MeetingAudioExporterTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("or-export-").FullName;
    private readonly string source;
    private readonly string destination;

    public MeetingAudioExporterTests()
    {
        source = Path.Combine(root, "audio.wav");
        destination = Path.Combine(root, "out", "Budget review.m4a");
        File.WriteAllBytes(source, [1, 2, 3]);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class FakeRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        public int ExitCode { get; set; }

        public bool WritesOutput { get; set; } = true;

        public bool Hangs { get; set; }

        public async Task<int> Run(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Runs.Add((executable, arguments));
            string output = arguments[^1]["file:".Length..];
            if (WritesOutput) { await File.WriteAllTextAsync(output, "aac", CancellationToken.None); }
            if (Hangs) { await Task.Delay(Timeout.Infinite, cancellationToken); }
            return ExitCode;
        }
    }

    [Fact]
    public void TheArgumentsAskForMonoAacAt32KbpsInM4a()
    {
        IReadOnlyList<string> arguments = MeetingAudioExporter.Arguments(source, destination);

        Assert.Equal("file:" + source, arguments[arguments.ToList().IndexOf("-i") + 1]);
        Assert.Equal("file:" + destination, arguments[^1]);
        Assert.Equal("1", arguments[arguments.ToList().IndexOf("-ac") + 1]);
        Assert.Equal("aac", arguments[arguments.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal("32k", arguments[arguments.ToList().IndexOf("-b:a") + 1]);
        Assert.Equal("ipod", arguments[arguments.ToList().IndexOf("-f") + 1]);
        Assert.Contains("-nostdin", arguments);
        Assert.Contains("-vn", arguments);
    }

    [Fact]
    public async Task ASuccessfulExportRunsFfmpegOnceAndKeepsTheFile()
    {
        var runner = new FakeRunner();
        var exporter = new MeetingAudioExporter("/usr/bin/ffmpeg", runner.Run);

        Assert.True(await exporter.Export(source, destination));

        var run = Assert.Single(runner.Runs);
        Assert.Equal("/usr/bin/ffmpeg", run.Executable);
        Assert.Equal(MeetingAudioExporter.Arguments(source, destination), run.Arguments);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task AFailedExportLeavesNothingBehind()
    {
        var runner = new FakeRunner { ExitCode = 1 };
        var exporter = new MeetingAudioExporter("/usr/bin/ffmpeg", runner.Run);

        Assert.False(await exporter.Export(source, destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ACancelledExportLeavesNothingBehind()
    {
        var runner = new FakeRunner { Hangs = true };
        var exporter = new MeetingAudioExporter("/usr/bin/ffmpeg", runner.Run);
        using var cancellation = new CancellationTokenSource();

        Task<bool> export = exporter.Export(source, destination, cancellation.Token);
        await TestWait.For("ffmpeg running", () => File.Exists(destination));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task AMissingRecordingOrFfmpegIsReportedNotRun()
    {
        var runner = new FakeRunner();

        Assert.False(await new MeetingAudioExporter("/usr/bin/ffmpeg", runner.Run).Export(Path.Combine(root, "gone.wav"), destination));
        var without = new MeetingAudioExporter(null, runner.Run);
        Assert.False(without.IsAvailable);
        Assert.False(await without.Export(source, destination));
        Assert.Empty(runner.Runs);
    }
}
