namespace DictationCore.Tests;

/// <summary>
/// Phonetic key: what it considers one word and what is different.
///
/// The tests are written from the rules of Russian phonetics, and not from a list of successful cases:
/// if a rule breaks, a line appears explaining which one it is.
/// </summary>
public sealed class PhoneticKeyTests
{
    private static void Same(string left, string right, string why) =>
        Assert.True(
            PhoneticKey.Of(left) == PhoneticKey.Of(right),
            $"«{left}» и «{right}» должны быть одним словом: {why}");

    private static void Different(string left, string right, string why) =>
        Assert.True(
            PhoneticKey.Of(left) != PhoneticKey.Of(right),
            $"«{left}» и «{right}» должны остаться разными: {why}");

    [Fact]
    public void TestUnstressedVowelsCollapse()
    {
        Same("диплой", "деплой", "безударные «и» и «е» звучат одинаково");
        Same("бэкэнд", "бэкенд", "«э» и «е» — одна гласная");
        Same("камит", "комит", "безударное «о» звучит как «а»");
        Same("мордж", "мёрдж", "«ё» — это «о» после мягкого");
    }

    [Fact]
    public void TestDoubledLettersCollapse()
    {
        Same("комит", "коммит", "удвоение на слух не различается");
        Same("рилис", "риллис", "то же самое");
    }

    [Fact]
    public void TestVoicingCollapsesOnlyWhereRussianNeutralisesIt()
    {
        Same("ребейс", "ребейз", "в конце слова звонкие оглушаются");
        Same("фронтент", "фронтенд", "то же самое");
        Same("хотфикс", "ходфикс", "перед глухим «ф» звонкая «д» оглушается");

        // For the sake of these three lines, the devoicing is made positional. Without them the set
        // ate 161 ordinary Russian words instead of 70 - with the same winnings.
        Different("теплой", "деплой", "в начале слова русский ничего не оглушает");
        Different("бетон", "питон", "то же самое");
        Different("протёкш", "продакш", "между гласными оглушения нет");
    }

    [Fact]
    public void TestFinalVowelIsNeverFolded()
    {
        // The main protection of the dictionary: the case ending lives in the last vowel.
        Different("центре", "центри", "иначе «в центре города» станет «в Sentry города»");
        Different("комете", "комети", "та же причина");
    }

    [Fact]
    public void TestSoftSignIsPartOfTheWord()
    {
        Different("камедь", "камет", "мягкий знак — не украшение");
        Different("ревю", "ревью", "«ревю» — своё слово");
    }
}

/// <summary>
/// Second pass of the dictionary on the real output of the model.
///
/// All left parts are from the Parakeet run: this is what the model actually wrote
/// when the voice pronounced an English term inside a Russian phrase.
/// None of these spellings are in the dictionary and cannot be - in another phrase
/// the same word will come out differently.
/// </summary>
public sealed class PhoneticMatchingTests
{
    private static string Polished(string text) =>
        DictionaryReplacements.Apply(StarterDictionary.Developer, text);

    [Fact]
    public void TestSpellingsTheDictionaryNeverSawAreStillRecognised()
    {
        (string Heard, string Expected)[] measured =
        [
            ("Запусти диплой в пятницу вечером.", "deploy"),
            ("Я поправил бэкэнд и залил изменения.", "backend"),
            ("Сделай комит в ветку.", "commit"),
            ("Потом ребейс и мордж.", "merge"),
            ("Потом ребейс и мордж.", "rebase"),
            ("Выкати роллбык без простоя.", "rollback"),
            ("Фронтент работает медленно.", "frontend"),
            ("Мы выкатили риллис вчера ночью.", "release"),
        ];

        foreach ((string heard, string expected) in measured)
        {
            Assert.True(
                Polished(heard).Contains(expected, StringComparison.Ordinal),
                $"«{heard}» обязано дать «{expected}», получилось «{Polished(heard)}»");
        }
    }

    [Fact]
    public void TestTermSplitInTwoWordsIsGluedBack()
    {
        // The model either glues the term together or breaks it apart - in the same recording.
        Assert.Equal("выкатывай без downtime", Polished("выкатывай без даун тайма"));
        Assert.Equal("добавил endpoint", Polished("добавил энд поинт"));
        Assert.Equal("напиши на JavaScript", Polished("напиши на джава скрипте"));
        Assert.Equal("мы пишем на TypeScript", Polished("мы пишем на тайп скрипте"));
    }

    [Fact]
    public void TestUnknownSpellingInAnObliqueCaseIsRecognised()
    {
        // The Russian inflects the term, and the model writes it as it hears it. Meet
        // both troubles at once, and most often this is the case.
        Assert.Equal("сразу после deploy", Polished("сразу после диплоя"));
        Assert.Equal("перед deploy", Polished("перед диплоем"));
        Assert.Equal("работа над backend", Polished("работа над бэкэндом"));
    }

    [Fact]
    public void TestWordsBrokenByPunctuationAreNotGluedTogether()
    {
        // "Энд, поинт" are two different places in the phrase, not a broken term.
        Assert.Equal("сказал энд, поинт не трогай", Polished("сказал энд, поинт не трогай"));
    }

    // MARK: - What phonetics doesn't do

    [Fact]
    public void TestShortReplacementsStayExact()
    {
        // Short words have too much ordinary speech per key.
        DictionaryReplacement[] replacements = [new(spoken: "апи", written: "API")];
        Assert.Equal("эпи и опа", DictionaryReplacements.Apply(replacements, "эпи и опа"));
    }

