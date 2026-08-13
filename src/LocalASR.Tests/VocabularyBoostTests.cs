using DictationCore;

namespace LocalASR.Tests;

/// <summary>
/// Rules for preparing a list of terms for the recognition hint.
///
/// No model is needed here: this is pure normalization. The mistakes in it are quiet - a
/// duplicate or an empty line does not damage recognition, it only spoils the quality of the
/// hint - so the rules are pinned by tests.
/// </summary>
public class VocabularyBoostTests
{
    [Fact]
    public void Scenario001()
    {
        var boost = new VocabularyBoost(
        [
            new VocabularyBoost.Term("deploy"),
            new VocabularyBoost.Term(""),
            new VocabularyBoost.Term("   "),
            new VocabularyBoost.Term("GitHub"),
        ]);

        Assert.Equal(["deploy", "GitHub"], boost.Terms.Select(term => term.Text));
    }

    [Fact]
    public void Scenario002()
    {
        // One term with two spellings is one term: the spotting model searches the acoustic
        // body, not the case. The first spelling wins because the person entered it first.
        var boost = new VocabularyBoost(
        [
            new VocabularyBoost.Term("GitHub"),
            new VocabularyBoost.Term("github"),
            new VocabularyBoost.Term("Deploy"),
            new VocabularyBoost.Term("deploy"),
        ]);

        Assert.Equal(["GitHub", "Deploy"], boost.Terms.Select(term => term.Text));
    }

    [Fact]
    public void Scenario003()
    {
        var boost = new VocabularyBoost([new VocabularyBoost.Term("  pull request  ")]);

        Assert.Equal(["pull request"], boost.Terms.Select(term => term.Text));
    }

    [Fact]
    public void Scenario004()
    {
        var boost = new VocabularyBoost([]);

        Assert.Empty(boost.Terms);
        Assert.True(boost.IsEmpty);
    }

