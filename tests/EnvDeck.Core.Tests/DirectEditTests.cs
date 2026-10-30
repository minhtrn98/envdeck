using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

public class DirectEditTests
{
    static DirectChange Edit(string name, string? value, string? original, bool isNew = false, EnvScope scope = EnvScope.User) =>
        new(scope, name, value, null, isNew, original);

    [Fact]
    public void Creates_updates_and_deletes_unmanaged_variables_after_a_backup()
    {
        using var t = new TestDeck();
        t.Set("OLD", "1");
        t.Set("GONE", "x");

        var preview = t.Service.ApplyDirect([Edit("NEW", "a", null, isNew: true), Edit("OLD", "2", "1"), Edit("GONE", null, "x")], dryRun: true);
        Assert.Equal(3, preview.Changes.Count);
        Assert.Null(t.Get("NEW")); // dry run writes nothing

        var result = t.Service.ApplyDirect([Edit("NEW", "a", null, isNew: true), Edit("OLD", "2", "1"), Edit("GONE", null, "x")]);

        Assert.Equal("a", t.Get("NEW"));
        Assert.Equal("2", t.Get("OLD"));
        Assert.Null(t.Get("GONE"));
        Assert.NotNull(result.BackupId);
        Assert.Equal("x", t.Service.Backups.Get(result.BackupId!).UserVariables!.Single(v => v.Name == "GONE").Value);
    }

    [Fact]
    public void Variables_a_profile_that_is_on_sets_or_removes_are_locked()
    {
        using var t = new TestDeck();
        t.Set("LEGACY", "1");
        t.Save("p", Var("JAVA_HOME", @"C:\jdk"), Var("LEGACY", null, VarOp.Remove));
        t.Service.Apply("p");

        Assert.Equal("p", t.Service.GetOwnership().WholeOwner(EnvScope.User, "java_home"));
        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("JAVA_HOME", @"D:\jdk", @"C:\jdk")]));
        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("LEGACY", "back", null, isNew: true)]));
        Assert.Equal(@"C:\jdk", t.Get("JAVA_HOME"));

        t.Service.Unapply("p");
        t.Service.ApplyDirect([Edit("LEGACY", "2", "1")]); // unlocked once the profile is off
        Assert.Equal("2", t.Get("LEGACY"));
    }

    [Fact]
    public void Path_entries_of_a_profile_that_is_on_stay_but_the_rest_of_path_is_editable()
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows;C:\old");
        t.Save("p", Var("Path", @"C:\jdk\bin", VarOp.Prepend));
        t.Service.Apply("p");
        var path = t.Get("Path"); // C:\jdk\bin;C:\Windows;C:\old

        Assert.Equal("p", t.Service.GetOwnership().EntryOwner(EnvScope.User, "Path", @"c:\JDK\bin\"));
        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("Path", @"C:\Windows;C:\old", path)]));
        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("Path", null, path)]));

        // Drop C:\old, add C:\new, reorder: allowed.
        t.Service.ApplyDirect([Edit("Path", @"C:\new;C:\jdk\bin;C:\Windows", path)]);
        Assert.Equal(@"C:\new;C:\jdk\bin;C:\Windows", t.Get("Path"));

        // Turning the profile off removes only its entry and keeps the direct edits.
        t.Service.Unapply("p");
        Assert.Equal(@"C:\new;C:\Windows", t.Get("Path"));
    }

    [Fact]
    public void Edits_based_on_stale_values_are_refused()
    {
        using var t = new TestDeck();
        t.Set("FOO", "loaded");
        t.Set("FOO", "changed elsewhere");

        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("FOO", "mine", "loaded")]));
        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("FOO", "mine", null, isNew: true)]));
        Assert.Throws<DeckException>(() => t.Service.ApplyDirect([Edit("BAD=NAME", "1", null, isNew: true)]));
        Assert.Equal("changed elsewhere", t.Get("FOO"));
    }

    [Fact]
    public void Machine_edits_need_admin_rights_even_for_a_preview()
    {
        using var t = new TestDeck();
        t.Env.MachineWritable = false;

        Assert.Throws<ElevationRequiredException>(() =>
            t.Service.ApplyDirect([Edit("M", "1", null, isNew: true, scope: EnvScope.Machine)], dryRun: true));
    }
}
