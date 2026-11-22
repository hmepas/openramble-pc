
namespace DictationCore.Tests;

/// <summary>
/// Clean Verbatim: the cleanup that only ever deletes.
///
/// Every case here is one of two promises. Hesitations, stutters and spoken
/// corrections disappear; nothing the person meant is reworded, reordered or
/// lost. A false deletion is the worse failure, so the meaning-preservation
/// cases outnumber the removal ones. Many of them came from an adversarial
/// review that tried to make the stage delete something real.
/// </summary>
public sealed class CleanVerbatimTests
{
    // MARK: - Fillers

    [Fact]
    public void TestRemovesALeadingFillerAndCapitalizesWhatFollows()
    {
        Assert.Equal("So the build passed.", CleanVerbatim.Clean("Um, so the build passed."));
        Assert.Equal("The build passed.", CleanVerbatim.Clean("Uh, the build passed."));
    }

    /// <summary>
    /// The commas around a hesitation are the pause and go with it, unless the
    /// one before it is grammar: a list, an opening word, a word that needs it.
    /// </summary>
    [Fact]
    public void TestTheCommaBeforeAHesitationStaysOnlyWhereItIsGrammar()
    {
        Assert.Equal("We should ship it.", CleanVerbatim.Clean("We should, um, ship it."));
        Assert.Equal("Don't deploy on Friday.", CleanVerbatim.Clean("Don't, uh, deploy on Friday."));
        Assert.Equal(
            "I'll send the contract on Friday.",
            CleanVerbatim.Clean("I'll, uh, send the contract on Friday."));
        Assert.Equal(
            "Мы думаем, что надо подождать.",
            CleanVerbatim.Clean("Мы, э, думаем, что, ээ, надо подождать."));
        Assert.Equal("Send me the report.", CleanVerbatim.Clean("Send me the, uh, the report."));
        Assert.Equal(
            "Set the values to 10, 20, 30.",
            CleanVerbatim.Clean("Set the values to 10, 20, um, 30."));
        Assert.Equal("Set it to 10, 30.", CleanVerbatim.Clean("Set it to 10, um, 30."));
        Assert.Equal(
            "We need eggs, milk, butter and bread.",
            CleanVerbatim.Clean("We need eggs, milk, uh, butter and bread."));
        Assert.Equal(
            "Fix the files a.swift, b.swift, c.swift.",
            CleanVerbatim.Clean("Fix the files a.swift, b.swift, uh, c.swift."));
        Assert.Equal("Купи хлеб, молоко, сыр.", CleanVerbatim.Clean("Купи хлеб, молоко, э, сыр."));
        Assert.Equal("Invite Anna, Maria and Bob.", CleanVerbatim.Clean("Invite Anna, um, Maria and Bob."));
        Assert.Equal(
            "Пригласи Анну, Марию и Бориса.",
            CleanVerbatim.Clean("Пригласи Анну, э, Марию и Бориса."));
        Assert.Equal("I'm sorry, I love you.", CleanVerbatim.Clean("I'm sorry, um, I love you."));
        Assert.Equal(
            "Hi John, can you review this?",
            CleanVerbatim.Clean("Hi John, um, can you review this?"));
        Assert.Equal("Yes, that works for me.", CleanVerbatim.Clean("Yes, um, that works for me."));
        Assert.Equal("Да, это работает.", CleanVerbatim.Clean("Да, ээ, это работает."));
        Assert.Equal(
            "We need milk, eggs and bread.",
            CleanVerbatim.Clean("We need milk, um, eggs and bread."));
        Assert.Equal("Купи хлеб, молоко и сыр.", CleanVerbatim.Clean("Купи хлеб, э, молоко и сыр."));
        Assert.Equal(
            "Мы думаем, что надо подождать.",
            CleanVerbatim.Clean("Мы думаем, ээ, что надо подождать."));
        Assert.Equal("The build failed.", CleanVerbatim.Clean("The build, uh, failed."));
        Assert.Equal("Ну, Sentry упал.", CleanVerbatim.Clean("Ну, э, Sentry, э, упал."));
        Assert.Equal(
            "Thanks, John, see you tomorrow.",
            CleanVerbatim.Clean("Thanks, John, um, see you tomorrow."));
        Assert.Equal("Okay, Mike, I will check.", CleanVerbatim.Clean("Okay, Mike, um, I will check."));
        Assert.Equal("Hi, Anna, quick question.", CleanVerbatim.Clean("Hi, Anna, um, quick question."));
        Assert.Equal("No, no, that's wrong.", CleanVerbatim.Clean("No, no, um, that's wrong."));
        Assert.Equal("Well, actually, I think so.", CleanVerbatim.Clean("Well, actually, um, I think so."));
        Assert.Equal("Да, конечно, сделаю.", CleanVerbatim.Clean("Да, конечно, э, сделаю."));
        // After an opening word, a word that starts the clause takes no comma.
        Assert.Equal("Okay, this looks good.", CleanVerbatim.Clean("Okay, this, uh, looks good."));
        Assert.Equal("Yes, it works now.", CleanVerbatim.Clean("Yes, it, um, works now."));
        Assert.Equal("Ну, это работает.", CleanVerbatim.Clean("Ну, это, э, работает."));
        Assert.Equal("Yes, rename it to FooBar.", CleanVerbatim.Clean("Yes, rename, um, it to FooBar."));
        // "Thanks for", "Sorry for", "Спасибо за" are one phrase.
        Assert.Equal("Thanks for the quick reply.", CleanVerbatim.Clean("Thanks, um, for the quick reply."));
        Assert.Equal("Sorry for the delay.", CleanVerbatim.Clean("Sorry, uh, for the delay."));
        Assert.Equal("Спасибо за помощь.", CleanVerbatim.Clean("Спасибо, э, за помощь."));
        Assert.Equal(
            "Okay, thanks for the quick reply.",
            CleanVerbatim.Clean("Okay, thanks, uh, for the quick reply."));
        Assert.Equal("Well, the build failed.", CleanVerbatim.Clean("Well, um, the build failed."));
        Assert.Equal(
            "We should ship and celebrate.",
            CleanVerbatim.Clean("We should, um, ship and celebrate."));
    }

    /// <summary>
    /// Recognizers sometimes detach punctuation: "Uh , the build".
    /// </summary>
    [Fact]
    public void TestDetachedPunctuationIsHandledLikeAttachedPunctuation()
    {
        Assert.Equal("The build passed.", CleanVerbatim.Clean("Uh , the build passed."));
        Assert.Equal("Ship it now", CleanVerbatim.Clean("Ship it , uh , now"));
        Assert.Equal("I think.", CleanVerbatim.Clean("I think , uh ."));
    }

