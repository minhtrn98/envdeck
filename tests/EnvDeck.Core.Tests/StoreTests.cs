using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

public class StoreTests
{
    [Fact]
    public void Save_creates_a_version_only_when_content_changes()
    {
        using var t = new TestDeck();
        var v1 = t.Save("p", Var("A", "1"));
        var (same, changed) = t.Service.Profiles.Save(v1.Clone());
        var v2 = t.Save("p", Var("A", "2"));

        Assert.Equal(1, v1.Version);
        Assert.False(changed);
        Assert.Equal(1, same.Version);
        Assert.Equal(2, v2.Version);
        Assert.Equal([2, 1], t.Service.Profiles.History("p").Select(h => h.Version));
        Assert.Equal("1", t.Service.Profiles.GetRef("p@1").Variables[0].Value);
    }

    [Fact]
    public void Rollback_saves_the_old_content_as_a_new_version()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"));
        t.Save("p", Var("A", "2"));

        var (rolled, changed) = t.Service.Profiles.Rollback("p", 1);

        Assert.True(changed);
        Assert.Equal(3, rolled.Version);
        Assert.Equal("1", t.Service.Profiles.Get("p").Variables[0].Value);
    }

    [Fact]
    public void History_is_pruned_to_the_limit()
    {
        using var t = new TestDeck(new DeckSettings { HistoryLimit = 2 });
        for (var i = 0; i < 5; i++) t.Save("p", Var("A", i.ToString()));

        Assert.Equal([5, 4], t.Service.Profiles.History("p").Select(h => h.Version));
    }

    [Fact]
    public void Hand_edited_json_becomes_a_new_version_on_next_start()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"));
        var file = t.Service.Profiles.FileOf("p");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"1\"", "\"edited\""));

        var service = t.Restart();

        Assert.Equal(["p"], service.ExternalEdits);
        Assert.Equal(2, service.Profiles.Get("p").Version);
        Assert.Equal("1", service.Profiles.GetVersion("p", 1).Variables[0].Value);
        Assert.Empty(t.Restart().ExternalEdits);
    }

    [Fact]
    public void Json_is_lenient_on_read_and_compact_on_write()
    {
        using var t = new TestDeck();
        Directory.CreateDirectory(t.Paths.ProfilesDir);
        File.WriteAllText(Path.Combine(t.Paths.ProfilesDir, "hand.json"), """
            {
              // comments and trailing commas are fine
              "Name": "hand",
              "variables": [
                { "name": "Path", "value": "C:\\x", "scope": "user", "op": "prepend", },
                { "name": "M", "value": "1", "scope": "Machine" },
              ],
            }
            """);

        var p = t.Restart().Profiles.Get("hand");

        Assert.Equal(VarOp.Prepend, p.Variables[0].Op);
        Assert.Equal(EnvScope.Machine, p.Variables[1].Scope);
        var json = File.ReadAllText(t.Service.Profiles.FileOf("hand"));
        Assert.Contains("\"op\": \"Prepend\"", json);
        Assert.DoesNotContain("\"scope\": \"User\"", json); // defaults are omitted
        Assert.Contains("\"$schema\": \"../profile.schema.json\"", json);
        Assert.True(File.Exists(t.Paths.SchemaFile));
    }

    [Theory]
    [InlineData("java17", true)]
    [InlineData("my.project_A-1", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    [InlineData("a@1", false)]
    [InlineData("..", false)]
    [InlineData(@"..\x", false)]
    [InlineData("con", false)]
    public void Profile_names_are_file_safe(string name, bool valid) => Assert.Equal(valid, ProfileStore.IsValidName(name));

    [Fact]
    public void Validator_rejects_set_combined_with_prepend()
    {
        var errors = ProfileValidator.Validate(new Profile
        {
            Name = "p",
            Variables = [Var("Path", "x"), Var("PATH", "y", VarOp.Prepend), Var("B", null)],
        });

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Rename_moves_history_and_on_state()
    {
        using var t = new TestDeck();
        t.Save("old", Var("A", "1"));
        t.Save("old", Var("A", "2"));
        t.Service.Apply("old");

        t.Service.RenameProfile("old", "new");

        Assert.False(t.Service.Profiles.Exists("old"));
        Assert.Equal(3, t.Service.Profiles.History("new").Count);
        Assert.True(t.Service.IsActive("new"));
        t.Service.Unapply("new");
        Assert.Null(t.Get("A"));
    }

    [Fact]
    public void Deleting_a_profile_that_is_on_is_refused()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"));
        t.Service.Apply("p");

        Assert.Throws<DeckException>(() => t.Service.DeleteProfile("p", purgeHistory: true));
    }
}
