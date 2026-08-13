namespace DictationCore;

/// <summary>
/// Ready-made replacements for those who dictate in Russian with English terms.
///
/// The model recognizes Russian speech and writes down Anglicisms as it hears them:
/// "pull request" becomes "пул реквест", "Sentry" becomes "центри". It is not a glitch
/// of the model, but natural behavior: inside a Russian phrase it should write
/// Cyrillic.
///
/// The dictionary returns the terms to their normal form. Safe terms are built into
/// every recognition pipeline; personal replacements remain separate and take priority.
/// </summary>
public static class StarterDictionary
{
    /// <summary>Developer terms.</summary>
    /// <remarks>
    /// A computed property, exactly like the Swift original: every read builds fresh
    /// records, so no caller can share identifiers with another.
    /// </remarks>
    public static IReadOnlyList<DictionaryReplacement> Developer =>
    [
        .. Inflecting.Select(pair => new DictionaryReplacement(
            spoken: pair.Spoken,
            written: pair.Written,
            noAcousticBoost: Unboostable.Contains(pair.Written),
            allowsPhoneticMatching: !NonPhonetic.Contains(pair.Written))),
        .. Literal.Select(pair => new DictionaryReplacement(
            spoken: pair.Spoken,
            written: pair.Written,
            inflects: false,
            noAcousticBoost: Unboostable.Contains(pair.Written),
            allowsPhoneticMatching: !NonPhonetic.Contains(pair.Written))),
        // These are exact, measured decoder spellings. They repair text
        // safely after recognition but are intentionally kept out of the
        // acoustic hint: several are fragments of ordinary Russian words.
        .. PostprocessingOnly.Select(pair => new DictionaryReplacement(
            spoken: pair.Spoken,
            written: pair.Written,
            inflects: false,
            noAcousticBoost: true,
            allowsPhoneticMatching: false)),
    ];

    /// <summary>
    /// Terms whose Cyrillic sound is an ordinary Russian word.
    ///
    /// The flag lives <b>in the data of the workpiece</b>, and not in the filter on the hint side.
    /// The difference is not cosmetic: to remove the list from <c>VocabularyBoost</c> and not
    /// to put a flag here means returning <c>deploy</c>, <c>Sentry</c> and <c>commit</c>
    /// to the acoustics, that is, silently rolling back a measurement that cost a separate day.
    /// Details on why these three: <see cref="DictionaryReplacement.NoAcousticBoost"/>
    /// and docs/benchmarks.md.
    /// </summary>
    private static readonly HashSet<string> Unboostable = ["deploy", "Sentry", "commit"];

    /// <summary>
    /// These two phonetic keys collide with ordinary Russian words: "в центре"
    /// and "наблюдение комет". Measured decoder spellings live as exact rules below.
    /// </summary>
    private static readonly HashSet<string> NonPhonetic = ["Sentry", "commit"];

    /// <summary>
    /// The term in ordinary Russian writing - with all cases.
    ///
    /// These words in Russian speech are inflected: "до релиза", "после деплоя",
    /// "без даунтайма".
    ///
    /// What is intentionally missing here: substitutions that coincide with an ordinary Russian
    /// word. "Питон", "редис", "докер", "бранч" have been removed from the set. Not out of
    /// caution, but according to measurements: on recent recordings they did not work even once
    /// (0 out of 5 phrases for each term - the model writes "допер", "кэшвербис",
    /// "написано"), but they broke ordinary speech: "питон сжал добычу" turned into
    /// "Python сжал добычу", "докеры разгружали судно" - into "Docker разгружали
    /// судно". A person makes such a replacement for himself, consciously and in his own
    /// dictionary; it has no place in the workpiece.
    /// </summary>
    private static readonly (string Spoken, string Written)[] Inflecting =
    [
        // Working with code
        ("пул реквест", "pull request"),
        ("пиар", "PR"),
        ("мёрдж", "merge"),
        ("ребейз", "rebase"),
        ("ревью", "review"),
        ("код ревью", "code review"),

        // Launch and operation
        ("деплой", "deploy"),
        ("деплоймент", "deployment"),
        ("билд", "build"),
        ("релиз", "release"),
        ("роллбэк", "rollback"),
        ("даунтайм", "downtime"),
        ("продакшн", "production"),
        ("стейджинг", "staging"),

        // Tools
        ("сентри", "Sentry"),
        ("гитхаб", "GitHub"),
        ("кубернетес", "Kubernetes"),
        ("постгрес", "Postgres"),
        ("линтер", "linter"),
        ("экс код", "Xcode"),

        // Development
        ("фича флаг", "feature flag"),
        ("хотфикс", "hotfix"),
        ("эндпоинт", "endpoint"),
        ("апи", "API"),
        ("бэкенд", "backend"),
        ("фронтенд", "frontend"),
        ("тайпскрипт", "TypeScript"),
        ("джаваскрипт", "JavaScript"),
        ("свифт", "Swift"),
    ];

