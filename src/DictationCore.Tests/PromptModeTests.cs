namespace DictationCore.Tests;

/// <summary>Mode by recipient application.</summary>
public sealed class PromptModeTests
{
    private static TargetApplication Target(string? bundleIdentifier, string? name = null) =>
        new(BundleIdentifier: bundleIdentifier, ProcessIdentifier: 42, LocalizedName: name);

    // MARK: - Categories

    [Theory]
    [InlineData("com.apple.Terminal")]
    [InlineData("com.googlecode.iterm2")]
    [InlineData("com.mitchellh.ghostty")]
    [InlineData("com.microsoft.VSCode")]
    [InlineData("com.apple.dt.Xcode")]
    [InlineData("dev.zed.Zed")]
    public void TestTerminalsAndEditorsAreVerbatim(string identifier)
    {
        var policy = new PromptModePolicy();
        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target(identifier)));
    }

    [Theory]
    [InlineData("com.tinyspeck.slackmacgap")]
    [InlineData("com.apple.mail")]
    [InlineData("com.apple.Notes")]
    [InlineData("md.obsidian")]
    [InlineData("com.apple.Safari")]
    public void TestChatsMailAndNotesAreProse(string identifier)
    {
        var policy = new PromptModePolicy();
        Assert.Equal(PromptMode.Prose, policy.Mode(Target(identifier)));
    }

    /// <summary>The bundle id case should not decide: the system gives it as is.</summary>
    [Fact]
    public void TestBundleIdentifierIsMatchedCaseInsensitively()
    {
        var policy = new PromptModePolicy();
        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target("COM.APPLE.TERMINAL")));
        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target("com.apple.terminal")));
    }

    // MARK: - Default

    /// <summary>
    /// <b>Unfamiliar application - verbatim.</b>
    ///
    /// An error in this direction will leave an extra word. An error in the other will quietly
    /// rewrite the command in the terminal, which we simply did not recognize.
    /// </summary>
    [Fact]
    public void TestUnknownApplicationDefaultsToVerbatim()
    {
        var policy = new PromptModePolicy();
        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target("com.example.something")));
        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target(null)));
        Assert.Equal(PromptMode.Verbatim, policy.Mode(null));
    }

    [Fact]
    public void TestUnknownCategoryDefaultIsVerbatim()
    {
        Assert.Equal(PromptMode.Verbatim, ApplicationCategory.Unknown.DefaultMode());
    }

    // MARK: - Precedent

    [Fact]
    public void TestUserOverrideBeatsTheMap()
    {
        var policy = new PromptModePolicy(new Dictionary<string, PromptMode>
        {
            ["com.apple.terminal"] = PromptMode.Prose,
        });

        // The person's choice is more important than our map.
        Assert.Equal(PromptMode.Prose, policy.Mode(Target("com.apple.Terminal")));
    }

    [Fact]
    public void TestUserOverrideCanForceVerbatimInAChat()
    {
        var policy = new PromptModePolicy(new Dictionary<string, PromptMode>
        {
            ["com.tinyspeck.slackmacgap"] = PromptMode.Verbatim,
        });

        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target("com.tinyspeck.slackmacgap")));
    }

    [Fact]
    public void TestOverrideForOneApplicationDoesNotLeakToOthers()
    {
        var policy = new PromptModePolicy(new Dictionary<string, PromptMode>
        {
            ["com.apple.terminal"] = PromptMode.Prose,
        });

        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target("com.googlecode.iterm2")));
    }

    // MARK: - Name as a reserve attribute

    /// <summary>
    /// The name is the second attempt, not the first: the bundle id is stable, and the name is
    /// localized and changes between versions.
    /// </summary>
    [Fact]
    public void TestNameIsOnlyAFallback()
    {
        var policy = new PromptModePolicy();
        Assert.Equal(
            PromptMode.Verbatim,
            policy.Mode(Target("com.unknown.thing", name: "Some Terminal")));
        // The familiar id decides for itself, no matter what is in the name.
        Assert.Equal(PromptMode.Prose, policy.Mode(Target("com.apple.Notes", name: "Terminal")));
    }

    /// <summary>
    /// A terminal gets a real Return for "Send it.", so only a terminal known
    /// by its bundle id is one. A name never is: Apple's Console is a log
    /// viewer, and a web app called "… console" is a web page.
    /// </summary>
    [Fact]
    public void TestANameNeverMakesATerminal()
    {
        foreach (TargetApplication app in new[]
        {
            Target("com.apple.Console", name: "Console"),
            Target("com.apple.Safari.WebApp.6F1D7A2C", name: "Google Cloud console"),
            Target("com.unknown.thing", name: "Some Terminal"),
        })
        {
            Assert.NotEqual(ApplicationCategory.Terminal, PromptModePolicy.Category(app));
            Assert.Equal(PromptMode.Verbatim, new PromptModePolicy().Mode(app));
        }
    }

    /// <summary>"Code" is deliberately not a sign: too many things are called that way.</summary>
    [Fact]
    public void TestGenericWordsInNamesDoNotDecide()
    {
        var policy = new PromptModePolicy();
        Assert.Equal(
            ApplicationCategory.Unknown,
            PromptModePolicy.Category(Target("x.y", name: "Code Notes")));
        Assert.Equal(PromptMode.Verbatim, policy.Mode(Target("x.y", name: "Code Notes")));
    }

    // MARK: - Map

    /// <summary>The bundle ids macOS reports for these apps, not guesses at them.</summary>
    [Fact]
    public void TestMessagesAndBearAreProse()
    {
        TargetApplication messages = Target("com.apple.MobileSMS", name: "Messages");
        TargetApplication bear = Target("net.shinyfrog.bear", name: "Bear");
        Assert.Equal(ApplicationCategory.Messaging, PromptModePolicy.Category(messages));
        Assert.Equal(ApplicationCategory.Notes, PromptModePolicy.Category(bear));
        Assert.Equal(PromptMode.Prose, new PromptModePolicy().Mode(messages));
        Assert.Equal(PromptMode.Prose, new PromptModePolicy().Mode(bear));
    }

    /// <summary>
    /// Port addition: a Linux window class and a Windows process name sit where macOS puts a
    /// bundle id, and the seed knows the common ones on both.
    /// </summary>
    [Theory]
    [InlineData("kitty", ApplicationCategory.Terminal)]
    [InlineData("Alacritty", ApplicationCategory.Terminal)]
    [InlineData("org.wezfurlong.wezterm", ApplicationCategory.Terminal)]
    [InlineData("WindowsTerminal", ApplicationCategory.Terminal)]
    [InlineData("code", ApplicationCategory.CodeEditor)]
    [InlineData("Code", ApplicationCategory.CodeEditor)]
    [InlineData("firefox", ApplicationCategory.Browser)]
    [InlineData("thunderbird", ApplicationCategory.Mail)]
    [InlineData("org.telegram.desktop", ApplicationCategory.Messaging)]
    public void TestLinuxClassesAndWindowsProcessNamesAreKnown(string identifier, ApplicationCategory category)
    {
        Assert.Equal(category, PromptModePolicy.Category(Target(identifier)));
    }

    [Fact]
    public void TestCategoryIsExposedForSettings()
    {
        Assert.Equal(
            ApplicationCategory.Terminal,
            PromptModePolicy.Category(Target("com.apple.Terminal")));
        Assert.Equal(ApplicationCategory.Mail, PromptModePolicy.Category(Target("com.apple.mail")));
        Assert.Equal(
            ApplicationCategory.Unknown,
            PromptModePolicy.Category(Target("com.example.nope")));
    }

    /// <summary>
    /// The map contains only lowercase keys - otherwise the lookup by the lowercased
    /// identifier would miss silently, and the application would simply receive the wrong mode.
    /// </summary>
    [Fact]
    public void TestSeedKeysAreAllLowercased()
    {
        foreach (string key in PromptModePolicy.Seed.Keys)
        {
            Assert.Equal(key.ToLowerInvariant(), key);
        }
    }

    [Fact]
    public void TestEveryCategoryHasAMode()
    {
        foreach (ApplicationCategory category in Enum.GetValues<ApplicationCategory>())
        {
            Assert.Contains(category.DefaultMode(), Enum.GetValues<PromptMode>());
        }
    }
}
