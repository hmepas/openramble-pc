using System.IO.Compression;
using System.Text;

namespace OpenRamble.AppCore.Tests;

/// <summary>One ZIP, saved where the person chose, holding nothing but sanitized diagnostics.</summary>
/// <remarks>
/// <c>testLowercaseCLIReportIsCollected</c> is not ported: there is no bundled CLI. Crash sources
/// are this port's own records (<c>crash-*.json</c>) rather than Apple's <c>.ips</c>.
/// </remarks>
public sealed class ErrorReportTests : IDisposable
{
    private const string CrashName = "crash-20260927T155445000Z.json";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"report-tests-{Guid.NewGuid()}");

    public ErrorReportTests()
    {
        Directory.CreateDirectory(root);
    }

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

    [Fact]
    public void TestZIPIncludesOnlySanitizedAppDiagnostics()
    {
        string reports = Path.Combine(root, "Reports");
        Directory.CreateDirectory(reports);
        File.WriteAllBytes(Path.Combine(reports, CrashName), CrashReportSanitizerTests.Report());
        File.WriteAllBytes(Path.Combine(reports, "crash-20260927T155446000Z.json"), CrashReportSanitizerTests.Report(app: "another.app"));
        File.WriteAllText(Path.Combine(reports, "crash-20260927T155447000Z.json"), "PRIVATE_CANARY");
        File.WriteAllText(Path.Combine(reports, "crash-PRIVATE_CANARY.json"), "PRIVATE_CANARY");
        File.WriteAllText(Path.Combine(reports, "voice.wav"), "PRIVATE_CANARY");
        using var log = new DictationLogFile(Path.Combine(root, "SupportLogs"), enabled: true);
        log.Record(DiagnosticEvent.DictationListening);
        string zip = Path.Combine(root, "report.zip");

        ErrorReport.Save(zip, log, directories: [reports]);

        string folder = Extract(zip);
        string[] files = Directory.GetFiles(folder);
        Assert.Equal(
            new[] { "README.txt", "crash-1.json", "events.jsonl", "system.json" },
            files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (string file in files)
        {
            Assert.DoesNotContain("PRIVATE_CANARY", File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.Contains("dictationListening", File.ReadAllText(Path.Combine(folder, "events.jsonl")), StringComparison.Ordinal);
        Assert.Contains("skipped: 2", File.ReadAllText(Path.Combine(folder, "README.txt")), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(zip));
        }
    }

    [Fact]
    public void TestNoCrashAndDisabledJournalStillProduceAnHonestReport()
    {
        using var log = new DictationLogFile(Path.Combine(root, "Logs"), enabled: false);
        string zip = Path.Combine(root, "report.zip");
        ErrorReport.Save(zip, log, directories: []);
        string folder = Extract(zip);
        Assert.Empty(File.ReadAllBytes(Path.Combine(folder, "events.jsonl")));
        string readme = File.ReadAllText(Path.Combine(folder, "README.txt"));
        Assert.Contains("No crash record was found", readme, StringComparison.Ordinal);
        Assert.Contains("Local journal: disabled", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSymlinksAndOldReportsAreExcluded()
    {
        string reports = Path.Combine(root, "Reports");
        Directory.CreateDirectory(reports);
        string old = Path.Combine(reports, CrashName);
        File.WriteAllBytes(old, CrashReportSanitizerTests.Report());
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-9));
        string fresh = Path.Combine(root, "crash-20260927T155446000Z.json");
        File.WriteAllBytes(fresh, CrashReportSanitizerTests.Report());
        File.CreateSymbolicLink(Path.Combine(reports, "crash-20260927T155447000Z.json"), fresh);
        string zip = Path.Combine(root, "report.zip");
        using var log = new DictationLogFile(Path.Combine(root, "Logs"), enabled: true);

        ErrorReport.Save(zip, log, directories: [reports]);

        Assert.DoesNotContain("crash-1.json", Directory.GetFiles(Extract(zip)).Select(Path.GetFileName));
    }

    /// <summary>Port-only: the crash a journal recorded is the crash the report carries.</summary>
    [Fact]
    public void TestTheJournalsOwnCrashRecordIsCollectedByDefault()
    {
        using var log = new DictationLogFile(Path.Combine(root, "SupportLogs"), enabled: true);
        log.RecordCrash(new InvalidOperationException("PRIVATE_CANARY"));
        string zip = Path.Combine(root, "report.zip");

        ErrorReport.Save(zip, log);

        string crash = File.ReadAllText(Path.Combine(Extract(zip), "crash-1.json"));
        Assert.Contains("System.InvalidOperationException", crash, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_CANARY", crash, StringComparison.Ordinal);
    }

    /// <summary>Port-only: a failed save throws; a good one replaces the picked file and stages nothing behind.</summary>
    [Fact]
    public void TestSaveReplacesOnlyOnSuccessAndLeavesNoStagingFile()
    {
        string zip = Path.Combine(root, "missing-folder", "report.zip");
        using var log = new DictationLogFile(Path.Combine(root, "Logs"), enabled: true);
        Assert.ThrowsAny<IOException>(() => ErrorReport.Save(zip, log, directories: []));

        string existing = Path.Combine(root, "report.zip");
        File.WriteAllText(existing, "earlier");
        ErrorReport.Save(existing, log, directories: []);
        Assert.NotEqual("earlier", Encoding.UTF8.GetString(File.ReadAllBytes(existing)));
        Assert.Single(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Concat(Directory.GetFiles(root, "report.zip")));
    }

    private string Extract(string zip)
    {
        string output = Path.Combine(root, "Extracted");
        if (Directory.Exists(output)) { Directory.Delete(output, recursive: true); }
        ZipFile.ExtractToDirectory(zip, output);
        return Path.Combine(output, "OpenRamble-Error-Report");
    }
}
