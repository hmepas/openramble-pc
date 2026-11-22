using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// A crash, kept for an error report, holds types and methods - never what the exception said.
/// </summary>
/// <remarks>
/// The Swift suite exercises Apple's <c>.ips</c> format, which no platform of this port produces.
/// Its intent is ported against this port's own record: personal fields are redacted without
/// losing the stack (<c>testRedactsPersonalFieldsWithoutLosingStackOrImageIndices</c>), another
/// app's record is rejected (<c>testRejectsOtherAppsEvenIfFileIsNamedOpenRamble</c>), and a
/// malformed file is not copied raw (<c>testMalformedAndFutureReportsAreNotCopiedRaw</c>).
/// <c>testSignedCLIReportWithoutBundleInfoIsAccepted</c> is not ported: there is no bundled CLI.
/// </remarks>
public sealed class CrashReportSanitizerTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 27, 15, 54, 45, TimeSpan.Zero);

    /// <summary>A record made from a real thrown exception, so the stack is real too.</summary>
    internal static byte[] Report(string app = CrashReportSanitizer.AppName)
    {
        byte[] record;
        try
        {
            ThrowWithPrivateText();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception error)
        {
            record = CrashReportSanitizer.Record(error, "0.30.3", "70", Moment);
        }

        if (app == CrashReportSanitizer.AppName) { return record; }
        JsonNode node = JsonNode.Parse(record)!;
        node["app"] = app;
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static void ThrowWithPrivateText()
    {
        try
        {
            throw new FileNotFoundException("PRIVATE_CANARY", "/home/PRIVATE_CANARY/notes.txt");
        }
        catch (FileNotFoundException inner)
        {
            throw new InvalidOperationException("PRIVATE_CANARY /home/PRIVATE_CANARY/take.wav", inner) { Data = { ["user"] = "PRIVATE_CANARY" } };
        }
    }

    [Fact]
    public void TestRedactsPersonalFieldsWithoutLosingTheStack()
    {
        string text = Encoding.UTF8.GetString(Report());
        Assert.DoesNotContain("PRIVATE_CANARY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowWithPrivateText), text, StringComparison.Ordinal);

        JsonNode body = JsonNode.Parse(text)!;
        JsonArray exceptions = body["exceptions"]!.AsArray();
        Assert.Equal(2, exceptions.Count);
        Assert.Equal("System.InvalidOperationException", (string?)exceptions[0]!["type"]);
        Assert.Equal("System.IO.FileNotFoundException", (string?)exceptions[1]!["type"]);
        Assert.NotEmpty(exceptions[0]!["frames"]!.AsArray());
        Assert.Equal("0.30.3", (string?)body["version"]);
    }

    /// <summary>
    /// Read back from disk, a record passes the allowlist again: fields the file grew are dropped
    /// and a value that is not an identifier is replaced, not copied - but in its place, so the
    /// stack keeps its shape.
    /// </summary>
    [Fact]
    public void TestATamperedRecordIsCleanedAgainOnExport()
    {
        JsonNode node = JsonNode.Parse(Report())!;
        node["message"] = "PRIVATE_CANARY";
        node["exceptions"]![0]!["frames"]![0]!["method"] = "/home/PRIVATE_CANARY/secret.cs";
        node["exceptions"]![0]!["frames"]![0]!["file"] = "PRIVATE_CANARY";
        node["exceptions"]![0]!["type"] = "Leaked text PRIVATE_CANARY";
        node["version"] = "PRIVATE CANARY";

        byte[]? sanitized = CrashReportSanitizer.Sanitize(Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.NotNull(sanitized);
        string text = Encoding.UTF8.GetString(sanitized);
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        JsonNode body = JsonNode.Parse(text)!;
        Assert.Equal("redacted", (string?)body["exceptions"]![0]!["frames"]![0]!["method"]);
        Assert.Equal("redacted", (string?)body["exceptions"]![0]!["type"]);
        Assert.Equal("unknown", (string?)body["version"]);
        Assert.Equal(
            node["exceptions"]![0]!["frames"]!.AsArray().Count,
            body["exceptions"]![0]!["frames"]!.AsArray().Count);
    }

    [Fact]
    public void TestRejectsOtherApps() =>
        Assert.Null(CrashReportSanitizer.Sanitize(Report(app: "another.app")));

    [Fact]
    public void TestMalformedReportsAreNotCopiedRaw() =>
        Assert.ThrowsAny<JsonException>(() => CrashReportSanitizer.Sanitize(Encoding.UTF8.GetBytes("private text")));
}
