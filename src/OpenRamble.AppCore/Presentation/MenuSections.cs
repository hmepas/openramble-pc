using DictationCore;

namespace OpenRamble.AppCore;

/// <summary>
/// A row the menu can offer. <see cref="SetupHints"/> expands to several controls in the view;
/// as policy it is one unit that appears and disappears whole.
/// </summary>
public enum MenuRow
{
    StatusLine,
    StopAndInsert,
    CancelDictation,
    SetupHints,
    FinishSetup,
    InsertLastDictation,
    /// <summary>
    /// The newest takes of this session, each as its own words; a click pastes it into the app
    /// in front. Expands to up to <see cref="MenuSections.TakeLimit"/> rows, the last take first.
    /// </summary>
    Takes,

    /// <summary>
    /// The same takes in a submenu, where a click copies. Upstream reads a held Option key off
    /// the click; a tray menu here is drawn by the panel and never sees a modifier.
    /// </summary>
    CopyTakes,
    Settings,
    Quit,
}

/// <summary>
/// Which rows the menu offers in each state - as data, so the whole matrix is testable without
/// rendering a menu.
///
/// The shape of the answer is sections: the view joins them with separators. Design rules the
/// matrix encodes:
/// <list type="bullet">
/// <item><description>While a session runs, the menu is about the session. Nothing else can be
/// acted on anyway, and a short menu is read faster mid-dictation.</description></item>
/// <item><description>Transcribing is seconds and cannot be meaningfully cancelled from a menu
/// that takes longer to open; Escape still works.</description></item>
/// <item><description>At most ONE recovery row ever appears: "Insert Last Dictation". Failed
/// transcription recordings heal quietly (kept on disk briefly, pruned by age) instead of
/// demanding menu decisions - the strange "Transcribe/Delete Saved Recording" pair is gone by
/// design; do not re-add it.</description></item>
/// <item><description>The newest takes lead the idle menu, the last one first, because the
/// menu is the backstop for words that could not land. Three at most: a menu is not a
/// history.</description></item>
/// </list>
/// </summary>
public static class MenuSections
{
    /// <summary>How many takes the menu offers.</summary>
    public const int TakeLimit = 3;

    public static IReadOnlyList<IReadOnlyList<MenuRow>> Sections(
        DictationState state,
        bool isDictationReady,
        bool hasRecoveredText,
        bool hasTakes)
    {
        var sections = new List<IReadOnlyList<MenuRow>> { new[] { MenuRow.StatusLine } };

        switch (state)
        {
            case DictationState.Preparing:
            case DictationState.Listening:
                sections.Add([MenuRow.StopAndInsert, MenuRow.CancelDictation]);
                break;

            case DictationState.Transcribing:
            case DictationState.Inserting:
                break;

            case DictationState.Idle:
                if (!isDictationReady)
                {
                    sections.Add([MenuRow.SetupHints, MenuRow.FinishSetup]);
                }

                if (hasRecoveredText)
                {
                    sections.Add([MenuRow.InsertLastDictation]);
                }

                if (hasTakes)
                {
                    sections.Add([MenuRow.Takes, MenuRow.CopyTakes]);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        sections.Add([MenuRow.Settings, MenuRow.Quit]);
        return sections;
    }
}
