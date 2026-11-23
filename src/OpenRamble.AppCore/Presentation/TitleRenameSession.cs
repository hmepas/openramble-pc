namespace OpenRamble.AppCore;

/// <summary>Where an inline rename is: closed, shown but not yet holding the caret, or being edited.</summary>
public enum TitleRenamePhase
{
    Closed,
    Opening,
    Editing,
}

/// <summary>
/// An inline title rename. The editor is shown first and takes the caret a moment later, so the
/// focus changes that opening it causes (the second click of a double-click, a closing menu) are
/// not mistaken for the person leaving it: only focus lost while editing, to somewhere else in the
/// same window, commits.
/// </summary>
public sealed class TitleRenameSession
{
    public TitleRenamePhase Phase { get; private set; }

    public bool IsOpen => Phase != TitleRenamePhase.Closed;

    /// <summary>Opens the editor; <c>false</c> when it is open already and must be left alone.</summary>
    public bool Begin()
    {
        if (IsOpen) { return false; }
        Phase = TitleRenamePhase.Opening;
        return true;
    }

    public void EditorFocused()
    {
        if (Phase == TitleRenamePhase.Opening) { Phase = TitleRenamePhase.Editing; }
    }

    /// <summary>
    /// The editor lost the caret; <c>true</c> when that commits. Focus gone to a popup or another
    /// window, or lost before the editor ever had it, keeps the rename open.
    /// </summary>
    public bool FocusLeft(bool toElsewhereInWindow)
    {
        if (Phase != TitleRenamePhase.Editing || !toElsewhereInWindow) { return false; }
        Phase = TitleRenamePhase.Closed;
        return true;
    }

    /// <summary>Return, or Escape: <c>true</c> when there was a rename to end.</summary>
    public bool End()
    {
        if (!IsOpen) { return false; }
        Phase = TitleRenamePhase.Closed;
        return true;
    }
}