    /// <summary>
    /// Substitutions that are taken only in the form in which they are written.
    ///
    /// Two reasons to come here, and both are verified by measurements.
    ///
    /// <b>This is not a word, but a recognition error.</b> A run of fifteen phrases
    /// showed that the model hears "пул реквист", "гитхеб", "постгриз", "ход
    /// фикс"; the measurement is recorded in docs/benchmarks.md. A stump has no cases, and
    /// the inflected stem only invents coincidences for it. Checked: inflecting
    /// these entries did not add a single term to any entry in the corpus.
    ///
    /// <b>The stem is the same as an ordinary Russian word.</b> So for "коммит": up to
    /// unstressed vowels it is indistinguishable from a "комета", and the inflected
    /// stem "комет-" turned "смотрели на комету" into "смотрели на
    /// commit". The cost of the failure is measured and small - one phrase out of thirty
    /// ("в коммите" is no longer recognized), and fifteen ordinary Russian
    /// words were saved. The exact "коммит" and measured contextual decoder spellings are
    /// still repaired; broad phonetic matching is deliberately disabled.
    ///
    /// The second pass of the dictionary - the phonetic one - makes most of these entries
    /// unnecessary: "диплой" and "депла" are recognized from "деплой" themselves. What stayed here
    /// is what phonetics cannot reach.
    /// </summary>
    private static readonly (string Spoken, string Written)[] Literal =
    [
        ("пул реквист", "pull request"),
        ("пулреквест", "pull request"),
        ("мердж", "merge"),
        ("коммит", "commit"),
        ("браннич", "branch"),
        ("ребейзнуть", "rebase"),
        ("кот ревью", "code review"),
        ("роллбук", "rollback"),
        ("доунтайм", "downtime"),
        ("продакш", "production"),
        ("центри", "Sentry"),
        ("гитхеб", "GitHub"),
        ("кубернетис", "Kubernetes"),
        ("постгриз", "Postgres"),
        ("линти", "linter"),
        ("иксод", "Xcode"),
        ("x-код", "Xcode"),
        ("фичи-флаг", "feature flag"),
        ("ход фикс", "hotfix"),
        ("бэкинг", "backend"),
        ("фронтинг", "frontend"),
        ("тайп-скрипт", "TypeScript"),
    ];

    /// <summary>
    /// Exact decoder errors that are specific enough to repair without changing
    /// ordinary Russian speech. Unlike the acoustic aliases above, these strings
    /// are never used to bias recognition.
    /// </summary>
    private static readonly (string Spoken, string Written)[] PostprocessingOnly =
    [
        ("фичер флэк", "feature flag"),
        ("комит", "commit"),
        // The model hears "commit в branch" as "эмит в branch". A bare
        // "эмит" remains untouched because `emit` is a distinct developer term.
        ("эмит в", "commit в"),
        ("брандж", "branch"),
        ("поезд герз", "Postgres"),
        ("кэшвербис", "кэш в Redis"),
        ("кэшвердис", "кэш в Redis"),
        ("экскоут", "Xcode"),
        ("коутривью", "code review"),
        ("дакары в кьюберниц", "Docker и в Kubernetes"),
        ("дакаревки убернит", "Docker и Kubernetes"),
    ];

    /// <summary>
    /// Replacements that the user does not yet have.
    ///
    /// It is necessary so that the "add the set" proposal does not produce duplicates if
    /// the person has already created some of the terms himself.
    /// </summary>
    public static IReadOnlyList<DictionaryReplacement> Missing(
        IReadOnlyList<DictionaryReplacement> existing)
    {
        var known = existing.Select(replacement => replacement.Spoken.ToLowerInvariant()).ToHashSet();
        return Developer
            .Where(replacement => !known.Contains(replacement.Spoken.ToLowerInvariant()))
            .ToList();
    }
}
