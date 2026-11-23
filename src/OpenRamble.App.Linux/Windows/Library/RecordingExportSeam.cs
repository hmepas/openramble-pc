using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// TODO(merge): stand-ins for <c>AppState.ExportTranscript</c> and <c>AppState.ExportAudio</c>,
/// which the recording-shortcut/tray/export task adds. Once those instance methods exist they win
/// over these extensions at every call site; delete this file then.
/// </summary>
internal static class RecordingExportSeam
{
    /// <summary>The recording's transcript as Markdown.</summary>
    public static string ExportTranscript(this AppState state, Guid id) =>
        throw new NotSupportedException("Exporting transcripts isn't available in this build yet.");

    /// <summary>The recording's audio as an m4a at <paramref name="destination"/>.</summary>
    public static Task ExportAudio(this AppState state, Guid id, string destination) =>
        throw new NotSupportedException("Exporting audio isn't available in this build yet.");
}
