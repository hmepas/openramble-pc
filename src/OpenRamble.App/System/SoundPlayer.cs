using DictationCore;

namespace OpenRamble.App;

/// <summary>
/// The one sound the product has: "look at the panel".
///
/// Not a start chime and not a stop chime. A working dictation already shows itself - the panel
/// while recording, the text at the cursor when it lands - and a sound on every session teaches
/// the ear to ignore sounds. <b>When</b> it plays is decided in <c>DictationCore</c>, by whether
/// the words failed to reach the field; this type only decides what it sounds like.
/// </summary>
/// <remarks>
/// <para>
/// macOS plays "Submarine": low and calm. What happened is recoverable - the text is still
/// offered in the menu - so the sound must not read as an alarm. The Windows counterpart is
/// <c>SystemSounds.Asterisk</c>, the shell's information sound. <c>Exclamation</c> and
/// <c>Hand</c> were both rejected: they are the shell's error tones, and a person who hears one
/// looks for something broken.
/// </para>
/// <para>
/// A system sound, not a bundled file, so that it follows whatever the person has chosen in Sound
/// settings - including having chosen silence.
/// </para>
/// </remarks>
public sealed class SoundPlayer : ISounding
{
    private readonly Func<bool> enabled;

    public SoundPlayer(Func<bool>? enabled = null)
    {
        this.enabled = enabled ?? (() => true);
    }

    public Task PlayAttention()
    {
        if (!enabled())
        {
            return Task.CompletedTask;
        }

        System.Media.SystemSounds.Asterisk.Play();
        return Task.CompletedTask;
    }
}
