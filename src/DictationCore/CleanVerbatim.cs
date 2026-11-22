using System.Text;

namespace DictationCore;

/// <summary>
/// Cleanup that only ever deletes.
///
/// Speech carries traces that typing never does: hesitations, a word said
/// twice while the next one loads, a sentence taken back out loud. This stage
/// removes exactly those and nothing else. It never rewords, reorders or adds a
/// word, which is the whole difference from cleanup done by a language model:
/// that kind can turn "don't" into "do", this kind cannot, because it has no
/// way to write a word that was not said.
///
/// <b>When in doubt, keep.</b> A false deletion is the worse failure, so every
/// rule leans towards keeping. Two adversarial review rounds tried to make the
/// stage delete something real - German separable verbs, the Portuguese "one",
/// millimetres, acronyms, grammatical doubles, sentences glued across "p.m." -
/// and each of those is a test.
///
/// Three passes, in this order:
///
/// 1. <b>Spoken corrections.</b> "Scratch that" takes back what was just said: as
///    its own sentence or opening one, the sentence before it; at the end of a
///    sentence, or between commas in the middle of one, everything before it in
///    that sentence. The commas are what make it a command rather than the
///    verb. English only. Every full stop ends a sentence except a title's
///    ("Dr. Smith", written so, before a name): when that guess is wrong a
///    correction takes back too little, never a sentence the person meant to
///    keep.
/// 2. <b>Fillers.</b> Words that are hesitation and nothing else. "um", "э" and
///    "mmm" are words too (a German particle, the Portuguese "one", a Russian
///    letter, a date format), so each goes only where it cannot be the word.
///    "um" never goes from a take with German or Portuguese in it. In a take
///    that reads as English it goes wherever it stands, except in "um hmm"
///    and right after a name; after a name, and in any other take, it goes
///    only when punctuation sets it apart on both sides. "э" and "эм", bare
///    or before a comma, go wherever they stand unless a letter is meant: in
///    a list of letters written with commas ("а, о, э, ы"), after a single
///    letter and a comma, next to a letter's name ("эс", "бэ"), after
///    "буква", or set against another letter ("пишется э, а не е"). "mmm" goes only when
///    punctuation sets it apart on both sides, and never in a date format.
///    An all-caps token is an acronym; "uh huh", "uh oh", "mm hmm" and
///    "um hmm" are words. The
///    comma after a hesitation goes with it; the one before stays, because the
///    recognizer wrote it, unless it can only be the pause: after a word that
///    cannot end a phrase ("should, um,"), in "Thanks, um, for", or after the
///    sentence's opening subject ("The build, uh,"). A restart around a
///    hesitation keeps one copy ("It's a, uh, a leak"), but a word said again
///    after a conjunction or preposition is two words ("shipped it and, um, it
///    works"). Repeated until nothing changes, so a hesitation that was hiding
///    another one goes in the same pass.
/// 3. <b>Stutters.</b> A short function word said twice in a row, from a list of
///    words whose doubles are never grammatical ("the the", "я я", "I, I").
/// 4. <b>"New paragraph"</b> ("Новый абзац"), set apart by punctuation, becomes a
///    paragraph break - in prose apps only. In a terminal a newline can run a
///    command, so verbatim apps keep the words. Last, so a hesitation after the
///    command is already gone and cannot take the break with it.
///
/// Protected spans and every name the person's own dictionary wrote are never
/// touched. A removal capitalizes what follows only when the removed piece
/// opened a sentence itself. Text with nothing to remove comes back exactly as it went in, and a
/// line break survives the removal of whatever followed it. A meeting
/// transcript turns spoken corrections off and keeps a turn that is nothing but
/// an interjection. The text is assumed to be NFC, as the recognizer writes it.
/// </summary>
/// <remarks>
/// Every index and count here is in grapheme clusters (Swift's <c>Character</c>), and every
/// "character" is a one-cluster string: tokens are cut on grapheme boundaries and the protected
/// spans they respect count the same unit. Where the original reads unicode scalars (an acronym's
/// capitals, the first letter a removal capitalizes), so does this, through <see cref="Rune"/>.
/// </remarks>
internal static class CleanVerbatim
{
    internal static string Clean(
        string text,
        bool spokenCorrections = true,
        IReadOnlySet<string>? protectedWords = null,
        PromptMode mode = PromptMode.Verbatim)
    {
        var tokens = new Tokens(text, protectedWords);
        if (tokens.Items.Count == 0) { return text; }

        bool corrected = spokenCorrections && tokens.RemoveCorrections();
        bool unfilled = false;
        bool unstuttered = false;
        // Fillers and stutters until neither changes: removing one can expose
        // the other ("The The um,"). Each pass removes at least one token, so
        // the token count bounds it.
        int bound = tokens.Items.Count;
        for (int pass = 0; pass <= bound; pass += 1)
        {
            bool filled = tokens.RemoveFillers();
            bool restarted = tokens.CollapseCommaRestarts();
            bool stuttered = tokens.CollapseStutters();
            unfilled = unfilled || filled;
            unstuttered = unstuttered || restarted || stuttered;
            if (!(filled || restarted || stuttered)) { break; }
        }

        // A take of nothing but hesitation is nothing: "Uh, um."
        if (spokenCorrections && tokens.Items.Count != 0 && tokens.IsOnlyHesitation)
        {
            tokens.Items.Clear();
            unfilled = true;
        }

        bool broken = mode == PromptMode.Prose && tokens.ApplyParagraphCommands();
        if (!(corrected || unfilled || unstuttered || broken)) { return text; }
        if (!spokenCorrections && tokens.Items.Count == 0) { return text; }
        return tokens.Rendered;
    }

