namespace OpenRamble.AppCore;

/// <summary>
/// One meaning per color, everywhere in the app.
///
/// Status colors are a vocabulary, not decoration: the same red that fills the tray dot fills
/// the overlay's recording indicator, so a person learns each color exactly once. Decorative
/// accents use the system accent color and never borrow from this table.
///
/// <list type="bullet">
/// <item><description><c>Recording</c> - the microphone is capturing. Nothing else may be red.</description></item>
/// <item><description><c>Processing</c> - the app is working on speech (transcribing, inserting).</description></item>
/// <item><description><c>Success</c> - a good outcome: permission granted, model ready, trial passed.</description></item>
/// <item><description><c>Attention</c> - something waits for the person, and their work is
/// preserved. Failures here are recoverable by design, so they wear orange, not a destructive
/// red that would cry wolf.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The role, not the color: the actual brushes live in the WPF layer, because they are the
/// only part of the original <c>StatusColors.swift</c> that needs a UI framework.
/// </remarks>
public enum StatusColorRole
{
    Recording,
    Processing,
    Success,
    Attention,
}
