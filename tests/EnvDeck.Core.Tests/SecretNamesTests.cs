using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

public class SecretNamesTests
{
    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("DEV_REDIS_PW")]
    [InlineData("Redis__Password")]
    [InlineData("GoogleMapsApiKey")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("ConnectionStrings__Main")]
    [InlineData("DefaultKey")]
    [InlineData("api-token")]
    public void Names_that_suggest_a_secret_are_masked(string name) => Assert.True(SecretNames.Looks(name), name);

    [Theory]
    [InlineData("Path")]
    [InlineData("JAVA_HOME")]
    [InlineData("DEV_DB_IP")]
    [InlineData("KEYBOARD_LAYOUT")]
    [InlineData("COMPASS_DIR")]
    [InlineData("SSH_KEY_PATH")]      // points at a secret, is not one
    [InlineData("TOKEN_FILE")]
    [InlineData("PASSWORD_POLICY_URL")]
    [InlineData("FeatureKeyboard")]
    public void Ordinary_names_are_not(string name) => Assert.False(SecretNames.Looks(name), name);

    [Fact]
    public void The_users_choice_wins_over_the_guess_and_survives_a_restart()
    {
        using var t = new TestDeck();

        Assert.False(t.Service.IsSecret("MY_SETTING"));
        t.Service.SetSecret("MY_SETTING", true);
        Assert.True(t.Service.IsSecret("my_setting")); // case-insensitive

        Assert.True(t.Service.IsSecret("DB_PASSWORD"));
        t.Service.SetSecret("DB_PASSWORD", false);
        Assert.False(t.Service.IsSecret("DB_PASSWORD"));

        var again = t.Restart();
        Assert.True(again.IsSecret("MY_SETTING"));
        Assert.False(again.IsSecret("DB_PASSWORD"));
    }

    [Fact]
    public void Going_back_to_what_the_name_suggests_stores_nothing()
    {
        using var t = new TestDeck();
        t.Service.SetSecret("DB_PASSWORD", false);
        t.Service.SetSecret("DB_PASSWORD", true); // the guess again
        t.Service.SetSecret("PLAIN_THING", true);
        t.Service.SetSecret("PLAIN_THING", false);

        Assert.Empty(t.Service.Settings.SecretNames);
        Assert.Empty(t.Service.Settings.PlainNames);
    }
}