    /// <summary>
    /// Every word a dictionary entry writes with a capital, lowercased: a name
    /// the person asked for ("The The", "Hmm Labs") is never a filler or a
    /// stutter. Lowercase connecting words ("commit в") are not names.
    /// A function word counts only when the name doubles it ("The The"), so
    /// "The Verge" does not stop "the the" from collapsing everywhere else.
    /// </summary>
    internal static IReadOnlySet<string> ProtectedWords(IReadOnlyList<DictionaryReplacement> replacements)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (DictionaryReplacement replacement in replacements)
        {
            string[] written = replacement.Written.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string[] words = written.Select(static word => word.ToLowerInvariant()).ToArray();
            for (int offset = 0; offset < written.Length; offset += 1)
            {
                if (!written[offset].EnumerateRunes().Any(Rune.IsUpper)) { continue; }
                string lower = words[offset];
                bool doubled = (offset > 0 && words[offset - 1] == lower)
                    || (offset + 1 < words.Length && words[offset + 1] == lower);
                if (StutterWords.Contains(lower) && !doubled) { continue; }
                result.Add(lower);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether every word of <paramref name="text"/> is a hesitation, counting the ones that
    /// are also words ("Um.", "Э."), which cleanup keeps when they stand alone.
    /// </summary>
    internal static bool IsOnlyHesitation(string text)
    {
        var tokens = new Tokens(text);
        if (tokens.Items.Count == 0) { return false; }
        for (int index = 0; index < tokens.Items.Count; index += 1)
        {
            if (!tokens.IsAnyFillerWord(index)) { return false; }
        }

        return true;
    }

    // MARK: - Vocabulary

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.Ordinal);

    /// <summary>Hesitation in every language Parakeet speaks: none of these is a word.</summary>
    internal static readonly HashSet<string> Fillers = Set(
        "uh", "uhh", "uhm", "umm", "erm", "hmm", "hm",
        "ээ", "эээ", "эмм",
        "äh", "ähm", "euh");

    /// <summary>
    /// Hesitations that are also words: "um" (a German particle, the
    /// Portuguese "one", the Slavic "mind"), "э" (a Russian letter), "mmm"
    /// (a date format). Each goes only where it cannot be the word, as the
    /// type's doc comment spells out.
    /// </summary>
    internal static readonly HashSet<string> GuardedFillers = Set("um", "er", "э", "эм", "mmm", "ммм");

    /// <summary>
    /// Hesitations that are words in German or Portuguese ("um", the German
    /// "er"): in an English take they go wherever they stand.
    /// </summary>
    internal static readonly HashSet<string> EnglishHesitations = Set("um", "er");

    /// <summary>
    /// Words that leave a phrase unfinished besides the pause words: "order
    /// more, um, more coffee", "Это такая, ээ, такая".
    /// </summary>
    internal static readonly HashSet<string> UnfinishedWords = Set(
        "more", "most", "some", "any", "many", "much", "every", "few", "less", "such", "этот", "эта", "эти", "такой",
        "такая", "такое", "такие", "самый", "самая", "um", "uma", "un", "una", "ein", "eine", "einen", "der",
        "die", "das", "le", "la", "les", "el", "los", "las");

    /// <summary>
    /// Russian hesitations that are also letter names ("э", "эм"): they go bare
    /// too, except next to a letter or after "буква".
    /// </summary>
    internal static readonly HashSet<string> LetterHesitations = Set("э", "эм");

    internal static readonly HashSet<string> LetterWords = Set("буква", "букву", "буквы", "букве", "буквой");

    /// <summary>Russian words of one letter, never a letter's name: "в", "и", "я".</summary>
    internal static readonly HashSet<string> OneLetterWords = Set("в", "к", "с", "у", "о", "и", "а", "я");

    /// <summary>
    /// Hesitation words that are half of a two-word interjection: "uh huh"
    /// (yes), "uh oh", "mm hmm", "um hmm".
    /// </summary>
    internal static readonly HashSet<string> InterjectionHeads = Set("uh", "um", "uhm", "mm", "mmm", "hm", "hmm");

    internal static readonly HashSet<string> InterjectionTails = Set("huh", "oh", "hmm", "hm");

    /// <summary>"Uh uh" and "nuh uh" mean no.</summary>
    internal static readonly HashSet<string> NoHeads = Set("uh", "nuh");

    /// <summary>Words that mark a take as German or Portuguese, where "um" is a word.</summary>
    internal static readonly HashSet<string> UmIsAWordMarkers = Set(
        "der", "das", "und", "ist", "nicht", "ich", "wir", "sich", "auch", "ein", "eine", "mit",
        "es", "geht", "dann", "dich", "oder", "aber", "nein", "bitte", "danke",
        "não", "você", "uma", "eu", "quero", "também", "são", "dois", "três", "quatro",
        "por", "favor", "obrigado", "obrigada", "muito", "isso", "está", "para");

    /// <summary>Letters that only German or Portuguese write, among the languages here.</summary>
    internal static readonly HashSet<string> UmIsAWordLetters = Set("ä", "ö", "ü", "ß", "ã", "õ");

    /// <summary>
    /// Words no other language the model speaks writes. One of them makes a
    /// take English, where "um" is never a word. ("need" is Estonian, "want"
    /// Dutch, "from" Swedish, so they are not here.)
    /// </summary>
    internal static readonly HashSet<string> EnglishMarkers = Set(
        "the", "you", "your", "that", "this", "with", "what", "would", "should", "could",
        "they", "about", "think", "know", "there", "their", "it's", "don't", "i'm", "let's");

    /// <summary>
    /// Words that cannot end a phrase, so a comma after one before a
    /// hesitation can only be the pause: "should, um,", "что, э,".
    /// </summary>
    internal static readonly HashSet<string> PauseCommaWords = Set(
        "a", "an", "the", "to", "of", "in", "on", "at", "for", "with", "from", "by", "into", "about", "and",
        "or", "but", "so", "because", "if", "when", "that", "than", "i", "we", "he", "she", "they", "my",
        "your", "our", "their", "its", "am", "is", "are", "was", "were", "be", "been", "do", "does", "did",
        "have", "has", "had", "will", "would", "shall", "should", "can", "could", "may", "might", "must",
        "not", "don't", "doesn't", "didn't", "can't", "won't", "wouldn't", "shouldn't", "couldn't", "isn't",
        "aren't", "wasn't", "weren't", "i'm", "i'll", "we're", "you're", "they're", "let's", "please", "just",
        "я", "мы", "ты", "вы", "он", "она", "оно", "они", "не", "ни", "бы", "в", "во", "на", "с", "со", "к",
        "ко", "у", "по", "за", "из", "от", "до", "о", "об", "при", "для", "и", "а", "но", "или", "что",
        "чтобы", "если", "когда", "потому", "как", "который", "которая", "которое", "которые", "можешь",
        "можете", "может", "могу", "буду", "будешь", "будет", "будем", "будете", "будут", "нужно", "нужны", "нужен", "нужна", "надо",
        "только", "должен", "хочу", "which", "who", "whom", "whose", "кроме",
        "через", "между", "без", "перед", "около", "вместо", "then");

    /// <summary>
    /// Conjunctions and prepositions, which open a phrase of their own: the
    /// word before one can close the phrase before it, so "shipped it and, um,
    /// it works" says "it" twice and means both.
    /// </summary>
    internal static readonly HashSet<string> JoiningWords = Set(
        "and", "or", "but", "so", "because", "if", "when", "that", "than", "then", "which", "who", "whom",
        "whose", "to", "of", "in", "on", "at", "for", "with", "from", "by", "into", "about", "и", "или", "а",
        "но", "что", "чтобы", "если", "когда", "потому", "как", "который", "которая", "которое", "которые",
        "в", "во", "на", "с", "со", "к", "ко", "у", "по", "за", "из", "от", "до", "о", "об", "при", "для",
        "кроме", "через", "между", "без", "перед", "около", "вместо");

    /// <summary>
    /// What can open a sentence's subject before its one word: "So Sentry,",
    /// "The build,".
    /// Words that open a phrase the comma after it belongs to: an introductory
    /// word anywhere ("Honestly, um,", "а во-вторых, э,").
    /// </summary>
    internal static readonly HashSet<string> IntroWords = Set(
        "actually", "honestly", "however", "unfortunately", "basically", "anyway", "also", "besides",
        "frankly", "кстати", "конечно", "наверное", "короче", "во-первых", "во-вторых", "в-третьих", "впрочем",
        "кажется", "честно", "например", "huh", "sorry");

    /// <summary>Introductory phrases of two or three words: "в общем", "of course".</summary>
    internal static readonly HashSet<string> IntroPhrases = Set(
        "в общем", "в итоге", "в целом", "в принципе", "по сути", "честно говоря", "на самом деле", "может быть",
        "к сожалению", "к счастью", "скорее всего", "кстати говоря", "of course", "for example", "by the way",
        "in fact", "to be honest");

    /// <summary>
    /// First-person verbs that introduce a thought, so the comma after one
    /// opening a sentence is grammar: "Думаю, э, это сработает".
    /// </summary>
    internal static readonly HashSet<string> ThinkingVerbs = Set(
        "думаю", "полагаю", "считаю", "понимаю", "повторяю", "признаю", "знаю", "слушаю");

    /// <summary>
    /// First words of a sentence-opening phrase whose comma is grammar: a
    /// greeting ("Hi team,") or a leading clause or phrase ("If the linter
    /// complains,", "In the meantime,").
    /// </summary>
    internal static readonly HashSet<string> Greetings = Set(
        "hi", "hey", "hello", "dear", "good", "привет", "здравствуйте", "доброе", "добрый");

    internal static readonly HashSet<string> LeadingPhraseWords = Set(
        "if", "when", "once", "after", "before", "while", "since", "until", "unless", "although", "because",
        "by", "in", "for", "on", "at", "during", "если", "когда", "после", "пока", "хотя");

    /// <summary>
    /// Words after a hesitation that open a new clause, so the comma before
    /// stays: "works, um, but", "функцию, э, которая".
    /// </summary>
    internal static readonly HashSet<string> ClauseWords = Set(
        "but", "so", "yet", "not", "which", "who", "а", "но", "чтобы", "потому", "который", "которая",
        "которое", "которые", "чем", "что", "не", "если", "когда", "хотя", "sonst", "aber", "weil", "dass", "denn", "i'll", "i'm",
        "i've", "i'd", "it's", "that's", "there's", "we'll", "we're", "let's", "you'll", "they're", "he's",
        "she's");

    /// <summary>"and"/"и" followed by a new subject opens a clause: "email, um, and I".</summary>
    internal static readonly HashSet<string> AndWords = Set("and", "or", "и", "или");

    internal static readonly HashSet<string> SubjectWords = Set(
        "i", "we", "you", "he", "she", "they", "it", "the", "this", "that", "there", "my", "our", "your",
        "their", "я", "мы", "ты", "вы", "он", "она", "оно", "они", "это", "этот", "эта", "там", "тут");

    /// <summary>
    /// Subject words that also open a noun phrase: a clause only with its
    /// verb ("and the docs are live", not "and the tire pressure").
    /// </summary>
    internal static readonly HashSet<string> NounPhraseOpeners = Set("the", "this", "that", "my", "our", "your", "their");

    internal static readonly HashSet<string> ClauseVerbs = Set(
        "is", "are", "was", "were", "has", "have", "had", "will", "would", "can", "could", "should",
        "isn't", "aren't", "wasn't", "weren't", "won't", "can't", "don't", "doesn't", "didn't");

    /// <summary>Words said twice for weight, even across a hesitation: "very, um, very".</summary>
    internal static readonly HashSet<string> WeightWords = Set(
        "very", "really", "yes", "yeah", "no", "okay", "ok", "bye", "да", "нет", "очень", "ага", "ладно");

    /// <summary>Object pronouns never start a list: "у нас, э, демо".</summary>
    internal static readonly HashSet<string> ObjectPronouns = Set(
        "me", "us", "him", "them", "нас", "вас", "нам", "вам", "мне", "тебе", "ему", "ей", "им");

    /// <summary>
    /// Verbs that follow a sentence's subject: "Sentry, uh, is down" is no
    /// vocative.
    /// </summary>
    internal static readonly HashSet<string> SubjectVerbs = Set(
        "is", "was", "has", "isn't", "wasn't", "hasn't", "keeps", "seems", "looks");

    /// <summary>
    /// A Russian past-tense verb ("сделал") or infinitive ("купить"): a verb,
    /// so the word before it is no vocative.
    /// </summary>
    internal static bool IsRussianPastTense(string word) =>
        GraphemeText.Count(word) >= 3 && GraphemeText.Split(word).All(IsRussianLowercase)
            && HasCharacterSuffix(word, "л", "ла", "ло", "ли");

    internal static bool IsRussianInfinitive(string word) =>
        GraphemeText.Count(word) >= 4 && GraphemeText.Split(word).All(IsRussianLowercase)
            && HasCharacterSuffix(word, "ть", "ти", "чь");

    /// <summary>
    /// The port of Swift's <c>("а"..."я").contains(character)</c>: a <c>Character</c> range
    /// compares by scalars, so an accented "а́" is inside it too.
    /// </summary>
    private static bool InCyrillicRange(string character) =>
        string.CompareOrdinal(character, "а") >= 0 && string.CompareOrdinal(character, "я") <= 0;

    private static bool IsRussianLowercase(string character) => InCyrillicRange(character) || character == "ё";

    /// <summary>The port of Swift's <c>hasSuffix</c>, which matches whole grapheme clusters.</summary>
    private static bool HasCharacterSuffix(string word, params string[] suffixes)
    {
        string[] characters = GraphemeText.Split(word);
        foreach (string suffix in suffixes)
        {
            string[] tail = GraphemeText.Split(suffix);
            if (tail.Length <= characters.Length && characters[^tail.Length..].SequenceEqual(tail)) { return true; }
        }

        return false;
    }

    internal static readonly HashSet<string> SubjectDeterminers = Set(
        "the", "a", "an", "my", "your", "his", "her", "our", "their", "its", "this", "that", "these", "those",
        "мой", "моя", "моё", "мои", "наш", "наша", "наше", "наши", "твой", "твоя", "ваш", "ваша",
        "этот", "эта", "эти");

    /// <summary>Opening words that take an object: "Thanks for", "Спасибо за".</summary>
    internal static readonly HashSet<string> ThankingWords = Set("thanks", "sorry", "спасибо", "извини");

    internal static readonly HashSet<string> ThankingObjects = Set("for", "about", "за");

    /// <summary>Hesitations that also end a sentence or a take: "…for you, um."</summary>
    internal static readonly HashSet<string> EndingHesitations = Set("um", "er", "э", "эм", "ммм");

    /// <summary>Date-format tokens: "dd, mmm, yyyy" is not a hesitation.</summary>
    internal static readonly HashSet<string> DateTokens = Set("d", "dd", "m", "mm", "y", "yy", "yyyy", "hh", "ss");

    /// <summary>Words after which "scratch that" is quoted, not said: "If I say, scratch that".</summary>
    internal static readonly HashSet<string> SpeechVerbs = Set(
        "say", "says", "said", "saying", "type", "types", "typed", "typing");

    /// <summary>
    /// Short function words whose doubles are never grammatical. Words that do
    /// double for a reason - "is is", "it it", "you you", "in in", "on on",
    /// "so so", "that that", "had had", "my my", "да да", "это это" - are left
    /// out. "i" counts only as the capital pronoun: Polish "i i" is "and i".
    /// </summary>
    internal static readonly HashSet<string> StutterWords = Set(
        "i", "the", "we", "he", "she", "they", "our", "your", "but", "of", "with", "я", "мы", "ты", "вы", "он",
        "она", "оно", "они", "и", "но", "в", "на", "с", "к", "у", "по", "за", "из", "от", "до");

    /// <summary>
    /// Doubles that are stutters only when the second copy runs on into the
    /// sentence ("это это известный", not "Это это?"), and the first three
    /// only in English ("Il y a a priori", Polish "to to" are grammar).
    /// </summary>
    internal static readonly HashSet<string> RunOnStutterWords = Set(
        "to", "a", "an", "this", "этот", "эта", "эти", "это");

    internal static readonly HashSet<string> EnglishStutterWords = Set("to", "a", "an");

    /// <summary>Russian letter names: "эс эм эс" is spelled, not hesitated.</summary>
    internal static readonly HashSet<string> LetterNames = Set(
        "эс", "эн", "эр", "эль", "эл", "эф", "ка", "тэ", "пэ", "бэ", "вэ", "гэ", "дэ", "зэ", "цэ", "ха", "ша");

    /// <summary>Doubles with a comma between them that are still stutters: "the, the".</summary>
    internal static readonly HashSet<string> CommaStutterWords = Set(
        "the", "our", "your", "every", "в", "на", "с", "к", "у", "по", "за", "из", "от", "до");

    /// <summary>Pronouns that stutter with a comma when they open a sentence: "I, I think".</summary>
    internal static readonly HashSet<string> OpeningCommaStutterWords = Set("i", "we", "я", "мы");

    /// <summary>Spoken corrections, as consecutive words.</summary>
    internal static readonly string[][] Corrections = [["scratch", "that"]];

    /// <summary>
    /// Words people lead a correction with: "Sorry, scratch that". Before the
    /// command they do not stop it from opening the sentence. "ah" is here
    /// because the model writes a spoken "uh" that way.
    /// "no" counts too, except right after a question, where it is the
    /// answer being corrected: "Is it ready? No, scratch that, yes."
    /// </summary>
    internal static readonly HashSet<string> CorrectionLeadIns = Set(
        "ah", "oh", "okay", "ok", "so", "well", "sorry", "no", "actually", "wait", "oops");

    /// <summary>Spoken paragraph breaks, as consecutive words.</summary>
    internal static readonly string[][] ParagraphCommands =
    [
        ["new", "paragraph"],
        ["новый", "абзац"],
    ];

    /// <summary>
    /// Titles whose full stop does not end a sentence, written exactly so:
    /// "MR." is a merge request and "ms." milliseconds.
    /// </summary>
    internal static readonly HashSet<string> Titles = Set("Dr", "Mr", "Mrs", "Ms", "Prof");

    /// <summary>Words before a title that make it a noun ending its sentence: "the Mrs."</summary>
    internal static readonly HashSet<string> Determiners = Set("the", "a", "an", "my", "your", "his", "her", "our", "their");

    /// <summary>Capitalized words after a title that are no name: "Dr. The house".</summary>
    internal static readonly HashSet<string> NotNames = Set(
        "the", "a", "an", "this", "that", "these", "those", "it", "he", "she", "we", "they", "i", "you",
        "his", "her", "its", "our", "their", "my", "your", "there", "here", "then");

    internal static readonly HashSet<string> TrailingPunctuation = Set(",", ".", ";", ":", "!", "?", "…");

    internal static readonly HashSet<string> SentenceEnding = Set(".", "!", "?", "…");

    internal static readonly HashSet<string> Closers = Set("\"", "'", "”", "’", "»", ")", "]");

    // MARK: - Tokens

    /// <summary>
    /// Whitespace as the Rust port sees it: every scalar of the character is
    /// whitespace, so a combining mark after a space starts a word.
    /// </summary>
    internal static bool IsSpace(string character) => character.EnumerateRunes().All(Rune.IsWhiteSpace);

    /// <summary>
    /// The port of Swift's <c>contains(where: \.isNewline)</c> over whitespace: the characters
    /// Swift calls newlines.
    /// </summary>
    internal static bool HasNewline(string text) =>
        text.IndexOfAny(['\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029']) >= 0;

    /// <summary>One word, or one protected span, with the whitespace in front of it.</summary>
    /// <remarks>
    /// A class where Swift has a struct: tokens live in one list and are mutated in place
    /// through it, which is what Swift's <c>items[index].text += …</c> does to its copy.
    /// </remarks>
    internal sealed class Token
    {
        private string text = "";
        private string[] characters = [];

        public Token(string separator, string text, bool isProtected)
        {
            Separator = separator;
            Text = text;
            IsProtected = isProtected;
        }

        public string Separator { get; set; }

        public string Text
        {
            get => text;
            set
            {
                text = value;
                characters = GraphemeText.Split(value);
            }
        }

        public bool IsProtected { get; }

        /// <summary>The word without its closing punctuation, lowercased.</summary>
        public string Word =>
            string.Concat(characters[..(characters.Length - TrailingCount(characters, TrailingPunctuation))])
                .ToLowerInvariant();

        /// <summary>The closing punctuation alone.</summary>
        public string Punctuation =>
            string.Concat(characters[(characters.Length - TrailingCount(characters, TrailingPunctuation))..]);

        public bool EndsWithComma => characters.Length > 0 && characters[^1] == ",";

        /// <summary>An acronym: two or more letters, all capitals (per scalar, as Rust).</summary>
        public bool IsShouting
        {
            get
            {
                Rune[] letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
                return letters.Length >= 2 && letters.All(Rune.IsUpper);
            }
        }

        /// <summary>A title with the comma a hesitation left after it: "Mr.,".</summary>
        public bool IsTitleBeforeComma =>
            characters.Length >= 2 && characters[^2] == "." && characters[^1] == ","
                && Titles.Contains(string.Concat(characters[..^2]));

        /// <summary>The text without closing quotes and brackets.</summary>
        private string[] Unquoted => characters[..(characters.Length - TrailingCount(characters, Closers))];

        /// <summary>
        /// A full stop, question mark, exclamation or ellipsis closes this
        /// token, possibly inside a closing quote or bracket.
        /// </summary>
        public bool EndsWithSentencePunctuation
        {
            get
            {
                string[] unquoted = Unquoted;
                return unquoted.Length > 0 && SentenceEnding.Contains(unquoted[^1]);
            }
        }

        /// <summary>A title as written, full stop and all: "Dr.", "Mrs."</summary>
        public bool IsTitle
        {
            get
            {
                string[] unquoted = Unquoted;
                if (unquoted.Length == 0 || unquoted[^1] != ".") { return false; }
                return Titles.Contains(string.Concat(unquoted[..^1]));
            }
        }

        /// <summary>Written with an ASCII digit: "10", "v2".</summary>
        public bool HasDigit => text.Any(static character => character is >= '0' and <= '9');

        public bool StartsUppercase => FirstRune is { } first && Rune.IsUpper(first);

        /// <summary>
        /// Starts with a lowercase letter: what a removal before it did not
        /// open as a sentence ("e.g. uh, empty").
        /// </summary>
        public bool StartsLowercase => FirstRune is { } first && Rune.IsLower(first);

        /// <summary>Closes with a question mark or an exclamation, possibly quoted.</summary>
        public bool EndsAsking
        {
            get
            {
                string[] unquoted = Unquoted;
                return unquoted.Length > 0 && unquoted[^1] is "?" or "!";
            }
        }

        /// <summary>Asks a question, possibly inside a closing quote: "ready?", "ready?!"</summary>
        public bool EndsWithQuestion
        {
            get
            {
                string[] unquoted = Unquoted;
                int count = TrailingCount(unquoted, TrailingPunctuation);
                return unquoted[(unquoted.Length - count)..].Contains("?");
            }
        }

        /// <summary>One letter, perhaps with accents: "а", "у́", "э".</summary>
        public bool IsSingleLetter
        {
            get
            {
                Rune[] scalars = Word.EnumerateRunes().ToArray();
                return scalars.Count(Rune.IsLetter) == 1
                    && scalars.All(static scalar => Rune.IsLetter(scalar) || scalar.Value is >= 0x300 and <= 0x36F);
            }
        }

        /// <summary>"I" and its contractions: "I'm", "I'll".</summary>
        public bool IsFirstPerson
        {
            get
            {
                string word = Word;
                return word == "i"
                    || word.StartsWith("i'", StringComparison.Ordinal)
                    || word.StartsWith("i’", StringComparison.Ordinal);
            }
        }

        /// <summary>Only punctuation, detached from the word it closes: "Uh , the".</summary>
        public bool IsDetachedPunctuation =>
            !IsProtected && characters.Length != 0 && characters.All(TrailingPunctuation.Contains);

        private Rune? FirstRune => Rune.TryGetRuneAt(text, 0, out Rune rune) ? rune : null;

        /// <summary>Remove the last character, as Swift's <c>removeLast()</c>.</summary>
        public void RemoveLast() => Text = string.Concat(characters[..^1]);

        /// <summary>How many characters at the end belong to <paramref name="set"/>.</summary>
        private static int TrailingCount(string[] characters, HashSet<string> set)
        {
            int count = 0;
            while (count < characters.Length && set.Contains(characters[characters.Length - 1 - count]))
            {
                count += 1;
            }

            return count;
        }
    }

    internal sealed class Tokens
    {
        private static readonly IReadOnlySet<string> NoWords = new HashSet<string>(StringComparer.Ordinal);

        private readonly IReadOnlySet<string> protectedWords;

        public Tokens(string text, IReadOnlySet<string>? protectedWords = null)
        {
            this.protectedWords = protectedWords ?? NoWords;
            string[] characters = GraphemeText.Split(text);
            IReadOnlyList<ProtectedSpan> spans = ProtectedSpanDetector.Detect(text);
            int spanIndex = 0;
            var separator = new StringBuilder();
            int index = 0;

            while (index < characters.Length)
            {
                while (spanIndex < spans.Count && spans[spanIndex].Range.End.Value <= index)
                {
                    spanIndex += 1;
                }

                if (spanIndex < spans.Count && spans[spanIndex].Range.Start.Value == index)
                {
                    Range range = spans[spanIndex].Range;
                    Items.Add(new Token(separator.ToString(), string.Concat(characters[range]), isProtected: true));
                    separator.Clear();
                    index = range.End.Value;
                    continue;
                }

                if (IsSpace(characters[index]))
                {
                    separator.Append(characters[index]);
                    index += 1;
                    continue;
                }

                var word = new StringBuilder();
                while (index < characters.Length && !IsSpace(characters[index]))
                {
                    if (spanIndex < spans.Count && spans[spanIndex].Range.Start.Value == index) { break; }
                    word.Append(characters[index]);
                    index += 1;
                }

                string before = separator.ToString();
                var token = new Token(before, word.ToString(), isProtected: false);
                // Punctuation the recognizer detached belongs to the word before it.
                if (token.IsDetachedPunctuation && before.Length != 0 && Items.Count != 0
                    && !Items[^1].IsProtected && !HasNewline(before))
                {
                    Items[^1].Text += token.Text;
                }
                else
                {
                    Items.Add(token);
                }

                separator.Clear();
            }

            Trailing = separator.ToString();
        }

        public List<Token> Items { get; } = [];

        public string Trailing { get; }

        public string Rendered =>
            string.Concat(Items.Select(static token => token.Separator + token.Text)) + Trailing;

        // MARK: Removal

        /// <summary>
        /// Remove <c>lower...upper</c>. The text's leading whitespace stays where it was,
        /// and a line break in front of the removed piece survives it.
        /// </summary>
        private void Remove(int lower, int upper)
        {
            string leading = Items[lower].Separator;
            Items.RemoveRange(lower, upper - lower + 1);
            if (lower >= Items.Count) { return; }
            string next = Items[lower].Separator;
            if (lower == 0 || (HasNewline(leading) && !HasNewline(next)))
            {
                Items[lower].Separator = leading;
            }
        }

        private bool IsKept(Token token) =>
            token.IsProtected || token.IsShouting || protectedWords.Contains(token.Word);

        /// <summary>The port of Swift's <c>items.indices.contains(index)</c>.</summary>
        private bool Has(int index) => index >= 0 && index < Items.Count;

        /// <summary>
        /// Whether the token at <paramref name="index"/> closes a sentence. A title's full stop
        /// is the one exception: written exactly so, before a capitalized name,
        /// and not after "the" ("I asked the Mrs. She said…" ends one). A
        /// word like "The" or "She" after it is no name ("5 Main Dr. The house").
        /// </summary>
        public bool EndsSentence(int index)
        {
            Token token = Items[index];
            if (!token.EndsWithSentencePunctuation) { return false; }
            if (!token.IsTitle || !Has(index + 1)) { return true; }
            Token next = Items[index + 1];
            if (!next.StartsUppercase || NotNames.Contains(next.Word)
                || IsAnyFillerWord(index + 1) || next.Word == "scratch")
            {
                return true;
            }

            // "the Mrs.", "the new Prof." are nouns; look back inside the sentence
            // only. "her to Dr." is no noun phrase.
            if (index > 0 && Determiners.Contains(Items[index - 1].Word)) { return true; }
            return index > 1 && Items[index - 1].Punctuation.Length == 0
                && !PauseCommaWords.Contains(Items[index - 1].Word)
                && Determiners.Contains(Items[index - 2].Word);
        }

        private bool OpensSentence(int index) => index == 0 || EndsSentence(index - 1);

        /// <summary>Whether "um" is a word in this take: German or Portuguese in it.</summary>
        private bool UmIsAWord =>
            Items.Any(static token =>
                !token.IsProtected && !token.IsShouting
                    && (UmIsAWordMarkers.Contains(token.Word)
                        || GraphemeText.Split(token.Text.ToLowerInvariant()).Any(UmIsAWordLetters.Contains)));

        /// <summary>Every token is a hesitation the filler pass would remove: "Uh, um."</summary>
        public bool IsOnlyHesitation
        {
            get
            {
                bool umIsAWord = UmIsAWord;
                bool readsAsEnglish = ReadsAsEnglish;
                for (int index = 0; index < Items.Count; index += 1)
                {
                    if (!IsFiller(index, umIsAWord, readsAsEnglish)) { return false; }
                }

                return true;
            }
        }

        /// <summary>Whether this take reads as English, where "um" is only a hesitation.</summary>
        private bool ReadsAsEnglish =>
            Items.Any(static token => !token.IsProtected && EnglishMarkers.Contains(token.Word));

        private string? WordAt(int index) => Has(index) ? Items[index].Word : null;

        private bool IsFiller(int index, bool umIsAWord, bool readsAsEnglish)
        {
            Token token = Items[index];
            if (IsKept(token)) { return false; }
            string word = token.Word;
            // "uh huh", "uh oh", "mm hmm" are words.
            if (InterjectionHeads.Contains(word) && token.Punctuation.Length == 0
                && WordAt(index + 1) is { } following && InterjectionTails.Contains(following))
            {
                return false;
            }

            if (InterjectionTails.Contains(word) && index > 0 && Items[index - 1].Punctuation.Length == 0
                && WordAt(index - 1) is { } preceding && InterjectionHeads.Contains(preceding))
            {
                return false;
            }

            // "Uh uh", "nuh uh": no.
            if (word == "uh" && index > 0 && Items[index - 1].Punctuation.Length == 0
                && WordAt(index - 1) is { } head && NoHeads.Contains(head))
            {
                return false;
            }

            if (NoHeads.Contains(word) && token.Punctuation.Length == 0 && WordAt(index + 1) == "uh")
            {
                return false;
            }

            if (Fillers.Contains(word)) { return true; }
            if (!GuardedFillers.Contains(word)) { return false; }
            if (EnglishHesitations.Contains(word) && umIsAWord) { return false; }
            // In an English take "um" and "er" are only hesitation.
            if (EnglishHesitations.Contains(word) && readsAsEnglish && !FollowsAName(index)) { return true; }
            // "э", "эм" are letters only in a letter list or after "буква".
            static bool ListedLetter(Token token) => token.IsSingleLetter && token.Punctuation == ",";
            static bool LetterName(Token token) => LetterNames.Contains(token.Word);
            bool letterContext = (index > 0
                    && (ListedLetter(Items[index - 1]) || LetterName(Items[index - 1])
                        || LetterWords.Contains(Items[index - 1].Word)))
                || (Has(index + 1) && LetterName(Items[index + 1]))
                || (token.Punctuation == "," && Has(index + 1)
                    && Items[index + 1].IsSingleLetter && Items[index + 1].Punctuation.Length != 0)
                // Set against another letter: "пишется э, а не е", "через э, а не через е".
                || (token.Punctuation == "," && WordAt(index + 1) == "а" && WordAt(index + 2) == "не"
                    && Items.Skip(index + 3).Take(2).Any(static other =>
                        other.IsSingleLetter && !OneLetterWords.Contains(other.Word)));
            if (LetterHesitations.Contains(word)
                && (token.Punctuation.Length == 0 || token.Punctuation == ",") && !letterContext)
            {
                return true;
            }

            // A letter among different letters: "а, о, у, э, ы". The same word
            // on both sides is a restart: "Я, э, я думаю".
            if (LetterHesitations.Contains(word) && index > 0 && Has(index + 1)
                && Items[index - 1].IsSingleLetter && Items[index + 1].IsSingleLetter
                && Items[index - 1].Word != Items[index + 1].Word)
            {
                return false;
            }

            // A date format: "dd, mmm, yyyy".
            if (word == "mmm"
                && (DateTokens.Contains(WordAt(index - 1) ?? "") || DateTokens.Contains(WordAt(index + 1) ?? "")))
            {
                return false;
            }

            // Hesitations in a row are one pause: "um uh, now", "um um, we".
            int runEnd = index + 1;
            while (runEnd < Items.Count && Items[runEnd].Punctuation.Length == 0 && IsAnyFillerWord(runEnd))
            {
                runEnd += 1;
            }

            bool carriesComma = token.EndsWithComma
                || (runEnd > index + 1 && runEnd < Items.Count && IsAnyFillerWord(runEnd) && Items[runEnd].EndsWithComma)
                || (token.Punctuation.Length == 0 && Has(index + 1)
                    && IsAnyFillerWord(index + 1) && Items[index + 1].EndsWithComma);
            bool apartBefore = OpensSentence(index) || Items[index - 1].EndsWithComma
                || (word == "um" && readsAsEnglish && !FollowsAName(index));
            if (carriesComma && apartBefore) { return true; }
            // A sentence of its own in English: "That's the plan. Um. Let's see."
            if (word == "um" && readsAsEnglish && OpensSentence(index) && EndsSentence(index)) { return true; }
            if (!EndingHesitations.Contains(word) || (LetterHesitations.Contains(word) && letterContext))
            {
                return false;
            }

            // One a sentence end sets apart after a comma: "…for you, um."
            string punctuation = token.Punctuation;
            if (punctuation.Length != 0 && !punctuation.Contains(',') && index > 0 && Items[index - 1].EndsWithComma)
            {
                return true;
            }

            // One the end of the text sets apart, where a trailing command took
            // its comma: "…the end um" from "…the end um, new line."
            return punctuation.Length == 0 && index == Items.Count - 1 && apartBefore;
        }

        /// <summary>
        /// A capitalized word just before, in the middle of a sentence: a German
        /// noun with its separable verb ("Stell die Config um"), or a name.
        /// </summary>
        private bool FollowsAName(int index) =>
            index > 0 && !OpensSentence(index) && Items[index - 1].StartsUppercase
                && !Items[index - 1].IsFirstPerson && !OpensSentence(index - 1);

        /// <summary>
        /// Whether the comma before the hesitation at <paramref name="index"/> is grammar, so it
        /// stays. A hesitation's commas are its pause and go with it, unless the
        /// one before closes something a careful writer would close with a
        /// comma: an opening word or clause, an introductory word, a clause that
        /// follows, a name, a list.
        /// </summary>
        private bool CommaBeforeIsGrammar(int index)
        {
            Token previous = Items[index - 1];
            string previousWord = previous.Word;
            // Russian sets every introductory word off, the comma before it
            // included: "Мы, эм, честно говоря, не ожидали".
            if (RussianIntro(index + 1) is { } end && Items[end].EndsWithComma) { return true; }
            if (PauseCommaWords.Contains(previousWord)) { return false; }
            if (!Has(index + 1)) { return false; }
            Token next = Items[index + 1];
            string nextWord = next.Word;
            // "Thanks, um, for" is one phrase; a title keeps its name ("Mr. Henderson").
            if ((ThankingWords.Contains(previousWord) && ThankingObjects.Contains(nextWord))
                || previous.IsTitleBeforeComma)
            {
                return false;
            }

            // English writes a comma after "e.g." and "i.e."; after any other
            // abbreviation's full stop the comma is the pause: "т. е., э, не".
            if (previousWord is "e.g" or "i.e") { return true; }
            if (previous.Text.EndsWith(".,", StringComparison.Ordinal)) { return false; }
            // Russian "и"/"или" joining a last item takes no comma: "фронтендер и тестировщик".
            if (nextWord is "и" or "или" && !SubjectWords.Contains(WordAt(index + 2) ?? ""))
            {
                return false;
            }

            // The same word on both sides keeps its comma rather than glue "it
            // it". An appositive restates the noun: "the old flag, er, the one".
            if (nextWord == previousWord || IntroWords.Contains(previousWord)
                || IntroPhraseEnds(index - 1)
                || ClauseWords.Contains(nextWord)
                || (nextWord == "the" && (WordAt(index + 2) ?? "") is "one" or "ones"))
            {
                return true;
            }

            // "не знаю, ээ, может быть завтра".
            if (RussianIntro(index + 1) is not null || ClosesARelativeClause(index)) { return true; }
            // "and I", "и они", "and Sam is": a new clause. The last item of a
            // list of three keeps its comma too: "the flights, uh, and the car".
            if (AndWords.Contains(nextWord) && (AndOpensAClause(index + 1) || AfterAListComma(index)))
            {
                return true;
            }

            // A name that closes the phrase: "Thanks again, um, Jordan,", or
            // opens a clause: "the vendor call, uh, Hannah is out". German
            // capitalizes every noun, so not there ("Es geht um, äh, Geld.").
            if (next.StartsUppercase && !next.IsFirstPerson
                && (next.Punctuation.Length != 0 || VerbFollows(index + 2)) && !UmIsAWord)
            {
                return true;
            }

            // A list: "eggs, milk, uh, butter", "10, um, 30", "chips, um, soda,".
            // An opening word before is no list item ("Okay, this, uh,"), unless
            // a greeting names someone ("Привет, ребята, э,").
            if (index >= 2 && Items[index - 2].EndsWithComma
                && (!OpensSentence(index - 2) || Greetings.Contains(Items[index - 2].Word)))
            {
                return true;
            }

            // A name set off by commas: "Thanks, John, um, see you". Not a
            // subject before its verb: "Ну, Sentry, э, упал".
            if (index >= 2 && previous.StartsUppercase && !previous.IsFirstPerson && !OpensSentence(index - 1)
                && Items[index - 2].EndsWithComma && !VerbFollows(index + 1) && !UmIsAWord)
            {
                return true;
            }

            // Items that repeat their first word: "от Лены, от Тимура, э, от Вадима".
            if (index >= 4 && Items[index - 2].Punctuation.Length == 0 && Items[index - 2].Word == nextWord
                && Items[index - 3].EndsWithComma && Items[index - 4].Word == nextWord)
            {
                return true;
            }

            if (previous.HasDigit && next.HasDigit) { return true; }
            // A subject before its verb is no list item: "Марина, ээ, сказала".
            // A greeting keeps its comma: "Привет, ээ, хотел".
            if (OpensSentence(index - 1) && !Greetings.Contains(previousWord) && VerbFollows(index + 1))
            {
                return false;
            }

            if (ContinuesAList(index)) { return true; }
            return OpeningComma(index);
        }

        /// <summary>A clause starts right after the hesitation: "but", "I'll", "and I".</summary>
        private bool OpensAClause(int index)
        {
            string next = WordAt(index + 1) ?? "";
            return ClauseWords.Contains(next) || (AndWords.Contains(next) && AndOpensAClause(index + 1));
        }

        /// <summary>
        /// The "and" at <paramref name="index"/> opens a clause: a new subject ("and I", "и
        /// они", "and Sam is"), or a noun phrase with its verb ("and the docs
        /// are live", not "and the tire pressure").
        /// </summary>
        private bool AndOpensAClause(int index)
        {
            if (!Has(index + 1)) { return false; }
            Token subject = Items[index + 1];
            if (!SubjectWords.Contains(subject.Word))
            {
                return subject.StartsUppercase && !subject.IsFirstPerson && VerbFollows(index + 2);
            }

            if (!NounPhraseOpeners.Contains(subject.Word)) { return true; }
            int at = index + 1;
            while (at < Items.Count && at <= index + 4 && Items[at].Punctuation.Length == 0)
            {
                at += 1;
                if (ClauseVerbs.Contains(WordAt(at) ?? "")) { return true; }
            }

            return false;
        }

        /// <summary>
        /// An earlier comma in this sentence sets off a list item, so the
        /// comma before the hesitation at <paramref name="index"/> is the list's.
        /// </summary>
        private bool AfterAListComma(int index)
        {
            int at = index - 1;
            while (at > 0 && !EndsSentence(at - 1))
            {
                at -= 1;
                if (Items[at].EndsWithComma) { return !OpensSentence(at) && index - 1 - at <= 3; }
            }

            return false;
        }

        /// <summary>
        /// An introductory phrase of two or three words ends at <paramref name="end"/>: "в
        /// общем", "of course".
        /// </summary>
        private bool IntroPhraseEnds(int end)
        {
            for (int length = 2; length <= 3; length += 1)
            {
                if (end + 1 < length) { continue; }
                List<Token> words = Items.GetRange(end + 1 - length, length);
                if (!words.Take(length - 1).Any(static word => word.Punctuation.Length != 0)
                    && IntroPhrases.Contains(string.Join(' ', words.Select(static word => word.Word))))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Where a Russian introductory word or phrase starting at <paramref name="start"/>
        /// ends: "кстати", "честно говоря".
        /// </summary>
        private int? RussianIntro(int start)
        {
            if (WordAt(start) is not { } first || !GraphemeText.Split(first).Any(InCyrillicRange)) { return null; }
            for (int length = 3; length >= 2; length -= 1)
            {
                if (start + length > Items.Count) { continue; }
                List<Token> words = Items.GetRange(start, length);
                if (!words.Take(length - 1).Any(static word => word.Punctuation.Length != 0)
                    && IntroPhrases.Contains(string.Join(' ', words.Select(static word => word.Word))))
                {
                    return start + length - 1;
                }
            }

            return IntroWords.Contains(first) ? start : null;
        }

        /// <summary>
        /// The comma before the hesitation at <paramref name="index"/> closes a relative clause
        /// the sentence opened with a comma: "Переводчик, которого нам
        /// посоветовали в Ташкенте, ээ, берёт".
        /// </summary>
        private bool ClosesARelativeClause(int index)
        {
            int at = index - 1;
            while (at > 0 && !EndsSentence(at - 1))
            {
                at -= 1;
                if (Items[at].EndsWithComma)
                {
                    string opener = Items[at + 1].Word;
                    return index - 1 - at >= 3
                        && (opener.StartsWith("котор", StringComparison.Ordinal) || opener is "which" or "who");
                }
            }

            return false;
        }

        /// <summary>
        /// The word at <paramref name="index"/> is a verb that follows its subject: "is",
        /// "сделал".
        /// </summary>
        private bool VerbFollows(int index) =>
            WordAt(index) is { } word && (SubjectVerbs.Contains(word) || IsRussianPastTense(word));

        /// <summary>
        /// One or two words after the hesitation, three after "a" or "the",
        /// then a comma or "and": the next item of a list, written the same
        /// way as the one before it (both bare, or both after "the").
        /// </summary>
        private bool ContinuesAList(int index)
        {
            static bool Determined(Token token) => SubjectDeterminers.Contains(token.Word);
            bool continues = false;
            for (int length = 1; length <= 3 && !continues; length += 1)
            {
                int last = index + length;
                if (last >= Items.Count || !(length < 3 || Determined(Items[index + 1]))
                    || Items.GetRange(index + 1, last - index - 1).Any(static token => token.Punctuation.Length != 0))
                {
                    continue;
                }

                continues = Items[last].EndsWithComma || AndWords.Contains(WordAt(last + 1) ?? "");
            }

            if (!continues || ObjectPronouns.Contains(Items[index - 1].Word)
                || IsRussianInfinitive(Items[index - 1].Word))
            {
                return false;
            }

            bool previousDetermined = index >= 2 && Items[index - 2].Punctuation.Length == 0 && Determined(Items[index - 2]);
            return previousDetermined == Determined(Items[index + 1]);
        }

        /// <summary>
        /// The comma closes the opening of the sentence: a single word
        /// ("Okay,", "Sarah,", "Кстати,"), a greeting ("Hi team,"), or a leading
        /// clause or phrase ("If the linter complains,", "In the meantime,").
        /// </summary>
        private bool OpeningComma(int index)
        {
            int end = index - 1;
            int start = end;
            while (start > 0 && !EndsSentence(start - 1)) { start -= 1; }
            bool Bare(int from) =>
                !Items.GetRange(from, end - from).Any(static token => token.Punctuation.Length != 0);
            // A leading phrase after an opening word: "Claude, before you
            // change anything, uh,".
            if (start < end && Items[start].EndsWithComma && Bare(start + 1))
            {
                return start + 1 < end && LeadingPhraseWords.Contains(Items[start + 1].Word);
            }

            if (!Bare(start)) { return false; }
            string first = Items[start].Word;
            if (start == end)
            {
                // A subject before its verb is no vocative: "Sentry, uh, is
                // down", "Дима, э, сделал". A greeting always is: "Привет,
                // хотел". A first-person verb takes its object: "Высылаю, э,
                // акт", unless it introduces a thought: "Думаю, э, это".
                bool acts = HasCharacterSuffix(first, "ю") && GraphemeText.Count(first) >= 4
                    && GraphemeText.Split(first).All(IsRussianLowercase)
                    && !ThinkingVerbs.Contains(first);
                return Greetings.Contains(first)
                    || (!IsRussianInfinitive(first) && !acts && !VerbFollows(index + 1));
            }

            return (Greetings.Contains(first) && end - start < 4) || LeadingPhraseWords.Contains(first);
        }

        /// <summary>
        /// How many words before the hesitation at <paramref name="index"/> are said again
        /// right after it - a restart: "It's a, uh, a leak", "Can you, um, can you".
        /// </summary>
        private int? RestartLength(int index)
        {
            if (index <= 0 || EndsSentence(index)) { return null; }
            return RepeatedLength(index - 1, index + 1, acrossHesitation: true) ?? RevisedLength(index);
        }

        /// <summary>
        /// How many words before the hesitation at <paramref name="index"/> start a phrase that
        /// stops on a word that cannot end it and is begun again with another
        /// ending: "with the, um, with Kowalski's", "It's a, uh, it's an". One
        /// that stops on a conjunction or preposition is begun again only from
        /// a sentence's start or a comma ("I think that, um, I think we"), not
        /// "shipped it and, um, it works".
        /// </summary>
        private int? RevisedLength(int index)
        {
            for (int length = 3; length >= 2; length -= 1)
            {
                if (index < length || index + length >= Items.Count) { continue; }
                List<Token> before = Items.GetRange(index - length, length);
                List<Token> again = Items.GetRange(index + 1, length - 1);
                Token last = Items[index - 1];
                string lastWord = last.Word;
                if (!(PauseCommaWords.Contains(lastWord) || UnfinishedWords.Contains(lastWord))
                    || !(!JoiningWords.Contains(lastWord) || OpensSentence(index - length)
                        || Items[index - length - 1].EndsWithComma)
                    || last.StartsUppercase
                    || !(last.Punctuation.Length == 0 || last.Punctuation == ",")
                    || before.Take(before.Count - 1).Any(static token => token.Punctuation.Length != 0)
                    || before.Any(IsKept) || again.Any(IsKept)
                    || !again.Zip(before).All(static pair =>
                        pair.First.Punctuation.Length == 0 && pair.First.Word == pair.Second.Word))
                {
                    continue;
                }

                return length;
            }

            return null;
        }

        /// <summary>
        /// How many words ending at <paramref name="beforeEnd"/> are said again starting at
        /// <paramref name="afterStart"/>: a restart when the first copy opens the sentence or
        /// stops on a word that cannot end a phrase ("It's a, a leak", "Can
        /// you, can you"), never a clause boundary ("Try it, it works"). Across
        /// a hesitation any other phrase is a restart too when the second copy
        /// runs on, unless it is said for weight or could open the next clause
        /// ("the ERP, uh, the ERP migration", "отправил, ммм, отправил договор").
        /// </summary>
        private int? RepeatedLength(int beforeEnd, int afterStart, bool acrossHesitation)
        {
            for (int length = 3; length >= 1; length -= 1)
            {
                if (beforeEnd + 1 < length || afterStart + length > Items.Count) { continue; }
                int beforeStart = beforeEnd + 1 - length;
                List<Token> before = Items.GetRange(beforeStart, length);
                List<Token> after = Items.GetRange(afterStart, length);
                Token last = Items[beforeEnd];
                string lastWord = last.Word;
                // Capitalized inside a sentence it is a name: "Ask Uma, uh, Uma knows".
                bool named = last.StartsUppercase && !last.IsFirstPerson && !OpensSentence(beforeEnd);
                bool unfinished = !named
                    && (PauseCommaWords.Contains(lastWord) || UnfinishedWords.Contains(lastWord));
                string firstWord = Items[beforeStart].Word;
                // A verb right after the second copy makes it a new subject:
                // "milk, uh, milk is", "бюджет, э, бюджет утвердили".
                bool VerbAt(int at) =>
                    WordAt(at) is { } word
                        && (ClauseVerbs.Contains(word) || SubjectVerbs.Contains(word) || IsRussianPastTense(word));
                int next = afterStart + length;
                bool subject = VerbAt(next) || (Has(next) && Items[next].Punctuation.Length == 0 && VerbAt(next + 1));
                bool restarts = acrossHesitation
                    && after[^1].Punctuation.Length == 0
                    && next < Items.Count
                    && !subject
                    && !Items[beforeStart].StartsUppercase
                    && !SubjectWords.Contains(firstWord)
                    && !ObjectPronouns.Contains(firstWord)
                    && !WeightWords.Contains(lastWord)
                    && (length > 1 || !before.Any(static token => token.HasDigit));
                if (!(OpensSentence(beforeStart) || unfinished || restarts)
                    || before.Any(IsKept) || after.Any(IsKept)
                    || before.Take(length - 1).Any(static token => token.Punctuation.Length != 0)
                    || !(before[^1].Punctuation.Length == 0 || before[^1].Punctuation == ",")
                    || after.Take(length - 1).Any(static token => token.Punctuation.Length != 0)
                    // "a, um, an": the article said again for the next sound.
                    || !before.Zip(after).All(static pair =>
                        pair.First.Word == pair.Second.Word
                            || (pair.First.Word is "a" or "an" && pair.Second.Word is "a" or "an")))
                {
                    continue;
                }

                return length;
            }

            return null;
        }

        /// <summary>
        /// A hesitation, as far as a correction is concerned: any filler word,
        /// guarded or not, because it can only stand between words here.
        /// </summary>
        public bool IsAnyFillerWord(int index)
        {
            Token token = Items[index];
            if (IsKept(token)) { return false; }
            return Fillers.Contains(token.Word) || GuardedFillers.Contains(token.Word);
        }

        // MARK: Pass 1: corrections

        /// <summary>Each sentence as <c>(lower, upper)</c>, both ends included.</summary>
        private List<(int Lower, int Upper)> Sentences
        {
            get
            {
                var result = new List<(int Lower, int Upper)>();
                int start = 0;
                for (int index = 0; index < Items.Count; index += 1)
                {
                    if (!(EndsSentence(index) || index == Items.Count - 1)) { continue; }
                    result.Add((start, index));
                    start = index + 1;
                }

                return result;
            }
        }

        public bool RemoveCorrections()
        {
            bool changed = false;
            // Each removal shortens the text, so the bound is the token count.
            int bound = Items.Count;
            for (int pass = 0; pass < bound; pass += 1)
            {
                if (NextCorrection() is not { } range) { break; }
                bool opener = !Items[range.Lower].StartsLowercase;
                Remove(range.Lower, range.Upper);
                changed = true;
                // Every removal starts at a sentence start; what is left there
                // opens the sentence when the removed piece did ("vs. coffee,
                // scratch that, water" did not).
                if (opener && range.Lower < Items.Count && !Items[range.Lower].IsProtected)
                {
                    CapitalizeFirstLetter(range.Lower);
                }
            }

            return changed;
        }

        /// <summary>The tokens the first spoken correction removes, itself included.</summary>
        private (int Lower, int Upper)? NextCorrection()
        {
            List<(int Lower, int Upper)> sentences = Sentences;
            for (int position = 0; position < sentences.Count; position += 1)
            {
                (int Lower, int Upper) sentence = sentences[position];
                var words = new List<int>();
                for (int index = sentence.Lower; index <= sentence.Upper; index += 1)
                {
                    if (!IsAnyFillerWord(index)) { words.Add(index); }
                }

                List<string> spoken = words
                    .Select(index => Items[index].IsProtected ? Items[index].Text : Items[index].Word)
                    .ToList();

                foreach (string[] command in Corrections)
                {
                    if (spoken.Count < command.Length) { continue; }
                    for (int offset = 0; offset <= spoken.Count - command.Length; offset += 1)
                    {
                        if (!spoken.GetRange(offset, command.Length).SequenceEqual(command)) { continue; }
                        int first = words[offset];
                        int last = words[offset + command.Length - 1];
                        // A question is not a command: "Scratch that? Or fix it?"
                        if (Items[last].EndsWithQuestion) { continue; }
                        // Quoted after a speech verb: "If I say, scratch that, …"
                        int said = first - 1;
                        while (said >= sentence.Lower && IsAnyFillerWord(said)) { said -= 1; }
                        if (said >= sentence.Lower && SpeechVerbs.Contains(Items[said].Word)) { continue; }
                        bool afterQuestion = PreviousSentence(position, sentences) is { } previous
                            && Items[sentences[previous].Upper].EndsWithQuestion;
                        bool opens = spoken.Take(offset).All(word =>
                            CorrectionLeadIns.Contains(word) && !(word == "no" && afterQuestion));
                        bool closes = offset + command.Length == spoken.Count;
                        bool pauseBefore = first > sentence.Lower && CommaBefore(first, sentence.Lower);
                        bool pauseAfter = CommaAfter(last, sentence.Upper);

                        if (opens && (closes || pauseAfter))
                        {
                            return (PreviousStart(position, sentences), closes ? sentence.Upper : last);
                        }

                        if (closes && pauseBefore) { return sentence; }
                        if (!opens && !closes && pauseBefore && pauseAfter) { return (sentence.Lower, last); }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Where the sentence a correction takes back begins. Sentences of
        /// nothing but hesitation are passed over and go with it.
        /// </summary>
        private int PreviousStart(int position, List<(int Lower, int Upper)> sentences) =>
            sentences[PreviousSentence(position, sentences) ?? 0].Lower;

        /// <summary>The sentence before <paramref name="position"/> that is more than hesitation.</summary>
        private int? PreviousSentence(int position, List<(int Lower, int Upper)> sentences)
        {
            int candidate = position - 1;
            while (candidate >= 0 && AllFillerWords(sentences[candidate])) { candidate -= 1; }
            return candidate >= 0 ? candidate : null;
        }

        private bool AllFillerWords((int Lower, int Upper) sentence)
        {
            for (int index = sentence.Lower; index <= sentence.Upper; index += 1)
            {
                if (!IsAnyFillerWord(index)) { return false; }
            }

            return true;
        }

        /// <summary>
        /// A comma closes the word before <paramref name="index"/>, perhaps behind bare
        /// hesitations: "Friday, uh scratch that".
        /// </summary>
        private bool CommaBefore(int index, int start)
        {
            int before = index - 1;
            while (before > start && !Items[before].EndsWithComma && IsAnyFillerWord(before)) { before -= 1; }
            return Items[before].EndsWithComma;
        }

        /// <summary>
        /// A comma closes the word at <paramref name="index"/>, or a bare hesitation right
        /// after it: "scratch that uh, on Monday".
        /// </summary>
        private bool CommaAfter(int index, int end)
        {
            int after = index;
            while (!Items[after].EndsWithComma && after < end && IsAnyFillerWord(after + 1)) { after += 1; }
            return Items[after].EndsWithComma;
        }

        // MARK: Pass 2: fillers

        public bool RemoveFillers()
        {
            bool umIsAWord = UmIsAWord;
            bool readsAsEnglish = ReadsAsEnglish;
            bool changed = false;
            int index = 0;
            while (index < Items.Count)
            {
                if (!IsFiller(index, umIsAWord, readsAsEnglish))
                {
                    index += 1;
                    continue;
                }

                if (RestartLength(index) is { } length)
                {
                    // Keep the second copy; it opens the sentence if the first did.
                    bool restartOpener = OpensSentence(index - length) && !Items[index - length].StartsLowercase;
                    Remove(index - length, index);
                    changed = true;
                    index -= length;
                    if (restartOpener && index < Items.Count && !Items[index].IsProtected)
                    {
                        CapitalizeFirstLetter(index);
                    }

                    continue;
                }

                Token filler = Items[index];
                string terminal = string.Concat(GraphemeText.Split(filler.Punctuation).Where(SentenceEnding.Contains));
                bool opens = OpensSentence(index);
                bool startsSentence = opens && !filler.StartsLowercase;
                bool isLast = index == Items.Count - 1;

                (int Lower, int Upper) removal = (index, index);
                if (index > 0 && !Items[index - 1].IsProtected)
                {
                    int previous = index - 1;
                    if (terminal.Length != 0)
                    {
                        StripClosingPauses(previous);
                        // "9 a.m., um?" keeps its question mark.
                        bool asks = terminal.Contains('?') || terminal.Contains('!');
                        if (!Items[previous].EndsWithSentencePunctuation
                            || (asks && !opens && !Items[previous].EndsAsking))
                        {
                            Items[previous].Text += terminal;
                        }
                    }
                    else if (Items[previous].EndsWithComma
                        && (isLast || (filler.EndsWithComma && !CommaBeforeIsGrammar(index))))
                    {
                        // The commas around a hesitation are its pause.
                        Items[previous].RemoveLast();
                        if (Items[previous].Text.Length == 0) { removal = (previous, index); }
                    }
                    else if (Items[previous].Punctuation.Length == 0 && filler.EndsWithComma && !isLast
                        && OpensAClause(index) && CommaBeforeIsGrammar(index))
                    {
                        // "Thank you um, I'll": the comma belongs to the clause.
                        Items[previous].Text += ",";
                    }
                }
                else if (index > 0 && terminal.Length != 0 && !EndsSentence(index - 1))
                {
                    Items.Insert(index, new Token(separator: "", text: terminal, isProtected: false));
                    removal = (index + 1, index + 1);
                }

                Remove(removal.Lower, removal.Upper);
                changed = true;
                index = removal.Lower;

                if (startsSentence && index < Items.Count && !Items[index].IsProtected)
                {
                    CapitalizeFirstLetter(index);
                }
            }

            return changed;
        }

        /// <summary>
        /// Drop the commas, semicolons and colons that close a word, so a full
        /// stop can take their place.
        /// </summary>
        private void StripClosingPauses(int index)
        {
            while (Items[index].Text.Length != 0 && Items[index].Text[^1] is ',' or ';' or ':')
            {
                Items[index].RemoveLast();
            }
        }

        /// <summary>
        /// Uppercase the first scalar only, as the Rust port does, so a
        /// combining mark is never turned into a letter.
        /// </summary>
        private void CapitalizeFirstLetter(int index)
        {
            string text = Items[index].Text;
            if (!Rune.TryGetRuneAt(text, 0, out Rune first) || !Rune.IsLower(first)) { return; }
            Items[index].Text = first.ToString().ToUpperInvariant() + text[first.Utf16SequenceLength..];
        }

        // MARK: Pass 4: paragraph commands

        public bool ApplyParagraphCommands()
        {
            bool changed = false;
            int index = 0;
            while (index < Items.Count)
            {
                if (ParagraphCommandLength(index) is not { } length)
                {
                    index += 1;
                    continue;
                }

                int last = index + length - 1;
                bool hasBefore = index > 0;
                bool hasAfter = last + 1 < Items.Count;

                if (hasBefore && !Items[index - 1].IsProtected && !EndsSentence(index - 1))
                {
                    StripClosingPauses(index - 1);
                    Items[index - 1].Text += ".";
                }

                Remove(index, last);
                if (hasBefore && hasAfter)
                {
                    Items[index].Separator = "\n\n";
                }

                if (index < Items.Count && !Items[index].IsProtected)
                {
                    CapitalizeFirstLetter(index);
                }

                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// How many tokens a paragraph command starting at <paramref name="index"/> spans, when
        /// punctuation sets it apart: "…point. New paragraph. Second…" is a
        /// command, "added a new paragraph about…" is a sentence.
        /// </summary>
        private int? ParagraphCommandLength(int index)
        {
            foreach (string[] command in ParagraphCommands)
            {
                int last = index + command.Length - 1;
                if (last >= Items.Count) { continue; }
                List<Token> tokens = Items.GetRange(index, command.Length);
                if (tokens.Any(static token => token.IsProtected)
                    || !tokens.Select(static token => token.Word).SequenceEqual(command)
                    || tokens.Take(command.Length - 1).Any(static token => token.Punctuation.Length != 0))
                {
                    continue;
                }

                bool apartBefore = index == 0 || Items[index - 1].Punctuation.Length != 0;
                bool apartAfter = last == Items.Count - 1 || Items[last].Punctuation.Length != 0;
                if (apartBefore && apartAfter) { return command.Length; }
            }

            return null;
        }

        // MARK: Pass 3: stutters

        /// <summary>
        /// A repeat across a comma with no hesitation between: "It, it
        /// crashes", "Can we, can we move", "I don't, I don't know". The second
        /// copy must run on into the sentence ("No, no, no." stays).
        /// </summary>
        public bool CollapseCommaRestarts()
        {
            bool changed = false;
            int index = 0;
            while (index + 1 < Items.Count)
            {
                if (!Items[index].EndsWithComma || IsKept(Items[index])
                    || RepeatedLength(index, index + 1, acrossHesitation: false) is not { } length
                    // "It, it crashes", "Honestly, we, we", "for the, for the one" -
                    // not "you and I, I think", nor the "no" of "Не, не надо".
                    || !(OpensSentence(index + 1 - length) || Items[index - length].EndsWithComma
                        || (length >= 2 && CommaStutterWords.Contains(Items[index].Word)))
                    || (length == 1 && Items[index].Word == "не")
                    || Items[index + length].Punctuation.Length != 0
                    || Items.GetRange(index + 1, length).Any(IsKept))
                {
                    index += 1;
                    continue;
                }

                int start = index + 1 - length;
                bool opener = OpensSentence(start) && !Items[start].StartsLowercase;
                Remove(start, index);
                if (opener && start < Items.Count && !Items[start].IsProtected) { CapitalizeFirstLetter(start); }
                changed = true;
                index = start;
            }

            return changed;
        }

        public bool CollapseStutters()
        {
            bool changed = false;
            int index = 1;
            while (index < Items.Count)
            {
                Token previous = Items[index - 1];
                Token current = Items[index];
                if (!IsStutter(previous, current, index))
                {
                    index += 1;
                    continue;
                }

                if (previous.EndsWithComma) { Items[index - 1].RemoveLast(); }
                Items[index - 1].Text += current.Punctuation;
                Items.RemoveAt(index);
                changed = true;
            }

            return changed;
        }

        private bool IsStutter(Token previous, Token current, int index)
        {
            if (IsKept(previous) || IsKept(current) || previous.Word != current.Word
                || HasNewline(current.Separator))
            {
                return false;
            }

            string word = current.Word;
            // Laughter: "He he, that's funny", "He he he." - a run of "he" that
            // opens a sentence and ends with punctuation.
            if (word == "he")
            {
                int first = index - 1;
                int last = index;
                while (first > 0 && Items[first - 1].Word == "he") { first -= 1; }
                while (last + 1 < Items.Count && Items[last].Punctuation.Length == 0 && Items[last + 1].Word == "he")
                {
                    last += 1;
                }

                if (OpensSentence(first) && Items[last].Punctuation.Length != 0) { return false; }
            }

            // A different case in mid-sentence is a different word: "220 В в сети".
            if (previous.StartsUppercase != current.StartsUppercase && !OpensSentence(index - 1)) { return false; }
            // "i" is the capital pronoun only: Polish "i i" is "and i", and
            // after a name it is a numeral: "World War I I moved".
            if (word == "i"
                && (!(previous.Text.StartsWith('I') && current.Text.StartsWith('I')) || FollowsAName(index - 1)))
            {
                return false;
            }

            // A spelled letter: "spelled A A R O N".
            static bool Spelled(Token token) => token.IsSingleLetter && token.StartsUppercase;
            if (GraphemeText.Count(word) == 1 && current.StartsUppercase
                && ((index >= 2 && Spelled(Items[index - 2])) || (Has(index + 1) && Spelled(Items[index + 1]))))
            {
                return false;
            }

            if (previous.Punctuation.Length == 0)
            {
                if (RunOnStutterWords.Contains(word))
                {
                    return current.Punctuation.Length == 0
                        && (!EnglishStutterWords.Contains(word) || ReadsAsEnglish);
                }

                return StutterWords.Contains(word);
            }

            if (previous.Punctuation != ",") { return false; }
            // "не, не" inside a sentence; opening one, "Не, не надо" says no.
            if (CommaStutterWords.Contains(word) || (word == "не" && !OpensSentence(index - 1))) { return true; }
            return OpensSentence(index - 1) && OpeningCommaStutterWords.Contains(word);
        }
    }
}