    /// <summary>
    /// A ready-made set for the developer's dictionary is assembled from the replacement
    /// dictionary, but three terms never fall into it: the hinter sees the similarity through
    /// alphabets, and "deploy" caught "тёплой", "Sentry" caught "в центре", "commit" caught
    /// "комету". The measurement is in docs/benchmarks.md; the test holds the outcome.
    /// </summary>
    [Fact]
    public void Scenario005()
    {
        VocabularyBoost boost = VocabularyBoost.DeveloperDefault();

        var texts = boost.Terms.Select(term => term.Text).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("deploy", texts);
        Assert.DoesNotContain("Sentry", texts);
        Assert.DoesNotContain("commit", texts);

        // The rest of the dictionary is included in its entirety, with the Cyrillic aliases:
        // without them the bridge from sound to Latin does not work.
        Assert.Contains("Kubernetes", texts);
        Assert.Contains("pull request", texts);
        // Kubernetes has two Cyrillic spellings - an inflected one and a mangled recognition;
        // both serve as bridges and both must go into the set.
        VocabularyBoost.Term? kubernetes = boost.Terms.FirstOrDefault(term => term.Text == "Kubernetes");
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "кубернетес", "кубернетис" },
            (kubernetes?.Aliases ?? []).ToHashSet(StringComparer.Ordinal));
        Assert.DoesNotContain(
            "коутривью",
            boost.Terms.FirstOrDefault(term => term.Text == "code review")?.Aliases ?? []);
    }

    /// <summary>
    /// User replacements - including those learned from edits - go into the acoustic set with
    /// the same fuses as the starter ones.
    /// </summary>
    [Fact]
    public void Scenario006()
    {
        VocabularyBoost boost = VocabularyBoost.WithUserReplacements(
        [
            // a duplicate of the starter one - does not double
            new DictionaryReplacement(spoken: "поуст герз", written: "Postgres"),
            // a new term - is learned
            new DictionaryReplacement(spoken: "графана", written: "Grafana"),
            // dangerous - the starter set holds a flag on it, and the person's record does
            // not cancel that
            new DictionaryReplacement(spoken: "деплой", written: "Deploy"),
            // without Latin it is not a term
            new DictionaryReplacement(spoken: "как слышится", written: "как пишется"),
            // a dangerous term of one's own: previously a person could not exclude anything
            new DictionaryReplacement(spoken: "касса", written: "Kassa", noAcousticBoost: true),
        ]);

        var texts = boost.Terms.Select(term => term.Text).ToList();
        Assert.Contains("Grafana", texts);
        Assert.DoesNotContain(texts, text => text.ToLowerInvariant() == "deploy");
        Assert.DoesNotContain("как пишется", texts);
        Assert.DoesNotContain("Kassa", texts);
        Assert.Single(texts, text => text.ToLowerInvariant() == "postgres");
        VocabularyBoost.Term? postgres = boost.Terms.FirstOrDefault(term => term.Text == "Postgres");
        Assert.Contains("поуст герз", postgres?.Aliases ?? []);
        VocabularyBoost.Term? grafana = boost.Terms.FirstOrDefault(term => term.Text == "Grafana");
        Assert.Equal(["графана"], grafana?.Aliases ?? []);
    }

    /// <summary>
    /// Cyrillic spellings are the bridge between sound and a Latin term: the model's text is
    /// Cyrillic, and without such aliases there is no candidate for replacement at all.
    /// </summary>
    [Fact]
    public void Scenario007()
    {
        var boost = new VocabularyBoost(
        [
            new VocabularyBoost.Term("deploy", [" деплой ", "", "ДЕПЛОЙ", "диплой", "deploy"]),
        ]);

        Assert.Equal(["деплой", "диплой"], boost.Terms[0].Aliases);
    }

    [Fact]
    public void Scenario008()
    {
        var boost = new VocabularyBoost([new VocabularyBoost.Term("GitHub")]);

        Assert.Empty(boost.Terms[0].Aliases);
    }

    [Fact]
    public void Scenario009()
    {
        VocabularyBoost boost = VocabularyBoost.WithUserReplacements(
        [
            new DictionaryReplacement(spoken: "постгрес", written: "Postgres", noAcousticBoost: true),
        ]);

        Assert.DoesNotContain(
            boost.Terms,
            term => term.Text.ToLowerInvariant() == "postgres");
    }

    [Fact]
    public void Scenario010()
    {
        const string First = "поуст герз";
        const string Second = "пост ги";
        VocabularyBoost boost = VocabularyBoost.WithUserReplacements(
        [
            new DictionaryReplacement(spoken: First, written: "Postgres"),
            new DictionaryReplacement(spoken: First, written: "POSTGRES"),
            new DictionaryReplacement(spoken: Second, written: "Postgres"),
        ]);

        VocabularyBoost.Term postgres = Assert.Single(
            boost.Terms, term => term.Text == "Postgres");
        Assert.Equal(
            [First, Second],
            postgres.Aliases.Where(alias => alias == First || alias == Second));
        Assert.Single(boost.Terms, term => term.Text.ToLowerInvariant() == "postgres");
    }

    [Fact]
    public void PersonalAliasOverridesDifferentBuiltInCanonical()
    {
        const string Spoken = "фича флаг";
        VocabularyBoost boost = VocabularyBoost.WithUserReplacements(
        [
            new DictionaryReplacement(spoken: Spoken, written: "launch switch"),
        ]);

        VocabularyBoost.Term personal = Assert.Single(
            boost.Terms, term => term.Text == "launch switch");
        Assert.Contains(Spoken, personal.Aliases);
        Assert.DoesNotContain(
            boost.Terms,
            term => term.Text == "feature flag" && term.Aliases.Contains(Spoken));
        Assert.Equal(
            ["launch switch"],
            boost.Terms.Where(term => term.Aliases.Contains(Spoken)).Select(term => term.Text));

        VocabularyBoost textOnly = VocabularyBoost.WithUserReplacements(
        [
            new DictionaryReplacement(spoken: Spoken, written: "launch switch", noAcousticBoost: true),
        ]);
        Assert.DoesNotContain(textOnly.Terms, term => term.Aliases.Contains(Spoken));
    }
}
