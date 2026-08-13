using System.Text;
using System.Text.Json;

namespace DictationCore.Tests;

/// <summary>
/// Replacement of a term in a case.
///
/// The Russian word is inflected, but the Latin term is not. A person says "перед
/// релизом" and waits for "перед release". An exact match never worked here,
/// and the dictionary was silent exactly where it was needed.
/// </summary>
public sealed class DictionaryDeclensionTests
{
    private static string Apply((string Spoken, string Written)[] pairs, string text) =>
        DictionaryReplacements.Apply(
            pairs.Select(pair => new DictionaryReplacement(spoken: pair.Spoken, written: pair.Written)).ToList(),
            text);

    [Fact]
    public void TestCasesOfASingleWordTerm()
    {
        (string, string)[] pairs = [("релиз", "release")];
        Assert.Equal("проверь release", Apply(pairs, "проверь релиз"));
        Assert.Equal("перед release", Apply(pairs, "перед релизом"));
        Assert.Equal("после release", Apply(pairs, "после релиза"));
        Assert.Equal("в release", Apply(pairs, "в релизе"));
        Assert.Equal("к release", Apply(pairs, "к релизу"));
    }

    [Fact]
    public void TestTermsEndingInShortI()
    {
        // "Деплой" is inflected, eating the "й": деплоя, деплою, деплоем.
        (string, string)[] pairs = [("деплой", "deploy")];
        Assert.Equal("запусти deploy", Apply(pairs, "запусти деплой"));
        Assert.Equal("после deploy", Apply(pairs, "после деплоя"));
        Assert.Equal("готовимся к deploy", Apply(pairs, "готовимся к деплою"));
        Assert.Equal("перед deploy", Apply(pairs, "перед деплоем"));
    }

    [Fact]
    public void TestPluralsAndCasesTogether()
    {
        Assert.Equal("подними Docker", Apply([("докер", "Docker")], "подними докеры"));
        Assert.Equal("три commit назад", Apply([("коммит", "commit")], "три коммита назад"));
        Assert.Equal("бэкенд на Python", Apply([("питон", "Python")], "бэкенд на питоне"));
        Assert.Equal("без downtime", Apply([("даунтайм", "downtime")], "без даунтайма"));
    }

    [Fact]
    public void TestLastWordOfAPhraseAlsoInflects()
    {
        (string, string)[] pairs = [("пул реквест", "pull request")];
        Assert.Equal("отправь pull request", Apply(pairs, "отправь пул реквест"));
        Assert.Equal("жду pull request", Apply(pairs, "жду пул реквеста"));
        Assert.Equal("в pull request", Apply(pairs, "в пул реквесте"));
    }

    [Fact]
    public void TestExtraSpacesInsideAPhraseDoNotBreakIt()
    {
        // Recognition does not promise exactly one space.
        Assert.Equal(
            "сделай code review",
            Apply([("код ревью", "code review")], "сделай код  ревью"));
    }

    [Fact]
    public void TestUnrelatedWordsWithTheSameBeginningAreLeftAlone()
    {
        // The main risk of such a replacement is eating someone else's word.
        Assert.Equal("религия", Apply([("релиз", "release")], "религия"));
        Assert.Equal("билдинг", Apply([("билд", "build")], "билдинг"));
        Assert.Equal("коммитить", Apply([("коммит", "commit")], "коммитить"));
        Assert.Equal("докерфайл", Apply([("докер", "Docker")], "докерфайл"));
    }

    [Fact]
    public void TestShortTermsStayExact()
    {
        // In short words, the tail too often turns out to be the beginning of another.
        Assert.Equal("API отдаёт ответ", Apply([("апи", "API")], "апи отдаёт ответ"));
        Assert.Equal("апиной", Apply([("апи", "API")], "апиной"));
    }

    [Fact]
    public void TestLatinTermsAreNotGivenRussianEndings()
    {
        // English words in Russian speech are not inflected, and an extra tail there
        // would mean a completely different word.
        Assert.Equal("swifty", Apply([("swift", "Swift")], "swifty"));
        Assert.Equal("пишем на Swift", Apply([("swift", "Swift")], "пишем на swift"));
    }

