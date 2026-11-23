namespace OpenRamble.AppCore.Tests;

public sealed class TitleRenameSessionTests
{
    [Fact]
    public void FocusLostWhileOpeningKeepsTheRenameOpen()
    {
        var session = new TitleRenameSession();
        Assert.True(session.Begin());
        Assert.False(session.FocusLeft(toElsewhereInWindow: true));
        Assert.Equal(TitleRenamePhase.Opening, session.Phase);
    }

    [Fact]
    public void FocusLeavingTheEditorForTheWindowCommits()
    {
        var session = new TitleRenameSession();
        session.Begin();
        session.EditorFocused();
        Assert.True(session.FocusLeft(toElsewhereInWindow: true));
        Assert.False(session.IsOpen);
    }

    [Fact]
    public void FocusGoneToAPopupOrAnotherWindowKeepsEditing()
    {
        var session = new TitleRenameSession();
        session.Begin();
        session.EditorFocused();
        Assert.False(session.FocusLeft(toElsewhereInWindow: false));
        Assert.Equal(TitleRenamePhase.Editing, session.Phase);
    }

    [Fact]
    public void BeginWhileOpenLeavesTheEditAlone()
    {
        var session = new TitleRenameSession();
        session.Begin();
        session.EditorFocused();
        Assert.False(session.Begin());
        Assert.Equal(TitleRenamePhase.Editing, session.Phase);
    }

    [Fact]
    public void EndClosesOnce()
    {
        var session = new TitleRenameSession();
        Assert.False(session.End());
        session.Begin();
        Assert.True(session.End());
        Assert.False(session.End());
        Assert.False(session.FocusLeft(toElsewhereInWindow: true));
    }
}
