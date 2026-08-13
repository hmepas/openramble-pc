using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// The menu's visibility matrix, pinned as data.
///
/// The menu is the whole interface at rest, and every extra row is a tax on a person mid-thought.
/// These tests document two removals as design decisions:
/// <list type="bullet">
/// <item><description>"Transcribe/Delete Saved Recording" are gone. Failed-transcription
/// recordings are kept quietly on disk and pruned by age; no menu decisions.</description></item>
/// <item><description>"Copy/Delete Saved Text" are gone. Failed-insert words join Recent
/// Dictations at failure time (copyable there), and the one recovery row is "Insert Last
/// Dictation". Do not re-add delete items.</description></item>
/// </list>
/// </summary>
public sealed class MenuSectionsTests
{
    private static MenuRow[][] Sections(
        DictationState state = DictationState.Idle,
        bool ready = true,
        bool recoveredText = false,
        bool recents = false,
        bool copyAsSpoken = false) =>
        MenuSections.Sections(state, ready, recoveredText, recents, copyAsSpoken)
            .Select(section => section.ToArray())
            .ToArray();

    /// <summary>The common case: a ready, quiet app shows three rows in two sections.</summary>
    [Fact]
    public void Scenario001()
    {
        Assert.Equal(
            [[MenuRow.StatusLine], [MenuRow.Settings, MenuRow.Quit]],
            Sections());
    }

    /// <summary>Everything on at once - still at most five top-level row units.</summary>
    [Fact]
    public void Scenario002()
    {
        Assert.Equal(
            [
                [MenuRow.StatusLine],
                [MenuRow.InsertLastDictation],
                [MenuRow.RecentDictations, MenuRow.CopyLastAsSpoken],
                [MenuRow.Settings, MenuRow.Quit],
            ],
            Sections(recoveredText: true, recents: true, copyAsSpoken: true));
    }

    /// <summary>
    /// While a session runs, the menu is about the session - history and recovery wait; nothing
    /// else can be acted on anyway.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        foreach (DictationState state in new[] { DictationState.Preparing, DictationState.Listening })
        {
            Assert.Equal(
                [
                    [MenuRow.StatusLine],
                    [MenuRow.StopAndInsert, MenuRow.CancelDictation],
                    [MenuRow.Settings, MenuRow.Quit],
                ],
                Sections(state, recoveredText: true, recents: true, copyAsSpoken: true));
        }
    }

    /// <summary>
    /// Transcribing lasts seconds and a menu opens slower than that: no cancel row. Escape still
    /// cancels.
    /// </summary>
    [Fact]
    public void Scenario004()
    {
        foreach (DictationState state in new[] { DictationState.Transcribing, DictationState.Inserting })
        {
            Assert.Equal(
                [[MenuRow.StatusLine], [MenuRow.Settings, MenuRow.Quit]],
                Sections(state, recoveredText: true, recents: true));
        }
    }

    /// <summary>Setup rows appear only while something is missing.</summary>
    [Fact]
    public void Scenario005()
    {
        Assert.Equal(
            [
                [MenuRow.StatusLine],
                [MenuRow.SetupHints, MenuRow.FinishSetup],
                [MenuRow.Settings, MenuRow.Quit],
            ],
            Sections(ready: false));
    }

    /// <summary>A person mid-setup keeps access to recovery and history.</summary>
    [Fact]
    public void Scenario006()
    {
        Assert.Equal(
            [
                [MenuRow.StatusLine],
                [MenuRow.SetupHints, MenuRow.FinishSetup],
                [MenuRow.InsertLastDictation],
                [MenuRow.RecentDictations],
                [MenuRow.Settings, MenuRow.Quit],
            ],
            Sections(ready: false, recoveredText: true, recents: true));
    }

    /// <summary>
    /// "Copy Last as Spoken" appears only when it would not duplicate the inserted text - the
    /// policy travels through canCopyAsSpoken.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        Assert.DoesNotContain(MenuRow.CopyLastAsSpoken, Sections(recents: true).SelectMany(section => section));
        Assert.Contains(
            MenuRow.CopyLastAsSpoken,
            Sections(recents: true, copyAsSpoken: true).SelectMany(section => section));
    }

    /// <summary>Settings and Quit close every menu, in every state.</summary>
    [Fact]
    public void Scenario008()
    {
        MenuRow[][][] variants =
        [
            Sections(),
            Sections(DictationState.Listening),
            Sections(DictationState.Transcribing),
            Sections(ready: false, recoveredText: true, recents: true, copyAsSpoken: true),
        ];

        foreach (MenuRow[][] variant in variants)
        {
            Assert.Equal([MenuRow.Settings, MenuRow.Quit], variant[^1]);
        }
    }

    /// <summary>
    /// The removed rows stay removed: no state resurrects a saved-recording or delete item.
    /// </summary>
    [Fact]
    public void Scenario009()
    {
        var everything = new HashSet<MenuRow>(
            Sections(ready: false, recoveredText: true, recents: true, copyAsSpoken: true)
                .SelectMany(section => section)
                .Concat(Sections(DictationState.Listening, recoveredText: true).SelectMany(section => section))
                .Concat(Sections(DictationState.Transcribing, recoveredText: true).SelectMany(section => section)));

        var allowed = new HashSet<MenuRow>
        {
            MenuRow.StatusLine, MenuRow.StopAndInsert, MenuRow.CancelDictation, MenuRow.SetupHints,
            MenuRow.FinishSetup, MenuRow.InsertLastDictation, MenuRow.RecentDictations,
            MenuRow.CopyLastAsSpoken, MenuRow.Settings, MenuRow.Quit,
        };

        Assert.True(everything.IsSubsetOf(allowed));
    }
}