    [Fact]
    public void TestStarterDictionaryCoversWhatTheModelActuallyProduces()
    {
        // The lines are taken from an actual model run, not made up.
        (string Heard, string Expected)[] measured =
        [
            ("Отправь пул реквист на ревью", "pull request"),
            ("Сделай кот ревью", "code review"),
            ("задача в гитхеб", "GitHub"),
            ("базы данных постгриз", "Postgres"),
            ("выкати ход фикс", "hotfix"),
            ("фронтинг на тайп-скрипте", "TypeScript"),
            ("написано на свифте", "Swift"),
        ];

        foreach ((string heard, string expected) in measured)
        {
            string result = DictionaryReplacements.Apply(StarterDictionary.Developer, heard);
            Assert.True(
                result.Contains(expected, StringComparison.Ordinal),
                $"«{heard}» обязано дать «{expected}», получилось «{result}»");
        }
    }
}

/// <summary>Inflectability is a property of a record, not an inference from its letters.</summary>
public sealed class DictionaryInflectionFlagTests
{
    [Fact]
    public void TestReplacementMarkedLiteralTakesNoCaseEndings()
    {
        // "Комет" is not a word, but a recognition error. It has no cases, but
        // the inflected stem "комет-" ate "комету".
        DictionaryReplacement[] literal =
        [
            new(spoken: "комет", written: "commit", inflects: false),
        ];
        Assert.Equal(
            "смотрели на комету",
            DictionaryReplacements.Apply(literal, "смотрели на комету"));
        Assert.Equal(
            "сделай commit",
            DictionaryReplacements.Apply(literal, "сделай комет"));
    }

    [Fact]
    public void TestReplacementsInflectByDefault()
    {
        // Silently changing the behavior of all user dictionaries would be
        // worse than the original disaster.
        DictionaryReplacement[] usual =
        [
            new(spoken: "деплой", written: "deploy"),
        ];
        Assert.True(usual[0].Inflects);
        Assert.Equal(
            "после deploy",
            DictionaryReplacements.Apply(usual, "после деплоя"));
    }

    [Fact]
    public void TestDictionariesSavedBeforeTheFlagExistedStillInflect()
    {
        // People have dictionaries on their disks without this key. Its absence
        // means "inflects" - exactly how these dictionaries worked.
        byte[] saved = Encoding.UTF8.GetBytes(
            "[{\"id\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\",\"spoken\":\"релиз\",\"written\":\"release\"}]");

        List<DictionaryReplacement> decoded =
            JsonSerializer.Deserialize<List<DictionaryReplacement>>(saved)!;

        Assert.Single(decoded);
        Assert.True(decoded[0].Inflects);
        Assert.Equal(
            "перед release",
            DictionaryReplacements.Apply(decoded, "перед релизом"));
    }

    [Fact]
    public void TestFlagSurvivesSavingAndLoading()
    {
        DictionaryReplacement[] original =
        [
            new(spoken: "комет", written: "commit", inflects: false),
        ];
        List<DictionaryReplacement> restored = JsonSerializer.Deserialize<List<DictionaryReplacement>>(
            JsonSerializer.SerializeToUtf8Bytes(original))!;
        Assert.Equal(original, restored);
        Assert.False(restored[0].Inflects);
    }
}

/// <summary>What a dictionary cannot do - and should not try.</summary>
public sealed class DictionaryAmbiguityTests
{
    [Fact]
    public void TestOrdinaryRussianWordsAreNotSacrificedForTerms()
    {
        // The model writes "Sentry" as "центри", but "центре" is an ordinary Russian
        // word. Replacing it with Sentry means breaking normal speech for the sake of
        // one term.
        string result = DictionaryReplacements.Apply(
            StarterDictionary.Developer,
            "встретимся в центре города");

        Assert.Equal("встретимся в центре города", result);
    }

    [Fact]
    public void TestStarterSetShipsNoReplacementThatIsAnOrdinaryRussianWord()
    {
        // The workpiece is offered to everyone indiscriminately, so an entry that coincides
        // with an ordinary word breaks everyone's speech. Such a replacement a person
        // starts himself and knows what he is getting into.
        //
        // The list was obtained by brute force: for each entry all spellings with
        // the same phonetic key were built, and real Russian words were selected from them.
        string[] ordinary = ["питон", "редис", "докер", "бранч"];
        var shipped = StarterDictionary.Developer
            .Select(replacement => replacement.Spoken.ToLowerInvariant())
            .ToHashSet();

        foreach (string word in ordinary)
        {
            Assert.False(
                shipped.Contains(word),
                $"«{word}» — обычное русское слово");
        }
    }
}
