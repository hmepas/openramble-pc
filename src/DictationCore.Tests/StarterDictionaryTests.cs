using System.Text;
using System.Text.Json;

namespace DictationCore.Tests;

/// <summary>
/// Checks on the real output of the model.
///
/// The texts below are not fiction: this is how Parakeet recognized the phrase
/// "Нужно сделать pull request и запустить deploy через Sentry",
/// dictated in Russian. The model writes English words in Cyrillic, and the dictionary
/// exists precisely to return them to their normal appearance.
/// </summary>
public sealed class StarterDictionaryTests
{
    [Fact]
    public void TestContainsNoPointlessSelfReplacements()
    {
        // Replacing a word with itself does nothing, it just clutters the list,
        // which the user then views with their eyes.
        foreach (DictionaryReplacement replacement in StarterDictionary.Developer)
        {
            Assert.True(
                replacement.Spoken.ToLowerInvariant() != replacement.Written.ToLowerInvariant(),
                $"Бессмысленная замена: {replacement.Spoken}");
        }
    }

    [Fact]
    public void TestMissingSkipsWhatUserAlreadyHas()
    {
        DictionaryReplacement[] existing =
        [
            new(spoken: "деплой", written: "выкатка"),
        ];

        IReadOnlyList<DictionaryReplacement> missing = StarterDictionary.Missing(existing);

        // Our own replacement is more important than the workpiece - we do not offer a duplicate.
        Assert.DoesNotContain(missing, replacement =>
            replacement.Spoken.ToLowerInvariant() == "деплой");
        Assert.Contains(missing, replacement =>
            replacement.Spoken == "пул реквест");
    }

    [Fact]
    public void TestEveryEntryIsUsable()
    {
        foreach (DictionaryReplacement replacement in StarterDictionary.Developer)
        {
            Assert.NotEqual("", replacement.Spoken.Trim());
            Assert.NotEqual("", replacement.Written.Trim());
        }
    }
}

/// <summary>The sign "do not boost with acoustics" lives in the data of the workpiece.</summary>
public sealed class StarterDictionaryBoostFlagTests
{
    /// <summary>
    /// Main tripwire of the task.
    ///
    /// The hardcode has been removed from <c>VocabularyBoost</c>, and if the sign is not included here,
    /// deploy/Sentry/commit will silently return to the acoustics - that is, it will roll back
    /// a measurement that was worth a separate day.
    /// </summary>
    [Fact]
    public void TestDangerousTermsCarryTheFlagInTheData()
    {
        var flagged = StarterDictionary.Developer
            .Where(replacement => replacement.NoAcousticBoost)
            .Select(replacement => replacement.Written)
            .ToHashSet();
        Assert.True(flagged.IsSupersetOf(["deploy", "Sentry", "commit"]));
    }

    /// <summary>
    /// Dangerous terms cannot reach the acoustics. The two whose phonetic keys are
    /// ordinary Russian words are also exact-only.
    /// </summary>
    [Fact]
    public void TestEverySpellingOfAFlaggedTermIsFlagged()
    {
        foreach (DictionaryReplacement term in StarterDictionary.Developer
            .Where(term => new[] { "deploy", "Sentry", "commit" }.Contains(term.Written)))
        {
            Assert.True(term.NoAcousticBoost, $"{term.Spoken} → {term.Written}");
            if (new[] { "Sentry", "commit" }.Contains(term.Written))
            {
                Assert.False(term.AllowsPhoneticMatching, $"{term.Spoken} → {term.Written}");
            }
        }
    }

    [Fact]
    public void TestOrdinaryTermsAreStillBoosted()
    {
        foreach (string written in new[] { "Postgres", "Kubernetes", "code review" })
        {
            Assert.True(
                StarterDictionary.Developer.Any(replacement =>
                    replacement.Written == written && !replacement.NoAcousticBoost),
                $"{written} must retain at least one measured acoustic alias");
        }
    }

    /// <summary>
    /// Dictionaries written before the sign appeared are read as "boosts" -
    /// it used to be the list, not the record, that decided this.
    /// </summary>
    [Fact]
    public void TestOldDictionariesDecodeWithoutTheFlag()
    {
        string json =
            $"{{\"id\":\"{Guid.NewGuid():D}\",\"spoken\":\"графана\",\"written\":\"Grafana\"}}";
        DictionaryReplacement decoded =
            JsonSerializer.Deserialize<DictionaryReplacement>(Encoding.UTF8.GetBytes(json))!;
        Assert.False(decoded.NoAcousticBoost);
        Assert.True(decoded.Inflects);
        Assert.True(decoded.AllowsPhoneticMatching);
    }

    [Fact]
    public void TestFlagSurvivesRoundTrip()
    {
        var original = new DictionaryReplacement(
            spoken: "касса",
            written: "Kassa",
            inflects: false,
            noAcousticBoost: true,
            allowsPhoneticMatching: false);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(original);
        DictionaryReplacement restored = JsonSerializer.Deserialize<DictionaryReplacement>(data)!;
        Assert.Equal(original, restored);
    }

    [Fact]
    public void TestMeasuredDecoderRepairsAreExactOnly()
    {
        DictionaryReplacement repair = Assert.Single(
            StarterDictionary.Developer,
            replacement => replacement.Spoken == "коутривью");
        Assert.False(repair.AllowsPhoneticMatching);
        Assert.Equal(
            "Сделай code review",
            DictionaryReplacements.Apply([repair], "Сделай коутривью"));
        Assert.Equal(
            "Сделай каутревью",
            DictionaryReplacements.Apply([repair], "Сделай каутревью"));
    }
}