    [Fact]
    public void TestLatinReplacementsAreNotMatchedPhonetically()
    {
        // The rules are Russian: applying them to the Latin alphabet means guessing.
        DictionaryReplacement[] replacements = [new(spoken: "swift", written: "Swift")];
        Assert.Equal("sweft", DictionaryReplacements.Apply(replacements, "sweft"));
    }

    [Fact]
    public void TestDeletingReplacementsAreNeverMatchedPhonetically()
    {
        // The empty right side crosses out the filler word. Crossing out by a guess is
        // impossible: a person sees the replaced word in the text, but a disappeared one is
        // not visible. The price is fair: "короче" has to be crossed out as a separate entry.
        DictionaryReplacement[] replacements = [new(spoken: "короче", written: "")];

        Assert.Equal(
            "кароче я пошёл",
            DictionaryReplacements.Apply(replacements, "кароче я пошёл"));
        Assert.Equal(
            " я пошёл",
            DictionaryReplacements.Apply(replacements, "короче я пошёл"));
    }

    [Fact]
    public void TestOneKeyForTwoTermsIsNotAGuess()
    {
        // If two entries sound the same but are spelled differently, a selection is
        // not possible. Exact match still works.
        DictionaryReplacement[] replacements =
        [
            new(spoken: "рэндом", written: "random"),
            new(spoken: "рындам", written: "rundum"),
        ];
        Assert.Equal("рындом", DictionaryReplacements.Apply(replacements, "рындом"));
        Assert.Equal("random", DictionaryReplacements.Apply(replacements, "рэндом"));
        Assert.Equal("rundum", DictionaryReplacements.Apply(replacements, "рындам"));
    }

    [Fact]
    public void TestDigitsNextToLettersMakeADifferentWord()
    {
        DictionaryReplacement[] replacements = [new(spoken: "деплой", written: "deploy")];
        Assert.Equal("диплой2", DictionaryReplacements.Apply(replacements, "диплой2"));
    }
}

/// <summary>
/// The price of the comparison: ordinary Russian speech.
///
/// This is the main test of the dictionary. A product that impairs normal speech is worse
/// than a product that does not recognize the term: a person sees the missing term and
/// corrects it, the substituted word - no.
///
/// The list of words is not made up. It was obtained by brute force: for each dictionary entry
/// all spellings with the same phonetic key are built, and the system
/// spell checker selected real Russian words from them. Here is
/// the entire catch.
/// </summary>
public sealed class DictionaryFalsePositiveTests
{
    private static void AssertUntouched(string text) =>
        Assert.Equal(text, DictionaryReplacements.Apply(StarterDictionary.Developer, text));

    [Fact]
    public void TestOrdinaryRussianSpeechSurvivesTheStarterDictionary()
    {
        string[] speech =
        [
            // Previously, the set would turn this into "на commit в телескоп".
            "вчера мы смотрели на комету в телескоп",
            "комета Галлея вернётся не скоро",
            "он написал реферат о комете",
            "кометы видно только в ясную ночь",
            "наблюдение комет продолжалось всю ночь",
            "эмит событие после обработки запроса",
            // Previously - "Docker разгружали судно".
            "докеры разгружали судно в порту",
            "докер работал в ночную смену",
            // Previously - "Python сжал добычу".
            "питон сжал добычу и замер",
            "питоны спят почти всё время",
            // Previously - "на грядке растут Redis и укроп".
            "на грядке растут редис и укроп",
            "мы пошли на бранч в воскресенье",
            // It has always worked and must continue to work.
            "встретимся в центре города",
            "центральная улица была перекрыта",
            // It would appear if we neutralised the voicing anywhere.
            "в тёплой воде плавали утки",
            "бетон застыл только к утру",
            "бидон с молоком стоял в сенях",
            "мы рылись в старых бумагах весь вечер",
            "протёкшая крыша испортила потолок",
            "камедь на стволе вишни застыла каплями",
            "экзоты в этой оранжерее не приживаются",
            "вечернее ревю мы досмотрели до конца",
            // Just similar words.
            "на льдине сидел морж",
            "комитет собрался в среду",
            "доктор велел больше гулять",
            "линейку я потерял ещё в прошлом году",
            "продавщица посоветовала взять батон посвежее",
        ];

        foreach (string phrase in speech)
        {
            AssertUntouched(phrase);
        }
    }

    [Fact]
    public void TestConnectedSpeechIsNotTouchedAnywhere()
    {
        // A coherent piece, not a collection of individual words: a substitution in the middle
        // of a phrase is the worst visible.
        AssertUntouched(
            "Сегодня утром я вышел из дома пораньше и успел на первый автобус. "
                + "В центре города было пусто, только дворники мели тротуары. "
                + "Доктор советовал больше гулять, и я решил пройтись пешком до парка, "
                + "где мы договорились встретиться у старой водонапорной башни. "
                + "Вечером пошёл дождь, и все спрятались под навесом у магазина на углу.");
    }

    [Fact]
    public void TestVoicingIsNotFoldedWhereRussianKeepsIt()
    {
        // The rule is also checked on a user record: it can no longer be typed
        // in the set, and a person starts one.
        DictionaryReplacement[] replacements = [new(spoken: "питон", written: "Python")];
        foreach (string phrase in new[] { "бетон застыл к утру", "бидон с молоком", "бутон раскрылся" })
        {
            Assert.Equal(phrase, DictionaryReplacements.Apply(replacements, phrase));
        }

        Assert.Equal(
            "скрипты у нас на Python",
            DictionaryReplacements.Apply(replacements, "скрипты у нас на питоне"));
    }
}
