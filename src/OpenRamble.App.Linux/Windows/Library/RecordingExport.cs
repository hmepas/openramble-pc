using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// Export Transcript… and Export Audio… for a finished recording: a save dialog (starting in
/// Documents, named after the recording), then the file. Shared by the Library's row menu and
/// the detail pane's Export menu.
/// </summary>
internal static class RecordingExport
{
    public static async Task Transcript(Control anchor, AppState state, MeetingRecordingMetadata recording)
    {
        if (await ChooseDestination(anchor, recording, "Export Transcript", "Markdown", "md").ConfigureAwait(true) is not { } path) { return; }
        try
        {
            string markdown = state.ExportTranscript(recording.Id);
            await File.WriteAllTextAsync(path, markdown).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Notice.Show($"Couldn't export the transcript: {error.Message}");
        }
    }

    public static async Task Audio(Control anchor, AppState state, MeetingRecordingMetadata recording)
    {
        if (await ChooseDestination(anchor, recording, "Export Audio", "MPEG-4 audio", "m4a").ConfigureAwait(true) is not { } path) { return; }
        try
        {
            await state.ExportAudio(recording.Id, path).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Notice.Show($"Couldn't export the audio: {error.Message}");
        }
    }

    private static async Task<string?> ChooseDestination(Control anchor, MeetingRecordingMetadata recording, string title, string kind, string extension)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { } storage) { return null; }
        string fallback = RecordingsPlaceholder.DefaultTitle(recording.StartedAt);
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = MeetingExportNaming.FileName(recording.Title ?? string.Empty, fallback) + "." + extension,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(kind) { Patterns = ["*." + extension] }],
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Documents).ConfigureAwait(true),
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }
}
