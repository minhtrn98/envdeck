using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

/// <summary>Profiles written before description, group and tags were removed from the model.</summary>
public class LegacyProfileTests
{
    const string OldFile = """
        {
          "name": "old",
          "description": "JDK 17 for project A",
          "group": "java",
          "tags": ["work"],
          "version": 1,
          "variables": [ { "name": "JAVA_HOME", "value": "C:\\jdk17" } ]
        }
        """;

    static string[] Kept(TestDeck t) =>
        Directory.Exists(Path.Combine(t.Service.Paths.DataDir, "legacy"))
            ? Directory.GetFiles(Path.Combine(t.Service.Paths.DataDir, "legacy"))
            : [];

    [Fact]
    public void An_old_file_still_loads_and_its_metadata_is_kept_before_the_file_is_rewritten()
    {
        using var t = new TestDeck();
        Directory.CreateDirectory(t.Service.Paths.ProfilesDir);
        var file = Path.Combine(t.Service.Paths.ProfilesDir, "old.json");
        File.WriteAllText(file, OldFile);

        Assert.Equal(@"C:\jdk17", t.Service.Profiles.Get("old").Variables.Single().Value); // unknown fields do not break loading

        Assert.Equal(["old"], t.Service.Profiles.SyncExternalEdits());

        var rewritten = File.ReadAllText(file);
        Assert.DoesNotContain("group", rewritten);
        var copy = Assert.Single(Kept(t));
        var text = File.ReadAllText(copy);
        Assert.Contains("JDK 17 for project A", text);
        Assert.Contains("\"group\": \"java\"", text);
        Assert.StartsWith("old-", Path.GetFileName(copy));
    }

    [Fact]
    public void Saving_a_profile_whose_file_has_the_old_fields_keeps_them_too()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"));
        var file = t.Service.Profiles.FileOf("p");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"name\": \"p\"", "\"name\": \"p\", \"description\": \"keep me\""));

        t.Save("p", Var("A", "2"));

        Assert.Contains("keep me", File.ReadAllText(Assert.Single(Kept(t))));
    }

    [Fact]
    public void Ordinary_files_leave_nothing_behind()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"));
        t.Save("p", Var("A", "2"));

        Assert.Empty(Kept(t));
    }
}
