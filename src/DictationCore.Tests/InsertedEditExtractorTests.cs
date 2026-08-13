namespace DictationCore.Tests;

/// <summary>
/// Selecting the edit of the inserted fragment from the contents of another field.
///
/// We know three things: what the field was like immediately after insertion, where our
/// fragment is and what the field became later. The common prefix and suffix are cut off;
/// if the changed middle lies inside our fragment - this is an edit of the
/// dictation, and it can be learned from.
/// </summary>
public sealed class InsertedEditExtractorTests
{
    [Fact]
    public void TestScenario001()
    {
        var edit = InsertedEditExtractor.Extract(
            baseline: "Привет! Открой поуст герз и проверь. Пока.",
            current: "Привет! Открой Postgres и проверь. Пока.",
            inserted: "Открой поуст герз и проверь.");

        Assert.Equal("Открой поуст герз и проверь.", edit?.Original);
        Assert.Equal("Открой Postgres и проверь.", edit?.Edited);
    }

    [Fact]
    public void TestScenario002()
    {
        // The person edited HIS text before our insertion - none of our business.
        var edit = InsertedEditExtractor.Extract(
            baseline: "Превет! Открой поуст герз. Пока.",
            current: "Привет! Открой поуст герз. Пока.",
            inserted: "Открой поуст герз.");

        Assert.Null(edit);
    }

    [Fact]
    public void TestScenario003()
    {
        var edit = InsertedEditExtractor.Extract(
            baseline: "Открой поуст герз.",
            current: "Открой поуст герз.",
            inserted: "Открой поуст герз.");

        Assert.Null(edit);
    }

    [Fact]
    public void TestScenario004()
    {
        // The field has been cleared or rewritten entirely - there is nothing to learn from.
        var edit = InsertedEditExtractor.Extract(
            baseline: "Совсем другой текст.",
            current: "Ещё более другой.",
            inserted: "Открой поуст герз.");

        Assert.Null(edit);
    }

    [Fact]
    public void TestScenario005()
    {
        // The person continued typing after inserting - the fragment did not change.
        var edit = InsertedEditExtractor.Extract(
            baseline: "Открой поуст герз.",
            current: "Открой поуст герз. И ещё кое-что.",
            inserted: "Открой поуст герз.");

        Assert.Null(edit);
    }

    [Fact]
    public void TestScenario006()
    {
        // Corrected the first word of the fragment: the common prefix ends in someone
        // else's text, but the changed middle is still inside the insert.
        var edit = InsertedEditExtractor.Extract(
            baseline: "Заметка: гитхаб лежит.",
            current: "Заметка: GitHub лежит.",
            inserted: "гитхаб лежит.");

        Assert.Equal("гитхаб лежит.", edit?.Original);
        Assert.Equal("GitHub лежит.", edit?.Edited);
    }

    [Fact]
    public void TestScenario007()
    {
        var edit = InsertedEditExtractor.Extract(
            baseline: "Открой поуст герз.",
            current: "",
            inserted: "Открой поуст герз.");

        Assert.Null(edit);
    }
}