    /// <summary>
    /// Two-word interjections are words: "uh huh" means yes. "um hmm" stays
    /// even in a take that reads as English, where a lone "um" goes.
    /// </summary>
    [Fact]
    public void TestKeepsTwoWordInterjections()
    {
        foreach (string text in new[]
        {
            "Uh huh, ship it.", "Uh huh.", "Mm hmm, sounds good.", "Uh oh, the deploy failed.", "Um hmm.",
            "Um hmm, sounds good.", "Um hmm, that's the plan you know.",
        })
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    [Fact]
    public void TestRemovesABareFillerThatIsNotAWordAnywhere()
    {
        Assert.Equal("Then we ship it.", CleanVerbatim.Clean("Then uh we ship it."));
        Assert.Equal("It is fine.", CleanVerbatim.Clean("It is hmm fine."));
    }

    [Fact]
    public void TestAFillerAtASentenceStartInsideTheTakeCapitalizesTheNextWord()
    {
        Assert.Equal("Done. The next one.", CleanVerbatim.Clean("Done. Uh, the next one."));
    }

    [Fact]
    public void TestAFillerCarryingTheFullStopHandsItToTheWordBefore()
    {
        Assert.Equal("I think.", CleanVerbatim.Clean("I think, uh."));
        Assert.Equal("I think.", CleanVerbatim.Clean("I think uh."));
        Assert.Equal("Is it ready?", CleanVerbatim.Clean("Is it ready, uh?"));
    }

    [Fact]
    public void TestRemovesRussianGermanAndFrenchFillers()
    {
        Assert.Equal("Я думаю, что это работает.", CleanVerbatim.Clean("Ээ, я думаю, что это работает."));
        Assert.Equal("Я не уверен.", CleanVerbatim.Clean("Я, э, не уверен."));
        Assert.Equal("Давай завтра.", CleanVerbatim.Clean("Ммм, давай завтра."));
        Assert.Equal("Ich komme morgen.", CleanVerbatim.Clean("Ähm, ich komme morgen."));
        Assert.Equal("Je pense que oui.", CleanVerbatim.Clean("Euh, je pense que oui."));
    }

    [Fact]
    public void TestATakeThatIsOnlyHesitationBecomesEmpty()
    {
        Assert.Equal("", CleanVerbatim.Clean("Uh."));
        Assert.Equal("", CleanVerbatim.Clean("Uh, uh."));
    }

    // MARK: - "um" is a real word in German and Portuguese

    [Fact]
    public void TestKeepsGermanAndPortugueseUm()
    {
        string[] unchanged =
        [
            "Um fünf Uhr treffen wir uns.",
            "Wir gehen um fünf.",
            "Eu tenho um carro.",
            // Separable verbs put "um" right before the punctuation.
            "Benenne die Funktion um.",
            "Wir ziehen nächste Woche um.",
            "Bau die Funktion um, damit sie async ist.",
            "Wir steigen in Berlin um, dann fahren wir weiter.",
            "Er dreht sich um.",
            "Es geht um Geld.",
            // The Portuguese numeral.
            "Quero um, não dois.",
            "Só preciso de um.",
            "Conta comigo: um, dois, três.",
            "Um, dois, três, quatro.",
            "Mais um?",
            "Mais um, por favor.",
            "Quantos? Um, por favor.",
            "Comprei só um, mas era caro.",
            "Só preciso de um, obrigado.",
            "Schau dich um, vielleicht findest du was.",
            "Sie ziehen um, oder?",
            "Dreh dich um, bitte.",
            // Croatian, Slovak and Slovenian: "um" is "mind".
            "Ljudski um, kao i tijelo, treba odmor.",
            "Ľudský um, ako vieme, je zložitý.",
        ];
        foreach (string text in unchanged)
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    [Fact]
    public void TestAGermanHesitationNextToAGermanUmLeavesTheUm()
    {
        Assert.Equal("Es geht um Geld.", CleanVerbatim.Clean("Es geht um, äh, Geld."));
    }

    [Fact]
    public void TestAnUmWithoutItsOwnCommaGoesOnlyFromAnEnglishTake()
    {
        Assert.Equal("So the build passed.", CleanVerbatim.Clean("So um the build passed."));
        Assert.Equal("Um.", CleanVerbatim.Clean("Um."));
    }

    /// <summary>
    /// "word um," is how German separable verbs, the Portuguese "one" and the
    /// Slavic "mind" look too, so it goes only from a take that reads as
    /// English, where "um" is never a word.
    /// </summary>
    [Fact]
    public void TestAnUmAfterAWordGoesOnlyFromEnglish()
    {
        Assert.Equal("I need you to fix it.", CleanVerbatim.Clean("I need you to um, fix it."));
        Assert.Equal(
            "Ljudski um, kao i tijelo, treba odmor.",
            CleanVerbatim.Clean("Ljudski um, kao i tijelo, treba odmor."));
        Assert.Equal("Comprei só um, mas era caro.", CleanVerbatim.Clean("Comprei só um, mas era caro."));
        Assert.Equal("Ich will you to um, fix it.", CleanVerbatim.Clean("Ich will you to um, fix it."));
    }

    /// <summary>
    /// Hesitations in a row are one pause: the comma after the last one sets
    /// them all apart.
    /// A capitalized word in the middle of a sentence before "um" is a German
    /// noun with its separable verb ("Stell die Config um"), even in a take
    /// with English in it.
    /// </summary>
    [Fact]
    public void TestAnUmAfterACapitalizedWordStays()
    {
        Assert.Equal(
            "Stell die Config um, that should fix it.",
            CleanVerbatim.Clean("Stell die Config um, that should fix it."));
        Assert.Equal("And I think that works.", CleanVerbatim.Clean("And I um, think that works."));
        Assert.Equal("So I'm not sure about that.", CleanVerbatim.Clean("So I'm um, not sure about that."));
        Assert.Equal(
            "I think I'll call you tomorrow.",
            CleanVerbatim.Clean("I think I'll um, call you tomorrow."));
    }

    [Fact]
    public void TestHesitationsInARowGoTogether()
    {
        Assert.Equal("Ship it now", CleanVerbatim.Clean("Ship it, um uh, now"));
        Assert.Equal("We should ship it.", CleanVerbatim.Clean("We should um uh, ship it."));
    }

    /// <summary>
    /// The German and Portuguese markers ignore acronyms and English words.
    /// </summary>
    [Fact]
    public void TestEnglishTakesWithLookalikeMarkersStillLoseTheirUms()
    {
        Assert.Equal("The battery will die soon.", CleanVerbatim.Clean("Um, the battery will die soon."));
        Assert.Equal("I studied at MIT.", CleanVerbatim.Clean("Um, I studied at MIT."));
        Assert.Equal("The EU rules changed.", CleanVerbatim.Clean("Um, the EU rules changed."));
        Assert.Equal("The façade is done.", CleanVerbatim.Clean("Um, the façade is done."));
    }

    // MARK: - Real words that look like fillers

    [Fact]
    public void TestKeepsUnitsLettersAndAcronyms()
    {
        string[] unchanged =
        [
            "Use a 3 mm drill bit and a 10 mm bolt.",
            "The gap is 5 mm, not 6.",
            "Толщина стенки 5 мм.",
            "Format the date as dd mm yyyy.",
            "Слово начинается на э.",
            "Напиши букву э.",
            "Отправь эс эм эс.",
            "We trained an HMM on the data.",
            "We need an ERM framework.",
            "Build an MMM for the marketing team.",
            "Contact HM Revenue and Customs.",
            "I studied at UH.",
            "I studied at UM.",
            "Format the date as dd mmm yyyy.",
            "Гласные: а, о, у, э, ы.",
            "Буквы А, О, У, Э, Ы.",
        ];
        foreach (string text in unchanged)
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    /// <summary>
    /// The last item of a letter or format list is never a dangling hesitation.
    /// </summary>
    [Fact]
    public void TestTheLastItemOfAListIsNotADanglingHesitation()
    {
        foreach (string text in new[] { "Гласные: а, о, у, э", "Date formats: dd, mm, mmm", "Твёрдые гласные а, о, у, ы, э", "Гласные: а́, о́, у́, э, ы́." })
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    // MARK: - Stutters

    /// <summary>
    /// A double whose case differs mid-sentence is two words ("220 В в сети"),
    /// and "I I" after a capitalized word is a numeral and a pronoun.
    /// </summary>
    [Fact]
    public void TestDoublesThatDifferInCaseOrFollowANameStay()
    {
        foreach (string text in new[] { "Напряжение 220 В в сети.", "After World War I I moved to Paris." })
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
        Assert.Equal("Then I moved to Paris.", CleanVerbatim.Clean("Then I I moved to Paris."));
    }

    [Fact]
    public void TestCollapsesARepeatedFunctionWord()
    {
        Assert.Equal("I think the build passed.", CleanVerbatim.Clean("I I think the the build passed."));
        Assert.Equal("The deploy failed.", CleanVerbatim.Clean("The the the deploy failed."));
        Assert.Equal("Я думаю, что и это тоже.", CleanVerbatim.Clean("Я я думаю, что и и это тоже."));
    }

    [Fact]
    public void TestAStutterKeepsThePunctuationOfTheWordItDrops()
    {
        Assert.Equal("Ask me and I.", CleanVerbatim.Clean("Ask me and I I."));
    }

    [Fact]
    public void TestKeepsDoublesThatMeanSomething()
    {
        string[] unchanged =
        [
            "That was very very good.",
            "I had had enough.",
            "I think that that is right.",
            "Да да, конечно.",
            "Так так, посмотрим.",
            "What the problem is is that the cache is stale.",
            "What it was was a race condition.",
            "Log in in Safari and check the cookie.",
            "Turn the flag on on staging.",
            "Search for for loops in the codebase.",
            "The movie was so so.",
            "Use and and or instead of the symbols.",
            "My name is Aaron, spelled A A R O N.",
            "Take him to the OR or the ER.",
            "Tell IT it is broken.",
            "Conheço a cidade de A a Z.",
            "Il y a a priori un problème.",
            "Najważniejsze to to, że działa.",
            "Er weiß nicht, was was ist.",
            "Это это?",
            "I told you you were right.",
            "Did I tell you you need to sign?",
            "Before you run it it needs to compile.",
            "When I saw it it was broken.",
            "If you build it it will come.",
            "Thank you you guys are great.",
            "Zmienne i i j są indeksami.",
            "Use i I mean j.",
            "My my, how you have grown.",
            "Read the\nthe report",
        ];
        foreach (string text in unchanged)
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    [Fact]
    public void TestKeepsARepeatSeparatedByPunctuation()
    {
        Assert.Equal("No, no, no.", CleanVerbatim.Clean("No, no, no."));
        Assert.Equal("The answer is yes.", CleanVerbatim.Clean("The, the answer is yes."));
        Assert.Equal("I really think so.", CleanVerbatim.Clean("I, I really think so."));
        Assert.Equal("Я думаю.", CleanVerbatim.Clean("Я, я думаю."));
        Assert.Equal("Try it, it works.", CleanVerbatim.Clean("Try it, it works."));
        Assert.Equal("Between you and I, I think so.", CleanVerbatim.Clean("Between you and I, I think so."));
        Assert.Equal(
            "Это сделал он, он сам признался.",
            CleanVerbatim.Clean("Это сделал он, он сам признался."));
    }

    // MARK: - Spoken corrections

    [Fact]
    public void TestScratchThatAsItsOwnSentenceRemovesThePreviousSentence()
    {
        Assert.Equal(
            "The deploy passed.",
            CleanVerbatim.Clean("The deploy failed. Scratch that. The deploy passed."));
    }

    [Fact]
    public void TestScratchThatEndingASentenceRemovesThatSentence()
    {
        Assert.Equal(
            "The deploy passed.",
            CleanVerbatim.Clean("So the deploy failed, scratch that. The deploy passed."));
    }

    /// <summary>
    /// What Parakeet actually writes: the command between commas, mid-sentence.
    /// </summary>
    [Fact]
    public void TestScratchThatBetweenCommasRemovesWhatCameBeforeItInTheSentence()
    {
        Assert.Equal(
            "The deploy passed.",
            CleanVerbatim.Clean("Um, so that the deploy failed, uh, scratch that, the deploy passed."));
        Assert.Equal(
            "This stays. We go Monday.",
            CleanVerbatim.Clean("This stays. We go Friday, scratch that, we go Monday."));
    }

    [Fact]
    public void TestScratchThatOpeningASentenceRemovesThePreviousSentence()
    {
        Assert.Equal("It passed.", CleanVerbatim.Clean("The deploy failed. Scratch that, it passed."));
        Assert.Equal("It passed.", CleanVerbatim.Clean("Scratch that, it passed."));
    }

    [Fact]
    public void TestScratchThatAtTheStartOrEndHasNothingElseToRemove()
    {
        Assert.Equal("Hello there.", CleanVerbatim.Clean("Scratch that. Hello there."));
        Assert.Equal("", CleanVerbatim.Clean("Ship it on Friday. Scratch that"));
    }

    [Fact]
    public void TestRepeatedCorrectionsEachRemoveOneSentence()
    {
        Assert.Equal("Third.", CleanVerbatim.Clean("First. Second. Scratch that. Scratch that. Third."));
    }

    /// <summary>
    /// "Ah, scratch that" — Parakeet writes a spoken "uh" as "Ah" — still opens
    /// the sentence, and so do the words people lead a correction with.
    /// </summary>
    [Fact]
    public void TestALeadInBeforeScratchThatStillOpensTheSentence()
    {
        Assert.Equal(
            "The deploy passed.",
            CleanVerbatim.Clean("Um, so that the deploy failed. Ah, scratch that, the deploy passed."));
        Assert.Equal(
            "We meet on Monday.",
            CleanVerbatim.Clean("We meet on Friday. Sorry, scratch that, we meet on Monday."));
        Assert.Equal("", CleanVerbatim.Clean("Ship it today. No, scratch that."));
    }

    /// <summary>
    /// A sentence of nothing but hesitation is not what "scratch that" takes back.
    /// </summary>
    [Fact]
    public void TestAHesitationSentenceDoesNotAbsorbTheCorrection()
    {
        Assert.Equal("", CleanVerbatim.Clean("Ship it. Uh. Scratch that."));
        Assert.Equal("Ship it Monday.", CleanVerbatim.Clean("Ship it. Um. Scratch that. Ship it Monday."));
    }

    /// <summary>
    /// Titles before a name do not end a sentence; closing quotes and
    /// brackets do not hide one.
    /// </summary>
    [Fact]
    public void TestTitlesAndClosingQuotesDoNotMoveSentenceEdges()
    {
        Assert.Equal(
            "I met nobody.",
            CleanVerbatim.Clean("I met Dr. Smith today. Scratch that. I met nobody."));
        Assert.Equal("Dr. Jones.", CleanVerbatim.Clean("Call Dr. Smith, scratch that, Dr. Jones."));
        Assert.Equal(
            "He said \"stop.\" We stayed.",
            CleanVerbatim.Clean("He said \"stop.\" Then we left, scratch that, we stayed."));
        Assert.Equal(
            "The first test failed (it timed out.) We ship Monday.",
            CleanVerbatim.Clean("The first test failed (it timed out.) We ship Friday, scratch that, we ship Monday."));
    }

    /// <summary>
    /// When it is unclear whether a full stop ends a sentence, it does: then a
    /// correction can only take back too little, never a sentence the person
    /// meant to keep.
    /// </summary>
    [Fact]
    public void TestWhenInDoubtAFullStopEndsTheSentence()
    {
        (string, string)[] cases =
        [
            ("I opened an MR. Ping the team about it. Scratch that.", "I opened an MR."),
            ("Review the MR. Merge it after lunch. Scratch that, merge it tomorrow.", "Review the MR. Merge it tomorrow."),
            ("Set the timeout to 500 ms. Use three retries. Scratch that.", "Set the timeout to 500 ms."),
            ("I asked the Mrs. She said no. Scratch that, she said yes.", "I asked the Mrs. She said yes."),
            ("Install MS. Office is great. Scratch that.", "Install MS."),
            ("Is it ready? No, scratch that, yes.", "Is it ready? Yes."),
            ("Ship it today. No, scratch that.", ""),
            ("Is it ready? Uh. No, scratch that, yes.", "Is it ready? Yes."),
            ("Is it ready? Hmm. No, scratch that.", "Is it ready?"),
            ("She asked, \"Is it ready?\" No, scratch that, yes.", "She asked, \"Is it ready?\" Yes."),
            ("Is it ready?! No, scratch that, yes.", "Is it ready?! Yes."),
            ("I live at 5 Main Dr. The house is blue. Scratch that.", "I live at 5 Main Dr."),
            ("I met Dr. Will Smith today. Scratch that. I met nobody.", "I met nobody."),
            ("Call Ms. May tomorrow, scratch that, call her today.", "Call her today."),
            ("Ask Dr. Will Jones about it, scratch that.", ""),
            ("We met the new Prof. Students love her. Scratch that.", "We met the new Prof."),
            ("We met the new Prof. She seems nice. Scratch that.", "We met the new Prof."),
            ("Done. 3 tests failed. Scratch that, all passed.", "Done. All passed."),
            ("Okay. \"Ship it\" was the plan. Scratch that, we wait.", "Okay. We wait."),
            ("Meet at 3 p.m. Bring snacks. Scratch that. Bring drinks.", "Meet at 3 p.m. Bring drinks."),
            ("I asked twice and he said no. Ship it Friday. Scratch that. Ship it Monday.", "I asked twice and he said no. Ship it Monday."),
            ("Set the timeout to 5. Use port 80. Scratch that. Use port 8080.", "Set the timeout to 5. Use port 8080."),
            ("Upgrade to Python 3.12. Then run the tests. Scratch that. Run the linter.", "Upgrade to Python 3.12. Run the linter."),
            ("Bump the version to 1.4.2. Deploy on Friday, scratch that, on Monday.", "Bump the version to 1.4.2. On Monday."),
            ("The standup is at 9 a.m. We demo Friday, scratch that.", "The standup is at 9 a.m."),
            ("Edit config.yaml. Then restart nginx. Scratch that. Then reload nginx.", "Edit config.yaml. Then reload nginx."),
            ("Buy milk, eggs, etc. Ship on Friday. Scratch that. Ship Monday.", "Buy milk, eggs, etc. Ship Monday."),
            ("Pick plan B. Ship it Friday. Scratch that. Ship it Monday.", "Pick plan B. Ship it Monday."),
            ("We meet at 5. Scratch that. We meet at 6.", "We meet at 6."),
            ("The total is 5. Ship it Friday, scratch that.", "The total is 5."),
            ("Are we shipping today? No. Scratch that. Yes.", "Are we shipping today? Yes."),
            ("Should I merge it? No. Scratch that, yes, merge it.", "Should I merge it? Yes, merge it."),
            ("Meet at 5 p.m. tomorrow. Scratch that. Meet at 6.", "Meet at 5 p.m. Meet at 6."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
        }
    }

    /// <summary>
    /// A removal capitalizes what follows only when the removed piece was
    /// itself capitalized: after "e.g." or "vs." a sentence did not start.
    /// </summary>
    [Fact]
    public void TestARemovalCapitalizesOnlyWhatOpenedASentence()
    {
        (string, string)[] cases =
        [
            ("Handle the edge cases, e.g. uh, empty input.", "Handle the edge cases, e.g. empty input."),
            ("Нужно всё переписать, т.е. э, начать заново.", "Нужно всё переписать, т.е. начать заново."),
            ("I think... uh, maybe tomorrow.", "I think... maybe tomorrow."),
            ("Let's meet at 10 a.m. uh, on Monday.", "Let's meet at 10 a.m. on Monday."),
            ("Meet me at 5 p.m. on Monday, scratch that, on Tuesday.", "Meet me at 5 p.m. on Tuesday."),
            ("Compare tea vs. coffee, scratch that, water.", "Compare tea vs. water."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
        }
    }

    [Fact]
    public void TestAFillerAfterAnAbbreviationLeavesOneFullStop()
    {
        Assert.Equal("I'll be there by 9 a.m.", CleanVerbatim.Clean("I'll be there by 9 a.m., uh."));
        Assert.Equal("Bring plates, cups, etc.", CleanVerbatim.Clean("Bring plates, cups, etc., uh."));
    }

    /// <summary>
    /// "scratch that" is a command only when a comma or a sentence edge sets
    /// it apart; a bare hesitation before it is not a pause, and does not hide
    /// the comma before or after it either.
    /// </summary>
    [Fact]
    public void TestAHesitationAloneDoesNotMakeScratchThatACommand()
    {
        Assert.Equal(
            "If I were you I'd scratch that.",
            CleanVerbatim.Clean("If I were you I'd uh scratch that."));
        (string, string)[] cases =
        [
            ("We'll ship it on Friday, uh scratch that, on Monday.", "On Monday."),
            ("Ship it today, uh scratch that.", ""),
            ("Ship it on Friday. Scratch that uh, on Monday.", "On Monday."),
            ("Send it to Anna, um scratch that, to Bob.", "To Bob."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
            Assert.Equal(expected, CleanVerbatim.Clean(CleanVerbatim.Clean(input)));
        }
    }

    /// <summary>
    /// A sentence-final "um" or "э" after a comma goes like ", uh." does; a
    /// letter list keeps its last letter.
    /// </summary>
    [Fact]
    public void TestASentenceFinalHesitationAfterACommaGoes()
    {
        Assert.Equal(
            "Let me know if that works for you.",
            CleanVerbatim.Clean("Let me know if that works for you, um."));
        Assert.Equal("We should ship it on Friday.", CleanVerbatim.Clean("We should ship it on Friday, um."));
        Assert.Equal("I don't know.", CleanVerbatim.Clean("Um, I don't know, um."));
        Assert.Equal("Надо подождать.", CleanVerbatim.Clean("Надо подождать, э."));
        Assert.Equal("Is it ready?", CleanVerbatim.Clean("Is it ready, um?"));
        foreach (string text in new[] { "Гласные: а, о, у, э.", "Quero mais, um.", "Date formats: dd, mm, mmm." })
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
        Assert.Equal("Надо подождать\n", new TextPipeline().Process("Надо подождать, э, новая строка").Text);
        Assert.Equal("Это конец\n", new TextPipeline().Process("Это конец, э, новая строка.").Text);
    }

    [Fact]
    public void TestFillersRepeatUntilNothingChanges()
    {
        Assert.Equal("I think that", CleanVerbatim.Clean("I think that um um um um"));
        Assert.Equal("The build failed.", CleanVerbatim.Clean("The The um, build failed."));
        Assert.Equal("We should ship it.", CleanVerbatim.Clean("We We um, should ship it."));
        Assert.Equal(
            "This is the end\n",
            new TextPipeline().Process("This is the end um um um um, new line.").Text);
    }

    [Fact]
    public void TestAFillerAfterAShortSentenceStillCapitalizesAndKeepsOneFullStop()
    {
        Assert.Equal("Can we ship? No. Not yet.", CleanVerbatim.Clean("Can we ship? No. Uh, not yet."));
        Assert.Equal(
            "We met at 5 p.m. Then we left.",
            CleanVerbatim.Clean("We met at 5 p.m. Uh, then we left."));
        Assert.Equal("He said no. Then he left.", CleanVerbatim.Clean("He said no. Uh. Then he left."));
        Assert.Equal(
            "The price is 3. It goes up next week.",
            CleanVerbatim.Clean("The price is 3. Uh. It goes up next week."));
        Assert.Equal(
            "1. Install the deps.\n2. Run the tests.",
            CleanVerbatim.Clean("1. Install the deps.\n2. Uh, run the tests."));
    }

    [Fact]
    public void TestEveryScratchThatInASentenceIsConsidered()
    {
        Assert.Equal(
            "I will keep it.",
            CleanVerbatim.Clean("I will scratch that off the list, scratch that, I will keep it."));
    }

    [Fact]
    public void TestScratchInsideASentenceIsAnOrdinaryWord()
    {
        Assert.Equal(
            "Please scratch that line from the file.",
            CleanVerbatim.Clean("Please scratch that line from the file."));
        Assert.Equal(
            "If it chips, scratch that spot and repaint.",
            CleanVerbatim.Clean("If it chips, scratch that spot and repaint."));
        Assert.Equal("Scratch that itch.", CleanVerbatim.Clean("Scratch that itch."));
    }

    /// <summary>
    /// "Зачеркни" tells a reader to cross something out far more often than it
    /// takes back what was just said, so Russian has no spoken correction.
    /// </summary>
    [Fact]
    public void TestRussianInstructionsToCrossSomethingOutStayText()
    {
        string[] unchanged =
        [
            "В третьей строке опечатка. Зачеркни это, а потом перепиши.",
            "Если в договоре есть пункт про штрафы, зачеркни это.",
            "Видишь лишнее слово? Зачеркни.",
            "Зачеркни строку в файле.",
        ];
        foreach (string text in unchanged)
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    // MARK: - Everything together

    [Fact]
    public void TestTheMessyTakeBecomesWhatWasMeant()
    {
        Assert.Equal(
            "The deploy passed.",
            CleanVerbatim.Clean("Um, so the the deploy failed, uh, scratch that. The deploy passed."));
    }

    /// <summary>
    /// Transcripts the shipping model produced from synthesized disfluent
    /// speech (macOS `say`, OpenRamble 0.32.0 CLI), kept as they came out.
    /// </summary>
    [Fact]
    public void TestRealModelOutput()
    {
        Assert.Equal(
            "We should ship it on Friday. I think that the tests are green mostly.",
            CleanVerbatim.Clean("We should, um, ship it on Friday. I I think that the tests are green, uh, mostly."));
        Assert.Equal(
            "I need you to fix the login bug and then update the readme.",
            CleanVerbatim.Clean("Uh, I need you to um, fix the login bug, um, and then update the readme."));
        Assert.Equal(
            "Um 5 Uhr treffen wir uns im Büro.",
            CleanVerbatim.Clean("Um 5 Uhr treffen wir uns im Büro."));
        // Another voice: the model wrote the spoken "uh" as "Ah", a word.
        Assert.Equal(
            "Ah, I need you to fix the login bug and then update the readme.",
            CleanVerbatim.Clean("Ah, I need you to um, fix the login bug, um, and then update the readme."));
    }

    // MARK: - Meetings

    [Fact]
    public void TestAMeetingTranscriptKeepsSpokenCorrections()
    {
        Assert.Equal(
            "The deploy failed. Scratch that. It passed.",
            CleanVerbatim.Clean("Um, the deploy failed. Scratch that. It passed.", spokenCorrections: false));
        Assert.Equal(
            "It failed, scratch that. It passed.",
            new TextPipeline(spokenCorrections: false).Process("Uh, it failed, scratch that. It passed.").Text);
    }

    /// <summary>
    /// In a meeting a one-word reply is something a person said.
    /// </summary>
    [Fact]
    public void TestAMeetingKeepsATurnThatIsOnlyAnInterjection()
    {
        Assert.Equal("Hmm?", CleanVerbatim.Clean("Hmm?", spokenCorrections: false));
        Assert.Equal("Uh.", CleanVerbatim.Clean("Uh.", spokenCorrections: false));
    }

    // MARK: - "New paragraph" (prose apps only)

    [Fact]
    public void TestNewParagraphBreaksTheTextInProse()
    {
        Assert.Equal(
            "First point.\n\nSecond point.",
            CleanVerbatim.Clean("First point. New paragraph. Second point.", mode: PromptMode.Prose));
        Assert.Equal(
            "First point.\n\nSecond point.",
            CleanVerbatim.Clean("First point, new paragraph, second point.", mode: PromptMode.Prose));
        Assert.Equal(
            "Первый пункт.\n\nВторой пункт.",
            CleanVerbatim.Clean("Первый пункт. Новый абзац. Второй пункт.", mode: PromptMode.Prose));
    }

    [Fact]
    public void TestNewParagraphAfterAFillerStillBreaks()
    {
        Assert.Equal(
            "First point.\n\nSecond point.",
            CleanVerbatim.Clean("First point. New paragraph. Um, second point.", mode: PromptMode.Prose));
    }

    [Fact]
    public void TestNewParagraphAtTheEdgesHasNothingToSeparate()
    {
        Assert.Equal(
            "Hello there.",
            CleanVerbatim.Clean("New paragraph. Hello there.", mode: PromptMode.Prose));
        Assert.Equal(
            "Hello there.",
            CleanVerbatim.Clean("Hello there. New paragraph.", mode: PromptMode.Prose));
    }

    [Fact]
    public void TestNewParagraphIsTextWhenItIsPartOfASentence()
    {
        Assert.Equal(
            "I added a new paragraph about pricing.",
            CleanVerbatim.Clean("I added a new paragraph about pricing.", mode: PromptMode.Prose));
    }

    /// <summary>
    /// A newline in a terminal can run a command, so verbatim apps keep the words.
    /// </summary>
    [Fact]
    public void TestNewParagraphStaysWordsInVerbatimApps()
    {
        Assert.Equal(
            "First point. New paragraph. Second point.",
            CleanVerbatim.Clean("First point. New paragraph. Second point.", mode: PromptMode.Verbatim));
        Assert.Equal(
            "First point. New paragraph. Second point.",
            CleanVerbatim.Clean("First point. New paragraph. Second point."));
    }

    [Fact]
    public void TestAParagraphBreakSurvivesThePolisher()
    {
        Assert.Equal(
            "First point.\n\nSecond point",
            new TextPipeline(mode: PromptMode.Prose).Process("first point. new paragraph. second point").Text);
    }

    // MARK: - Protected spans are never touched

    [Fact]
    public void TestNeverTouchesProtectedSpans()
    {
        Assert.Equal("Run `uh` now.", CleanVerbatim.Clean("Run `uh` now."));
        Assert.Equal("Open /tmp/um/file please.", CleanVerbatim.Clean("Open /tmp/um/file please."));
        Assert.Equal("Call AppState AppState twice.", CleanVerbatim.Clean("Call AppState AppState twice."));
        Assert.Equal("Run `swift  test` now.", CleanVerbatim.Clean("Run `swift  test`, uh, now."));
    }

    [Fact]
    public void TestAFillerBeforeAnIdentifierDoesNotCapitalizeIt()
    {
        Assert.Equal("onTextInserted fired.", CleanVerbatim.Clean("Uh, onTextInserted fired."));
    }

    // MARK: - Shape of the text

    [Fact]
    public void TestLeavesCleanTextExactlyAsItWas()
    {
        string[] samples =
        [
            "",
            "   ",
            "Hello.",
            "Hello,  world.",
            "first line\nsecond line",
            "Ship it.\n",
        ];
        foreach (string sample in samples)
        {
            Assert.Equal(sample, CleanVerbatim.Clean(sample));
        }
    }

    [Fact]
    public void TestALineBreakSurvivesARemovalNextToIt()
    {
        Assert.Equal(
            "First paragraph.\n\nSecond paragraph.",
            CleanVerbatim.Clean("First paragraph.\n\nUh, second paragraph."));
        Assert.Equal(
            "Line one.\nLine three.",
            CleanVerbatim.Clean("Line one.\nLine two. Scratch that. Line three."));
        Assert.Equal(
            "First paragraph.\r\n\r\nSecond paragraph.",
            CleanVerbatim.Clean("First paragraph.\r\n\r\nUh, second paragraph."));
        Assert.Equal("Hello.\r\nThere.", CleanVerbatim.Clean("Hello.\r\nUh, there."));
    }

    [Fact]
    public void TestIsIdempotent()
    {
        string[] samples =
        [
            "Um, so the the deploy failed, uh, scratch that. The deploy passed.",
            "We should, um, ship it.",
            "I I think, uh.",
            "Я, э, не уверен.",
            "Set the values to 10, 20, um, 30.",
            "Ship it, um uh, now",
        ];
        foreach (string sample in samples)
        {
            var once = CleanVerbatim.Clean(sample);
            Assert.Equal(once, CleanVerbatim.Clean(once));
        }
    }

    // MARK: - Review round 6

    /// <summary>
    /// The comma the recognizer wrote stays unless it can only be the pause:
    /// after a word that cannot end a phrase, in "Thanks, um, for", or after
    /// a sentence's opening subject. Lists, clauses, names and introductory
    /// words keep it.
    /// </summary>
    [Fact]
    public void TestTheCommaBeforeAHesitationStaysUnlessItCanOnlyBeThePause()
    {
        (string, string)[] cases =
        [
            ("Купи хлеб, ээ, молоко, сыр.", "Купи хлеб, молоко, сыр."),
            ("Invite Anna, um, Maria, and Bob.", "Invite Anna, Maria, and Bob."),
            ("Add tests for login, uh, signup, and logout.", "Add tests for login, signup, and logout."),
            ("I met John, um, Paul.", "I met John, Paul."),
            ("Thank you, um, I'll take a look.", "Thank you, I'll take a look."),
            ("Thank you um, I'll take a look.", "Thank you, I'll take a look."),
            ("Refactor the function, uh, it's too long.", "Refactor the function, it's too long."),
            ("Let's use SQLite for now, um, it's simpler.", "Let's use SQLite for now, it's simpler."),
            ("Я не знаю, ээ, может быть завтра.", "Я не знаю, может быть завтра."),
            ("Anna, um, can you review my PR?", "Anna, can you review my PR?"),
            ("Claude, um, please refactor this function.", "Claude, please refactor this function."),
            ("Коллеги, ээ, завтра созвон в десять.", "Коллеги, завтра созвон в десять."),
            ("Hey team, um, quick update.", "Hey team, quick update."),
            ("However, um, the tests still fail.", "However, the tests still fail."),
            ("Кстати, ээ, я забыл ключи.", "Кстати, я забыл ключи."),
            ("Well, basically, um, we ship it on Friday.", "Well, basically, we ship it on Friday."),
            ("Ну, короче, э, давай завтра.", "Ну, короче, давай завтра."),
            ("Hi everyone, um, thanks for joining.", "Hi everyone, thanks for joining."),
            ("Привет, ребята, э, есть минутка?", "Привет, ребята, есть минутка?"),
            ("Dear John, um, thanks for the note.", "Dear John, thanks for the note."),
            ("Honestly, um, I'm not sure.", "Honestly, I'm not sure."),
            ("So Sentry, uh, is down.", "So Sentry is down."),
            ("Es geht um, äh, Geld.", "Es geht um Geld."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
        }
    }

    /// <summary>
    /// Round eight: the comma before a hesitation goes unless something keeps
    /// it, measured on a judged corpus; restarts across a hesitation; letters
    /// set against letters.
    /// </summary>
    [Fact]
    public void TestRoundEight()
    {
        (string, string)[] cases =
        [
            ("Try it, um, it works.", "Try it, it works."),
            ("Ask Uma, uh, Uma knows the answer.", "Ask Uma, Uma knows the answer."),
            ("That's very, um, very kind of you.", "That's very, very kind of you."),
            ("Мы обсудили бюджет, э, бюджет утвердили в пятницу.", "Мы обсудили бюджет, бюджет утвердили в пятницу."),
            ("I bought milk, uh, milk is expensive now.", "I bought milk, milk is expensive now."),
            ("Version 10, um, 10 users signed up.", "Version 10, 10 users signed up."),
            ("I sent the file, um, file names are wrong.", "I sent the file, file names are wrong."),
            ("Я отправил, ммм, отправил договор юристу.", "Я отправил договор юристу."),
            ("Не длиннее пятнадцати, ээ, пятнадцати секунд.", "Не длиннее пятнадцати секунд."),
            ("Сделай отдельный, э, отдельный endpoint.", "Сделай отдельный endpoint."),
            ("So I, um, I think it's fine.", "So I think it's fine."),
            ("It's a, um, an issue.", "It's an issue."),
            ("I gave it to the, to the team.", "I gave it to the team."),
            ("Between you and I, and I mean this, it's fine.", "Between you and I, and I mean this, it's fine."),
            ("The tests pass, um, and the build failed.", "The tests pass and the build failed."),
            ("Get milk, eggs, um, and the bread.", "Get milk, eggs, and the bread."),
            ("The API is done, uh, and the docs are live.", "The API is done, and the docs are live."),
            ("Call the vendor, uh, Sam has the number.", "Call the vendor, Sam has the number."),
            ("Пишется э, а не е.", "Пишется э, а не е."),
            ("Он сказал, э, а не пойти ли нам.", "Он сказал, а не пойти ли нам."),
            ("Напиши мне, э, если что.", "Напиши мне, если что."),
            ("Только, э, если успеем.", "Только если успеем."),
            ("Refer her to Dr. Haddad, uh, scratch that, to Dr. Moreau.", "To Dr. Moreau."),
            ("Не, не надо.", "Не, не надо."),
            ("Это не, не критично.", "Это не критично."),
            ("Use and and or instead of the symbols.", "Use and and or instead of the symbols."),
            ("Fruit, e.g., um, apples.", "Fruit, e.g., apples."),
            ("Нужен ноутбук, т. е., э, лёгкий.", "Нужен ноутбук, т. е. лёгкий."),
            ("Привет, ээ, хотел спросить.", "Привет, хотел спросить."),
            ("Марина, ээ, сказала, что да.", "Марина сказала, что да."),
            ("Маша, э, ты сделала?", "Маша, ты сделала?"),
            ("Я не буду, э, трогать модуль, пока нет тестов.", "Я не буду трогать модуль, пока нет тестов."),
            ("Eu queria uma, hmm, uma sopa.", "Eu queria uma sopa."),
            ("Can you, um, look at the failing test in auth.test.ts?", "Can you look at the failing test in auth.test.ts?"),
            ("Добрый день, ээ, Наталья Сергеевна.", "Добрый день, Наталья Сергеевна."),
            ("Thanks, John, um, see you tomorrow.", "Thanks, John, see you tomorrow."),
            ("Я не знаю, ээ, может быть завтра.", "Я не знаю, может быть завтра."),
            ("На ревью три PR: от Лены, от Тимура, э, от Вадима.", "На ревью три PR: от Лены, от Тимура, от Вадима."),
            ("I booked the flights, uh, and Sam is handling the hotel.", "I booked the flights, and Sam is handling the hotel."),
            ("I like the red car, um, the one with stripes.", "I like the red car, the one with stripes."),
            ("Okay, if it fails, um, we roll back.", "Okay, if it fails, we roll back."),
            ("Okay, this, uh, looks good.", "Okay, this looks good."),
            ("I need, um, a new laptop, and a mouse.", "I need a new laptop, and a mouse."),
            ("Bring a tent, um, a warm jacket, and boots.", "Bring a tent, a warm jacket, and boots."),
            ("В общем, ээ, всё готово.", "В общем, всё готово."),
            ("Of course, um, we can do that.", "Of course, we can do that."),
            ("Мы, эм, честно говоря, не ожидали.", "Мы, честно говоря, не ожидали."),
            ("Это, э, может быть, лучший вариант.", "Это, может быть, лучший вариант."),
            ("Сервер, который мы купили в мае, ээ, сломался.", "Сервер, который мы купили в мае, сломался."),
            ("Дом, который построили для, э, нас.", "Дом, который построили для нас."),
            ("The server, which we set up last week, um, crashed.", "The server, which we set up last week, crashed."),
            ("Tell me, which one, um, you want.", "Tell me, which one you want."),
            ("Высылаю, э, акт сверки.", "Высылаю акт сверки."),
            ("Думаю, э, это сработает.", "Думаю, это сработает."),
            ("Андрей, э, посмотри пулреквест.", "Андрей, посмотри пулреквест."),
            ("We're meeting with the, um, with Kowalski's team.", "We're meeting with Kowalski's team."),
            ("It's a, uh, it's an issue.", "It's an issue."),
            ("Put it in the, uh, in my bag.", "Put it in my bag."),
            ("Talk to the, um, to Anna.", "Talk to Anna."),
            ("It needs a 3 mm, uh, a 3 mm margin.", "It needs a 3 mm margin."),
            ("It fires every, every 15 minutes.", "It fires every 15 minutes."),
            ("Перенести приём на среду, ммм.", "Перенести приём на среду."),
            ("Нам нужны, ммм, два монитора.", "Нам нужны два монитора."),
            ("Meet me at 5 p.m. on Monday, scratch that, on Tuesday.", "Meet me at 5 p.m. on Tuesday."),
            ("When I asked, the agent said mm hmm, uh, and left.", "When I asked, the agent said mm hmm and left."),
            ("Every, um, every time it fails.", "Every time it fails."),
            ("I want the, um, the blue one.", "I want the blue one."),
            ("Use the format dd, mmm.", "Use the format dd, mmm."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
            Assert.Equal(expected, CleanVerbatim.Clean(CleanVerbatim.Clean(input)));
        }
    }

    /// <summary>
    /// A restart around a hesitation is one word said twice: the first copy
    /// goes with the hesitation instead of leaving a glued double.
    /// </summary>
    [Fact]
    public void TestARestartAroundAHesitationKeepsOneCopy()
    {
        (string, string)[] cases =
        [
            ("It's a, uh, a memory leak.", "It's a memory leak."),
            ("I'm, uh, I'm not sure.", "I'm not sure."),
            ("I want you to, uh, to add logging.", "I want you to add logging."),
            ("Write a function that, um, that returns the sum.", "Write a function that returns the sum."),
            ("Это, э, это баг.", "Это баг."),
            ("Can you, um, can you check it?", "Can you check it?"),
            ("Make sure, um, make sure it compiles.", "Make sure it compiles."),
            ("Я хотел, э, я хотел сказать.", "Я хотел сказать."),
            ("It's, um, it's not ready.", "It's not ready."),
            ("I, um, I think we should ship it.", "I think we should ship it."),
            ("Я, э, я думаю, что да.", "Я думаю, что да."),
            ("Давай в, э, в субботу.", "Давай в субботу."),
            ("We need a, um, a new plan.", "We need a new plan."),
            ("И, э, и потом мы уйдём.", "И потом мы уйдём."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
        }
    }

    /// <summary>
    /// A conjunction or preposition opens a phrase of its own, so the word
    /// before it can close the phrase before: "shipped it and, um, it works"
    /// says "it" twice and means both. A phrase stopped on one is begun again
    /// only from a sentence's start or a comma.
    /// </summary>
    [Fact]
    public void TestAWordSaidAgainAfterAConjunctionAndAHesitationStays()
    {
        (string, string)[] cases =
        [
            ("We shipped it and, um, it works.", "We shipped it and it works."),
            ("So we, uh, shipped it and, um, it works.", "So we shipped it and it works."),
            ("We shipped it and uh, it works.", "We shipped it and it works."),
            ("We shipped it but, uh, it crashed on launch.", "We shipped it but it crashed on launch."),
            ("I tested it so, um, it should be fine.", "I tested it so it should be fine."),
            ("We'll ship it when, uh, it is ready.", "We'll ship it when it is ready."),
            ("Ship it if, um, it passes review.", "Ship it if it passes review."),
            ("I can do it or, uh, it can wait.", "I can do it or it can wait."),
            ("We test it then, um, it ships.", "We test it then it ships."),
            ("I'll ask Sam and, uh, Sam will know.", "I'll ask Sam and Sam will know."),
            ("We moved the server and, uh, the server is faster now.", "We moved the server and the server is faster now."),
            ("I told you that, um, you should come.", "I told you that you should come."),
            ("We talked to them about, um, them joining.", "We talked to them about them joining."),
            ("Мы сделали это и, э, это работает.", "Мы сделали это и это работает."),
            ("Позвони ему или, э, ему напишет Лена.", "Позвони ему или ему напишет Лена."),
            // Begun again from the sentence's start or a comma, it is a restart.
            ("I think that, um, I think we should wait.", "I think we should wait."),
            ("Okay, I think that, um, I think we should wait.", "Okay, I think we should wait."),
            ("Let's meet at, uh, let's meet on Monday.", "Let's meet on Monday."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
            Assert.Equal(expected, CleanVerbatim.Clean(input, spokenCorrections: false));
        }
    }

    [Fact]
    public void TestRoundSixKeepsWhatWasMeant()
    {
        foreach (string text in new[]
        {
            "Is it safe to merge? Uh uh.",
            "Nuh uh, that's not true.",
            "Uh uh, don't merge that.",
            "The old migration is broken. Scratch that? Or fix it?",
            "If I say, scratch that, it should delete the sentence.",
            "When the user says, scratch that, remove the last sentence.",
            "Use the format dd, mmm, yyyy.",
            "The date tokens are dd, mmm, and yyyy.",
            "He he, that's funny.",
            "He he he, nice one.",
            "Гласные: а, о, у, э, ы.",
        })
        {
            Assert.Equal(text, CleanVerbatim.Clean(text));
        }
    }

    [Fact]
    public void TestRoundSixRemovesWhatWasNotMeant()
    {
        (string, string)[] cases =
        [
            ("Can you come at 9 a.m., um?", "Can you come at 9 a.m.?"),
            ("Is it ready by 5 p.m., uh?", "Is it ready by 5 p.m.?"),
            ("Hi Anna, Dr. Lee is here. Scratch that.", ""),
            ("I asked the team. Dr. Smith said no. Scratch that.", "I asked the team."),
            ("On Monday Dr. Smith called. Scratch that, on Tuesday.", "On Tuesday."),
            ("Invite Anna, Dr. Smith and Bob. Scratch that.", ""),
            ("Call Dr. Uh scratch that, call the nurse.", "Call the nurse."),
            ("Book it with Dr. Scratch that. Book it with the nurse.", "Book it with the nurse."),
            ("That's the plan. Um. Let's see what you think.", "That's the plan. Let's see what you think."),
            ("I don't know. Um... maybe you should ask them.", "I don't know. Maybe you should ask them."),
        ];
        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, CleanVerbatim.Clean(input));
            Assert.Equal(expected, CleanVerbatim.Clean(CleanVerbatim.Clean(input)));
        }
    }

    // MARK: - In the pipeline

    [Fact]
    public void TestRunsAfterTheDictionaryAndBeforeThePolisher()
    {
        var pipeline = new TextPipeline(replacements: [
            new DictionaryReplacement(spoken: "sentry", written: "Sentry", inflects: false),
        ]);
        Assert.Equal("So Sentry is down", pipeline.Process("um, so sentry, uh, is down").Text);
    }

    [Fact]
    public void TestTheDictionaryCheckpointIsRecordedBeforeCleanup()
    {
        var run = new TextPipeline().Run("Uh, the build passed.");
        Assert.Equal("Uh, the build passed.", run.Provenance.AfterDictionary);
        Assert.Equal("The build passed.", run.Output.Text);
    }

    /// <summary>
    /// A command with nothing left to act on is just words, as when it is
    /// spoken alone: never an empty insert that reads as "nothing recognized".
    /// </summary>
    [Fact]
    public void TestACommandAfterOnlyHesitationIsInsertedAsWords()
    {
        var newLine = new TextPipeline().Process("Uh, new line.");
        Assert.Equal("New line.", newLine.Text);
        Assert.Null(newLine.Command);

        var send = new TextPipeline(allowPressReturnCommand: true).Process("Uh, send it.");
        Assert.Equal("Send it.", send.Text);
        Assert.Null(send.Command);
    }

    /// <summary>
    /// What the person's own dictionary wrote is never a filler or a stutter.
    /// </summary>
    [Fact]
    public void TestWordsTheDictionaryWroteAreKept()
    {
        var band = new TextPipeline(replacements: [new DictionaryReplacement(spoken: "the the", written: "The The", inflects: false)]);
        Assert.Equal("I love The The band.", band.Process("I love the the band.").Text);
        var company = new TextPipeline(replacements: [new DictionaryReplacement(spoken: "hm labs", written: "Hmm Labs", inflects: false)]);
        Assert.Equal("I work at Hmm Labs.", company.Process("I work at hm labs.").Text);
    }

    [Fact]
    public void TestAStutterAfterARemovedHesitationIsCollapsedInOnePass()
    {
        Assert.Equal("Ship it\n", new TextPipeline().Process("Ship it, um uh, new line").Text);
    }

    [Fact]
    public void TestAHesitationBeforeATrailingCommandGoes()
    {
        Assert.Equal(
            "We should ship it\n",
            new TextPipeline().Process("We should ship it, um, new line.").Text);
        Assert.Equal("This is the end\n", new TextPipeline().Process("This is the end um, new line.").Text);
        Assert.Equal(
            "Гласные: а, о, у, э\n",
            new TextPipeline().Process("Гласные: а, о, у, э, новая строка").Text);
        var um = new TextPipeline().Process("Um, new line.");
        Assert.Equal("New line.", um.Text);
        var send = new TextPipeline(allowPressReturnCommand: true).Process("Um, send it.");
        Assert.Equal("Send it.", send.Text);
        Assert.Null(send.Command);
    }

    /// <summary>
    /// In a terminal a hesitation or stutter right before "Send it." is gone
    /// once the text is clean, and what is left is "Send it." as its own final
    /// sentence: Return, not the words typed at the prompt.
    /// </summary>
    [Fact]
    public void TestAHesitationOrStutterBeforeSendItStillPressesReturn()
    {
        var terminal = new TextPipeline(allowPressReturnCommand: true);
        foreach (string said in new[] { "git status. Um, send it.", "git status. Uh, send it.", "git status. Send, send it." })
        {
            var output = terminal.Process(said);
            Assert.Equal("Git status.", output.Text);
            Assert.Equal(TrailingCommand.PressReturn, output.Command);
        }
        // Every other rule still holds: never mid-sentence, never alone,
        // never outside a terminal.
        foreach (string said in new[] { "Review the doc, um, send it.", "and then we, uh, send it", "Uh, send it." })
        {
            Assert.Null(terminal.Process(said).Command);
        }
        var elsewhere = new TextPipeline().Process("git status. Um, send it.");
        Assert.Null(elsewhere.Command);
        Assert.Equal("Git status. Send it.", elsewhere.Text);
    }

    /// <summary>
    /// Cleanup keeps a hesitation that is also a word ("Um.", "Э.") when it
    /// stands alone, so it can be all that is left before "Send it.". Nothing
    /// but hesitation is nothing to send: the take is typed as words and
    /// Return is not pressed. One real word is enough.
    /// </summary>
    [Fact]
    public void TestNothingButAHesitationIsNeverTypedAndSent()
    {
        var terminal = new TextPipeline(allowPressReturnCommand: true);
        foreach (string said in new[]
        {
            "Um. Um, send it.", "Um. Uh, send it.", "Um.\u2029Uh, send it.", "Um. Send it.",
            "Hmm, um. Send it.", "Er. Send it.", "Mmm. Send it.",
            "\u042D. \u042D, \u043E\u0442\u043F\u0440\u0430\u0432\u044C.",
            "\u042D\u043C. \u042D, \u043E\u0442\u043F\u0440\u0430\u0432\u044C.",
        })
        {
            Assert.Null(terminal.Process(said).Command);
        }
        Assert.Equal("Um. Send it.", terminal.Process("Um. Um, send it.").Text);
        foreach (string said in new[] { "Yes. Um, send it.", "\u0414\u0430. \u042D, \u043E\u0442\u043F\u0440\u0430\u0432\u044C." })
        {
            Assert.Equal(TrailingCommand.PressReturn, terminal.Process(said).Command);
        }
    }

    /// <summary>
    /// Return is decided on the cleaned text only when "Send it." was the last
    /// thing said. Words after it that cleanup takes away ("Stop, scratch
    /// that.") mean the person was taking the send back, and a dictionary
    /// entry that writes "send it" was never the command said aloud.
    /// </summary>
    [Fact]
    public void TestSendItIsReturnOnlyWhenItWasTheLastThingSaid()
    {
        var terminal = new TextPipeline(allowPressReturnCommand: true);
        foreach (string said in new[]
        {
            "Fix the failing test. Send it. Stop, scratch that.",
            "git push --force. Send it. Hold on, scratch that.",
            "git push --force. Send it. Not yet. Scratch that.",
            "rm -rf build. Send it. Uh, hold on, scratch that.",
        })
        {
            Assert.Null(terminal.Process(said).Command);
        }
        var shipIt = new TextPipeline(
            replacements: [new DictionaryReplacement(spoken: "ship it", written: "send it", inflects: false)],
            allowPressReturnCommand: true
        );
        foreach (string said in new[] { "git status. Ship it.", "git status. Um, ship it." })
        {
            Assert.Null(shipIt.Process(said).Command);
        }
    }

    /// <summary>
    /// The sentence end before "Send it." has to be one the person said. A
    /// dictionary entry that writes one ("Acme Inc.", ".") is not a sentence
    /// ending in what was said, so "tell acme send it" stays words.
    /// </summary>
    [Fact]
    public void TestASentenceEndTheDictionaryWroteDoesNotPressReturn()
    {
        var acme = new TextPipeline(
            replacements: [new DictionaryReplacement(spoken: "acme", written: "Acme Inc.")],
            allowPressReturnCommand: true
        );
        foreach (string said in new[] { "tell acme send it", "ask acme, um, send it" })
        {
            Assert.Null(acme.Process(said).Command);
        }
        var dot = new TextPipeline(
            replacements: [new DictionaryReplacement(spoken: "dot", written: ".")],
            allowPressReturnCommand: true
        );
        Assert.Null(dot.Process("git add dot send it").Command);
        Assert.Equal(TrailingCommand.PressReturn, acme.Process("tell acme. Um, send it.").Command);
    }

    /// <summary>
    /// The built-in dictionary writes "commit в" and "Docker и в Kubernetes";
    /// only a word written with a capital is a name to protect, so Russian
    /// stutters still collapse in the shipping app.
    /// </summary>
    [Fact]
    public void TestTheStarterDictionaryDoesNotProtectConnectingWords()
    {
        var pipeline = new TextPipeline(replacements: StarterDictionary.Missing([]));
        Assert.Equal("Я и ты пойдём.", pipeline.Process("Я и и ты пойдём.").Text);
        Assert.Equal("Положи файл в папку.", pipeline.Process("Положи файл в в папку.").Text);
        var words = CleanVerbatim.ProtectedWords([
            new DictionaryReplacement(spoken: "коммит в", written: "commit в", inflects: false),
            new DictionaryReplacement(spoken: "the the", written: "The The", inflects: false),
        ]);
        Assert.Equal(["the"], words.ToArray());
    }
}

/// <summary>A take whose every word was taken back is not a recognition failure.</summary>
[Collection(DictationControllerCollection.Name)]
public sealed class TakenBackDictationTests
{
    [Fact]
    public async Task TestATakenBackTakeIsKeptQuietlyAndNotReportedAsUnrecognized()
    {
        var inserter = new FakeInserter();
        var overlay = new FakeOverlay();
        var sounds = new FakeSounds();
        var controller = new DictationController(
            capture: new FakeCapture(),
            transcribe: (_, _) => Task.FromResult(new ASRResult(
                "Ship it Friday. Scratch that.",
                audioDuration: TimeSpan.FromSeconds(2),
                processingDuration: TimeSpan.FromSeconds(0.1))),
            inserter: inserter,
            overlay: overlay,
            sounds: sounds,
            pipeline: _ => new TextPipeline());
        var archived = new List<string>();
        controller.OnTakeFinished = (text, _, _) => { lock (archived) { archived.Add(text); } return true; };

        controller.Begin(handsFree: false, isEnabled: true, isModelReady: true);
        await Settle(12);
        controller.Stop();
        await Settle(24);

        Assert.Empty(inserter.InsertedTexts);
        // Kept in History with what was heard.
        lock (archived) { Assert.Equal(["Ship it Friday. Scratch that."], archived); }
        Assert.Contains(overlay.Notices, notice => notice.Message == "Nothing left to insert after cleanup.");
        Assert.DoesNotContain(overlay.Notices, notice => notice.WordsDidNotLand);
        // Nothing was lost, so no alarm.
        Assert.Equal(0, sounds.AttentionPlays);
    }

    private static async Task Settle(int iterations)
    {
        for (int i = 0; i < iterations; i += 1)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }
}
