using System.Diagnostics;
using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Edit observer: rereads the field after the insertion and hands in the edit.
/// </summary>
public sealed class EditLearningTests
{
    /// <summary>A stand-in field whose value changes as the test goes, just as a person changes it.</summary>
    private sealed class FakeField : IFocusedFieldReading
    {
        private readonly Lock gate = new();
        private readonly List<long> readAt = [];
        private string? value;

        public int Captured { get; private set; }

        /// <summary>
        /// When the field was read. The first entry is the baseline inside
        /// <c>BeginWatching</c>, the rest are the scheduled checks: it is their timing that the
        /// settings text promises to a person.
        /// </summary>
        public IReadOnlyList<long> ReadAt
        {
            get
            {
                lock (gate)
                {
                    return readAt.ToArray();
                }
            }
        }

        public string? Value
        {
            get
            {
                lock (gate)
                {
                    return value;
                }
            }

            set
            {
                lock (gate)
                {
                    this.value = value;
                }
            }
        }

        public FocusedFieldHandle? CaptureFocusedField()
        {
            Captured++;
            return new FocusedFieldHandle(() =>
            {
                lock (gate)
                {
                    readAt.Add(Stopwatch.GetTimestamp());
                    return value;
                }
            });
        }
    }

    [Fact]
    public async Task Scenario001()
    {
        var field = new FakeField { Value = "Hello! Open poust girls." };
        var watcher = new EditLearningWatcher(
            field,
            [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60)]);

        (string Original, string Edited)? learned = null;
        watcher.BeginWatching(
            "Open poust girls.",
            (original, edited) => learned = (original, edited));
        // The person corrected the term while the observation was waiting.
        field.Value = "Hello! Open Postgres.";

        for (int attempt = 0; attempt < 100 && learned is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("Open poust girls.", learned?.Original);
        Assert.Equal("Open Postgres.", learned?.Edited);
    }

    [Fact]
    public void Scenario002()
    {
        var field = new FakeField { Value = "Completely different text" };
        var watcher = new EditLearningWatcher(field, [TimeSpan.FromMilliseconds(20)]);

        bool fired = false;
        watcher.BeginWatching("Open the post gerz.", (_, _) => fired = true);

        Assert.Equal(1, field.Captured);
        Assert.False(fired);
    }

    [Fact]
    public async Task Scenario003()
    {
        var field = new FakeField { Value = "First text" };
        var watcher = new EditLearningWatcher(field, [TimeSpan.FromMilliseconds(50)]);

        bool firstFired = false;
        watcher.BeginWatching("First text", (_, _) => firstFired = true);
        // A new dictation before the first check fires.
        field.Value = "Second text";
        watcher.BeginWatching("Second text", (_, _) => { });
        // Editing the "first" no longer concerns anybody.
        field.Value = "First text corrected";

        await Task.Delay(150);
        Assert.False(firstFired, "a cancelled observation is not eligible to teach");
    }

    /// <summary>
    /// Both reads are counted from the insertion, not from each other.
    ///
    /// Spent as a chain of sleeps, the same two numbers read somebody else's field at 8 + 25 = 33
    /// seconds - past the half a minute the settings promise.
    /// </summary>
    [Fact]
    public async Task Scenario005()
    {
        var field = new FakeField { Value = "Hello! Open poust girls." };
        var watcher = new EditLearningWatcher(
            field,
            [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300)]);

        long start = Stopwatch.GetTimestamp();
        // Nobody corrects anything: the observation must use up both checks.
        watcher.BeginWatching("Open poust girls.", (_, _) => { });

        for (int attempt = 0; attempt < 200 && field.ReadAt.Count < 3; attempt++)
        {
            await Task.Delay(5);
        }

        Assert.Equal(3, field.ReadAt.Count);
        Assert.True(
            Stopwatch.GetElapsedTime(start, field.ReadAt[2]) < TimeSpan.FromMilliseconds(450),
            "the second check is at 300 ms from the insertion, not at 200 + 300");
    }

    /// <summary>The schedule numbers are a promise printed in the settings.</summary>
    [Fact]
    public void Scenario006()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(25)],
            EditLearningSchedule.CheckOffsets);
        Assert.True(EditLearningSchedule.CheckOffsets[^1] <= EditLearningSchedule.Limit);
    }

    [Fact]
    public void Scenario007()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(17)],
            EditLearningSchedule.Waits([TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(25)]));
        Assert.Empty(EditLearningSchedule.Waits([]));
    }
}
