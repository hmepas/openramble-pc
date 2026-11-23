using System.Text;
using DictationCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>The local journal: allowlisted, bounded, and empty when the person said no.</summary>
/// <remarks>
/// Not ported: <c>testCompletedDictationRecordsFullEngineTimeDiskReadsAndIdle</c> - the total
/// engine time, lost audio, disk reads and engine idle time are not measured by this port.
/// <c>testFailureReasonIsACaseNameThatSurvivesExport</c> is adapted to the failure stage, and
/// the paste failure's own case name has <see cref="APasteFailureIsRecordedByItsKind"/>: the
/// invariant both pin - a value outside the allowlist cannot be smuggled in - is the same.
/// </remarks>
public sealed class DictationLogFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"journal-tests-{Guid.NewGuid()}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Text(byte[] data) => Encoding.UTF8.GetString(data);

    private static string[] Lines(byte[] data) => Text(data).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TestEnabledByDefaultAndWritesSurviveReopening()
    {
        Assert.True(SettingsDefaults.DetailedLogging);
        byte[] first;
        using (var log = new DictationLogFile(root, enabled: true))
        {
            log.Record(DiagnosticEvent.DictationListening);
            (byte[] data, _, bool incomplete) = log.Snapshot();
            Assert.False(incomplete);
            Assert.Contains("dictationListening", Text(data), StringComparison.Ordinal);
            first = data;
        }

        using var reopened = new DictationLogFile(root, enabled: true);
        Assert.Equal(first, reopened.Snapshot().Data);
    }

    [Fact]
    public void TestOptOutCreatesNothingAndClearsEarlierEvents()
    {
        using var log = new DictationLogFile(root, enabled: false);
        log.Record(DiagnosticEvent.AppLaunched);
        Assert.Empty(log.Snapshot().Data);
        Assert.False(Directory.Exists(root));
        log.IsEnabled = true;
        log.Record(DiagnosticEvent.AppLaunched);
        Assert.NotEmpty(log.Snapshot().Data);
        log.IsEnabled = false;
        log.Record(DiagnosticEvent.DictationListening);
        Assert.Empty(log.Snapshot().Data);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void TestSevenDayAndTotalByteLimitsKeepNewestCompleteEntries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var log = new DictationLogFile(root, enabled: true, maxBytes: 1000);
        log.Record(DiagnosticEvent.EngineFailed, at: now.AddDays(-8));
        for (int i = 0; i < 100; i += 1) { log.Record(DiagnosticEvent.DictationListening, milliseconds: i, at: now); }
        log.Record(DiagnosticEvent.AppTerminationReady, at: now);
        (byte[] data, _, bool incomplete) = log.Snapshot(now);
        Assert.True(data.Length <= 1000);
        Assert.DoesNotContain("engineFailed", Text(data), StringComparison.Ordinal);
        Assert.Contains("appTerminationReady", Text(data), StringComparison.Ordinal);
        long bytes = new DirectoryInfo(root).EnumerateFiles().Sum(file => file.Length);
        Assert.True(bytes <= 1000);
        Assert.False(incomplete);
    }

    [Fact]
    public void TestExportReencodesKnownFieldsAndSkipsInterruptedWrites()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.Record(DiagnosticEvent.DictationListening);
        _ = log.Snapshot();
        string file = Directory.GetFiles(root).Single();
        string text = File.ReadAllText(file);
        text = text.Replace("\"event\":", "\"private\":\"PRIVATE_CANARY\",\"event\":", StringComparison.Ordinal);
        text += "{\"partial\":\"PRIVATE_CANARY";
        File.WriteAllText(file, text);
        (byte[] data, _, bool incomplete) = log.Snapshot();
        Assert.True(incomplete);
        Assert.DoesNotContain("PRIVATE_CANARY", Text(data), StringComparison.Ordinal);
        Assert.Contains("dictationListening", Text(data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConcurrentWritersProduceCompleteEntries()
    {
        using var log = new DictationLogFile(root, enabled: true);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => log.Record(DiagnosticEvent.DictationCompleted, milliseconds: i))));
        (byte[] data, _, bool incomplete) = log.Snapshot();
        Assert.Equal(100, Lines(data).Length);
        Assert.False(incomplete);
    }

    [Fact]
    public void TestRecognitionFailureKeepsNativeCodeWithoutErrorText()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.RecordRecognitionFailure(new ASREngineError.InferenceFailed("PRIVATE_CANARY /home/someone/take.wav", code: -7));
        log.RecordRecognitionFailure(new ASREngineError.UnsupportedAudioFormat("PRIVATE_CANARY"));
        string text = Text(log.Snapshot().Data);
        Assert.Contains("recognitionEngineFailed", text, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\":-7", text, StringComparison.Ordinal);
        Assert.Contains("recognitionAudioInvalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_CANARY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestRecognitionTimingsSurviveExportAlongsideOlderEntries()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.Record(DiagnosticEvent.AppLaunched);
        log.Record(
            DiagnosticEvent.DictationCompleted,
            milliseconds: 52000,
            recognition: new DictationLogFile.Recognition
            {
                AudioMilliseconds = 480000,
                EngineMilliseconds = 51900,
                DecodingMilliseconds = 50,
                StreamedSegments = 23,
                FileBacked = true,
            });
        (byte[] data, _, bool incomplete) = log.Snapshot();
        string text = Text(data);
        Assert.False(incomplete);
        Assert.Equal(2, Lines(data).Length);
        Assert.Contains("\"streamedSegments\":23", text, StringComparison.Ordinal);
        Assert.Contains("\"engineMilliseconds\":51900", text, StringComparison.Ordinal);
        Assert.Contains("\"fileBacked\":true", text, StringComparison.Ordinal);
        Assert.Null(DictationLogFile.Milliseconds(double.PositiveInfinity));
        Assert.Null(DictationLogFile.Milliseconds(double.NaN));
    }

    /// <summary>
    /// Adapted from testFailureReasonIsACaseNameThatSurvivesExport: the allowlisted case name here
    /// is the failure stage.
    /// </summary>
    [Fact]
    public void TestFailureStageIsACaseNameThatSurvivesExport()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.Record(DiagnosticEvent.DictationStageFailed, stage: DictationFailureStage.Recognition);
        log.Record(DiagnosticEvent.DictationFailed);
        (byte[] data, _, bool incomplete) = log.Snapshot();
        string[] lines = Lines(data);
        Assert.False(incomplete);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"stage\":\"recognition\"", lines[0], StringComparison.Ordinal);
        Assert.False(lines[1].Contains("stage", StringComparison.Ordinal), "an older line without one still exports");

        // A value outside the allowlist cannot be smuggled in through the file.
        string file = Directory.GetFiles(root).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"recognition\"", "\"PRIVATE_CANARY\"", StringComparison.Ordinal));
        (byte[] reread, _, bool rereadIncomplete) = log.Snapshot();
        Assert.True(rereadIncomplete);
        Assert.DoesNotContain("PRIVATE_CANARY", Text(reread), StringComparison.Ordinal);
    }

    [Fact]
    public void TestFailureStageAndRecoveredStreamErrorStayPrivateAndSurviveExport()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.Record(DiagnosticEvent.DictationStageFailed, milliseconds: 10001, stage: DictationFailureStage.CaptureFreeze, timedOut: true);
        log.RecordFallback(
            DictationFallbackReason.SegmentFailed,
            new ASREngineError.InferenceFailed("PRIVATE_CANARY /home/private/take.wav", code: -7));
        (byte[] data, _, bool incomplete) = log.Snapshot();
        string text = Text(data);
        Assert.False(incomplete);
        Assert.Contains("\"stage\":\"captureFreeze\"", text, StringComparison.Ordinal);
        Assert.Contains("\"timedOut\":true", text, StringComparison.Ordinal);
        Assert.Contains("\"fallbackReason\":\"segmentFailed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\":-7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_CANARY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// How long the microphone took to deliver its first frame, and how long key-up took to reach
    /// the paste, are numbers every take already measured. Numbers only: nothing of the take itself.
    /// </summary>
    [Fact]
    public void TestCompletedDictationRecordsMicrophoneStartupAndPaste()
    {
        var phases = new DictationPhaseBreakdown(
            CaptureFreeze: TimeSpan.FromMilliseconds(12),
            EnginePreparation: TimeSpan.FromMilliseconds(3),
            Recognition: TimeSpan.FromMilliseconds(150),
            EngineProcessing: TimeSpan.FromMilliseconds(140),
            AudioDuration: TimeSpan.FromSeconds(4));
        DictationLogFile.Recognition? measured = DictationLogFile.Recognition.From(new DictationSpeedReport(
            ToRecognizedText: TimeSpan.FromMilliseconds(170),
            ToPasteDispatched: TimeSpan.FromMilliseconds(190),
            MicrophoneStartup: TimeSpan.FromMilliseconds(212),
            Phases: phases,
            ToFirstRead: TimeSpan.FromMilliseconds(260)));
        DictationLogFile.Recognition? unmeasured = DictationLogFile.Recognition.From(new DictationSpeedReport(
            ToRecognizedText: TimeSpan.FromMilliseconds(170),
            Phases: phases));
        Assert.NotNull(measured);
        Assert.NotNull(unmeasured);
        Assert.True(
            DictationLogFile.Recognition.From(new DictationSpeedReport(TimeSpan.FromMilliseconds(170))) is null,
            "a report without its breakdown records no recognition, as before");

        using var log = new DictationLogFile(root, enabled: true);
        log.Record(DiagnosticEvent.DictationCompleted, milliseconds: 170, recognition: measured);
        log.Record(DiagnosticEvent.DictationCompleted, milliseconds: 170, recognition: unmeasured);
        (byte[] data, _, bool incomplete) = log.Snapshot();
        Assert.False(incomplete);
        string[] lines = Lines(data);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"microphoneStartupMilliseconds\":212", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"pasteMilliseconds\":190", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"readMilliseconds\":260", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"preparationMilliseconds\":3", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("microphoneStartupMilliseconds", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("pasteMilliseconds", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("readMilliseconds", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Which kind of paste did not land is a case name from an allowlist, and a line naming a kind
    /// the allowlist does not know is dropped from a report, not copied into it.
    /// </summary>
    [Fact]
    public void APasteFailureIsRecordedByItsKind()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.Record(DiagnosticEvent.DictationWarning, failureReason: DictationFailureReason.PasteUnread);
        log.Record(DiagnosticEvent.DictationWarning, failureReason: (DictationFailureReason)999);
        (byte[] data, _, _) = log.Snapshot();
        string[] lines = Lines(data);
        Assert.Single(lines);
        Assert.Contains("\"failureReason\":\"pasteUnread\"", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A line that fails for a reason other than the disk costs that line, not the journal.
    ///
    /// The writer is one thread for the whole run. An exception it did not expect used to end
    /// it - and with it the process, or, where the process survived, every later barrier, which
    /// waited for a thread that was gone. An undefined event is the stand-in here: the
    /// allowlist's converter refuses to write it, and refusing is a <see cref="System.Text.Json.JsonException"/>,
    /// not an I/O error.
    /// </summary>
    [Fact]
    public void TestAnUnexpectedWriteErrorMarksTheJournalIncompleteAndKeepsItRunning()
    {
        using var log = new DictationLogFile(root, enabled: true);
        log.Record((DiagnosticEvent)999);
        log.Record(DiagnosticEvent.DictationListening);

        (byte[] data, _, bool incomplete) = log.Snapshot();

        Assert.True(incomplete);
        Assert.Contains("dictationListening", Assert.Single(Lines(data)), StringComparison.Ordinal);
    }

    // MARK: - Port-only: where it lives, file modes and crash records

    /// <summary>Beside the app's other files, under <c>SupportLogs</c>, one file per UTC date.</summary>
    [Fact]
    public void TestItLivesBesideTheAppsOtherFiles()
    {
        var paths = new AppPaths(root);
        Assert.Equal(Path.Combine(paths.Support(), "SupportLogs"), DictationLogFile.DirectoryUnder(paths));

        string journal = Path.Combine(root, "journal");
        using var log = new DictationLogFile(journal, enabled: true);
        log.Record(DiagnosticEvent.AppLaunched, at: new DateTimeOffset(2026, 8, 21, 23, 30, 0, TimeSpan.FromHours(-3)));
        log.Flush();
        Assert.Equal(["events-2026-08-22.jsonl"], Directory.GetFiles(journal).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Directory.GetFiles(journal).Single()));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(journal));
        }
    }

    /// <summary>
    /// A crash leaves a sanitized record beside the journal, and only while the journal is on;
    /// turning it off clears that record too.
    /// </summary>
    [Fact]
    public void TestCrashRecordFollowsTheSwitch()
    {
        var error = new InvalidOperationException("PRIVATE_CANARY /home/someone/notes.txt");
        using var log = new DictationLogFile(root, enabled: false);
        log.RecordCrash(error);
        Assert.False(Directory.Exists(root));

        log.IsEnabled = true;
        log.RecordCrash(error);
        string crash = Assert.Single(Directory.GetFiles(root, "crash-*.json"));
        string text = File.ReadAllText(crash);
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_CANARY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);

        log.IsEnabled = false;
        Assert.Empty(Directory.GetFiles(root));
    }
}
