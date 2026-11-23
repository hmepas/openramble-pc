using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// A recording leaving the PC: its transcript as Markdown, its sound as an m4a, both named after
/// the person's title (upstream AppState export cases).
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class AppStateExportTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly AppHarness harness = new();
    private readonly List<IReadOnlyList<string>> runs = [];
    private int exitCode;

    public AppStateExportTests()
    {
        harness.AudioExporter = new MeetingAudioExporter("/usr/bin/ffmpeg", (_, arguments, _) =>
        {
            runs.Add(arguments);
            File.WriteAllText(arguments[^1]["file:".Length..], "aac");
            return Task.FromResult(exitCode);
        });
    }

    public void Dispose() => harness.Dispose();

    private Guid Stored(
        string? title,
        bool meeting = true,
        bool othersArrived = true,
        bool imported = false,
        params MeetingUtterance[] utterances)
    {
        var store = new MeetingStore(harness.Paths.Recordings());
        MeetingRecordingMetadata metadata = MeetingRecordingMetadata.Create(
            startedAt: Started,
            systemAudio: new SystemAudioSummary(meeting, othersArrived, othersArrived),
            duration: 125,
            title: title,
            microphoneEverDeliveredAudio: true,
            endReason: MeetingEndReason.StoppedByUser,
            transcriptionState: MeetingTranscriptionState.Complete,
            importLanguage: imported ? RecordingImportLanguage.Automatic : null);
        store.Write(metadata);
        store.Write(new MeetingTranscript(utterances), metadata.Id);
        File.WriteAllBytes(Path.Combine(store.Directory(metadata.Id), MeetingStore.AudioFileName), [1, 2, 3]);
        return metadata.Id;
    }

    [Fact]
    public void TheTranscriptIsMarkdownWithYouAndOthers()
    {
        Guid id = Stored(
            "Budget review",
            utterances:
            [
                new MeetingUtterance(MeetingChannel.System, 3, 5, "Shall we start?"),
                new MeetingUtterance(MeetingChannel.Microphone, 1, 2, "Hello."),
            ]);
        AppState state = harness.MakeState();

        string markdown = Assert.IsType<string>(state.ExportTranscript(id));

        Assert.StartsWith("# Budget review\n\n", markdown, StringComparison.Ordinal);
        Assert.Contains("2 min · Meeting", markdown, StringComparison.Ordinal);
        Assert.Contains("**You** · 00:00:01\nHello.\n\n**Others** · 00:00:03\nShall we start?\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("> Only your microphone", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AOneSidedMeetingKeepsTheLineThatSaysSo()
    {
        Guid id = Stored("Call", othersArrived: false, utterances: [new MeetingUtterance(MeetingChannel.Microphone, 0, 1, "Hi.")]);
        AppState state = harness.MakeState();

        Assert.Contains(
            "> Only your microphone was recorded. The other side of this call was not captured.",
            state.ExportTranscript(id),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportedFileHasNoSpeakers()
    {
        Guid id = Stored("Lecture", meeting: false, imported: true, utterances: [new MeetingUtterance(MeetingChannel.Microphone, 61, 62, "Welcome.")]);
        AppState state = harness.MakeState();

        string markdown = state.ExportTranscript(id)!;

        Assert.Contains("\n**00:01:01**\nWelcome.\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("You", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWordsOrNoRecordingIsNothingToExport()
    {
        Guid id = Stored("Silent");
        AppState state = harness.MakeState();

        Assert.Null(state.ExportTranscript(id));
        Assert.Null(state.ExportTranscript(Guid.NewGuid()));
    }

    [Fact]
    public void FileNamesComeFromTheTitleOrTheDate()
    {
        Guid titled = Stored("Q4: plan/review");
        Guid untitled = Stored(null);
        AppState state = harness.MakeState();

        Assert.Equal("Q4  plan review.md", state.SuggestedTranscriptFileName(titled));
        Assert.Equal("Q4  plan review.m4a", state.SuggestedAudioFileName(titled));
        Assert.Equal(
            MeetingExportNaming.FileName(string.Empty, RecordingsPlaceholder.DefaultTitle(Started)) + ".m4a",
            state.SuggestedAudioFileName(untitled));
    }

    [Fact]
    public async Task TheAudioIsEncodedFromTheRecordingToTheChosenFile()
    {
        Guid id = Stored("Budget review");
        AppState state = harness.MakeState();
        string destination = Path.Combine(harness.Root, "Budget review.m4a");
        string source = state.RecordingAudioPath(id)!;

        Assert.True(state.CanExportAudio(id));
        Assert.True(await state.ExportAudio(id, destination));

        IReadOnlyList<string> arguments = Assert.Single(runs);
        Assert.Equal(MeetingAudioExporter.Arguments(source, destination), arguments);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task AFailedAudioExportSaysSo()
    {
        Guid id = Stored("Budget review");
        AppState state = harness.MakeState();
        exitCode = 1;

        Assert.False(await state.ExportAudio(id, Path.Combine(harness.Root, "x.m4a")));

        Assert.Equal("Couldn't save the audio.", state.LastNotice?.Message);
    }
}
