namespace EnvDeck.Core.Tests;

public class NameGroupsTests
{
    static string[] Group(params string[] names) => NameGroups.Group(names, n => n).ToArray();

    [Fact]
    public void Names_are_split_on_double_underscore_underscore_and_dash()
    {
        Assert.Equal(["redis", "url"], NameGroups.Parts("Redis__Url"));
        Assert.Equal(["acc", "redis"], NameGroups.Parts("acc-redis"));
        Assert.Equal(["dev", "db", "password"], NameGroups.Parts("DEV_DB_PASSWORD"));
        Assert.Equal(["path"], NameGroups.Parts("Path"));
    }

    [Theory]
    [InlineData("redis_url", "redis_pw", 1.0)]          // same first part
    [InlineData("acc_redis", "redis_url", 0.5)]         // shared part, at different positions: the later one limits it
    [InlineData("DEV_DB_USER", "DEV_DB_PASSWORD", 1.5)] // first two parts shared
    [InlineData("Redis__Url", "redis-url", 1.5)]        // separators do not matter
    [InlineData("JAVA_HOME", "Path", 0.0)]
    public void Similarity_weighs_early_parts_more(string a, string b, double expected)
    {
        Assert.Equal(expected, NameGroups.Similarity(a, b), 3);
        Assert.Equal(expected, NameGroups.Similarity(b, a), 3); // symmetric
    }

    [Fact]
    public void Closer_matches_come_first_and_weaker_ones_follow()
    {
        // redis_url pulls in redis_pw and redis_db (1.0) before acc_redis (0.5); JAVA_* is a separate cluster.
        var result = Group("redis_url", "JAVA_HOME", "acc_redis", "redis_pw", "JAVA_OPTS", "redis_db");

        Assert.Equal(["redis_url", "redis_pw", "redis_db", "acc_redis", "JAVA_HOME", "JAVA_OPTS"], result);
    }

    [Fact]
    public void A_name_only_related_through_another_member_joins_the_cluster()
    {
        // c_d is not similar to a_b, but it is to b_c, which is: clusters grow through their members (a_b -> b_c -> c_d).
        Assert.Equal(["a_b", "b_c", "c_d", "z"], Group("a_b", "z", "c_d", "b_c"));
    }

    [Fact]
    public void Unrelated_and_already_grouped_lists_keep_their_order()
    {
        string[] unrelated = ["A", "B", "C"];
        string[] grouped = ["R__x", "R__y", "B", "C"];

        Assert.Equal(unrelated, Group(unrelated));
        Assert.Equal(grouped, Group(grouped));
    }

    [Fact]
    public void Rows_without_a_name_stay_in_line()
    {
        Assert.Equal(["X__1", "X__2", "", "Y"], Group("X__1", "", "X__2", "Y"));
        Assert.Equal(["", "A__1", "A__2"], Group("", "A__1", "A__2"));
    }
}
