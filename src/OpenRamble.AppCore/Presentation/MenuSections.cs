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
    RecentDictations,
    CopyLast,
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
/// </list>
/// </summary>
public static class MenuSections
{
    public static IReadOnlyList<IReadOnlyList<MenuRow>> Sections(
        DictationState state,
        bool isDictationReady,
        bool hasRecoveredText,
        bool hasRecents)
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

                if (hasRecents)
                {
                    // Copy Last gets its own row rather than a label inside the submenu: the
                    // menu shows a shortcut beside Settings and Quit, and the copy key was the
                    // one binding with nowhere to be seen while it lived a level down next to a
                    // dictation rather than next to its own action.
                    sections.Add([MenuRow.RecentDictations, MenuRow.CopyLast]);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        sections.Add([MenuRow.Settings, MenuRow.Quit]);
        return sections;
    }
}
