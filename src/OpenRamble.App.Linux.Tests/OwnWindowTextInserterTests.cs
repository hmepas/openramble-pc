using DictationCore;
using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// Our own windows take dictation directly; every other window still goes through the real inserter.
/// </summary>
public sealed class OwnWindowTextInserterTests
{
    private sealed class RecordingInserter : ITextInserting
    {
        public List<string> Inserted { get; } = [];

        public int Returns { get; private set; }

        public TargetApplication? FrontmostApplication() => null;

        public Task Insert(string text, TargetApplication? target)
        {
            Inserted.Add(text);
            return Task.CompletedTask;
        }

        public Task PressReturn()
        {
            Returns += 1;
            return Task.CompletedTask;
        }
    }

    private sealed class Field : IDictationSink
    {
        public string Text { get; private set; } = string.Empty;

        public bool Accepts { get; set; } = true;

        public bool TryInsert(string text)
        {
            if (!Accepts) { return false; }

            Text += text;
            return true;
        }
    }

    private static Task<bool> Inline(Func<bool> work) => Task.FromResult(work());

    [Fact]
    public async Task TheActiveWindowOfOursTakesTheTextItself()
    {
        var inner = new RecordingInserter();
        var field = new Field();
        var inserter = new OwnWindowTextInserter(inner, () => field, Inline);

        InsertionMarks marks = await inserter.InsertReportingMarks("hello", null);

        Assert.Equal("hello", field.Text);
        Assert.Empty(inner.Inserted);
        Assert.NotNull(marks.PasteDispatchedAt);
    }

    [Fact]
    public async Task NoWindowOfOursMeansTheRealInserter()
    {
        var inner = new RecordingInserter();
        var inserter = new OwnWindowTextInserter(inner, () => null, Inline);

        await inserter.Insert("hello", null);

        Assert.Equal(["hello"], inner.Inserted);
    }

    [Fact]
    public async Task AWindowWithNoFieldOpenHandsOver()
    {
        var inner = new RecordingInserter();
        var field = new Field { Accepts = false };
        var inserter = new OwnWindowTextInserter(inner, () => field, Inline);

        await inserter.Insert("hello", null);

        Assert.Equal(string.Empty, field.Text);
        Assert.Equal(["hello"], inner.Inserted);
    }

    [Fact]
    public async Task ReturnIntoOurOwnFieldIsALineBreak()
    {
        var inner = new RecordingInserter();
        var field = new Field();
        var inserter = new OwnWindowTextInserter(inner, () => field, Inline);

        await inserter.PressReturn();

        Assert.Equal("\n", field.Text);
        Assert.Equal(0, inner.Returns);
    }

    [Fact]
    public async Task ReturnElsewhereIsStillAReturn()
    {
        var inner = new RecordingInserter();
        var inserter = new OwnWindowTextInserter(inner, () => null, Inline);

        await inserter.PressReturn();

        Assert.Equal(1, inner.Returns);
    }
}
