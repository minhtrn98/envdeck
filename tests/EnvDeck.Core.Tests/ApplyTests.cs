using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

public class ApplyTests
{
    [Fact]
    public void Apply_then_unapply_restores_the_original_environment()
    {
        using var t = new TestDeck();
        t.Set("JAVA_HOME", @"C:\old");
        t.Set("Path", @"C:\Windows;C:\tools", kind: ValueKind.ExpandString);
        t.Set("LEGACY", "1");
        t.Save("p",
            Var("JAVA_HOME", @"C:\jdk17"),
            Var("NEW_VAR", "x"),
            Var("LEGACY", null, VarOp.Remove),
            Var("Path", @"%JAVA_HOME%\bin", VarOp.Prepend));

        var result = t.Service.Apply("p");

        Assert.Equal(4, result.Changes.Count);
        Assert.Equal(@"C:\jdk17", t.Get("JAVA_HOME"));
        Assert.Equal("x", t.Get("NEW_VAR"));
        Assert.Null(t.Get("LEGACY"));
        Assert.Equal(@"%JAVA_HOME%\bin;C:\Windows;C:\tools", t.Get("Path"));
        Assert.True(t.Service.IsActive("p"));
        Assert.NotNull(result.BackupId);

        t.Service.Unapply("p");

        Assert.Equal(@"C:\old", t.Get("JAVA_HOME"));
        Assert.Null(t.Get("NEW_VAR"));
        Assert.Equal("1", t.Get("LEGACY"));
        Assert.Equal(@"C:\Windows;C:\tools", t.Get("Path"));
        Assert.Equal(ValueKind.ExpandString, t.Env.Get(EnvScope.User, "Path")!.Kind);
        Assert.False(t.Service.IsActive("p"));
    }

    [Fact]
    public void Prepend_moves_an_existing_entry_to_the_front_and_append_does_not_duplicate()
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\a;C:\b\;C:\c");
        t.Save("p", Var("Path", @"c:\B", VarOp.Prepend), Var("Path", @"C:\a;C:\d", VarOp.Append));

        var plan = t.Service.Plan(t.Service.Profiles.Get("p")).Single();

        Assert.Equal(@"c:\B;C:\a;C:\c;C:\d", plan.After);
        Assert.Equal([@"C:\d"], plan.AddedEntries);
    }

    [Fact]
    public void Profiles_can_be_stacked_and_unapplied_in_reverse_order()
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows");
        t.Save(new Profile { Name = "java17", Variables = [Var("JAVA_HOME", @"C:\jdk17"), Var("Path", @"C:\jdk17\bin", VarOp.Prepend)] });
        t.Save(new Profile { Name = "java21", Variables = [Var("JAVA_HOME", @"C:\jdk21"), Var("Path", @"C:\jdk21\bin", VarOp.Prepend)] });

        t.Service.Apply("java17");
        var result = t.Service.Apply("java21");

        Assert.Empty(result.TurnedOff);
        Assert.Equal(@"C:\jdk21", t.Get("JAVA_HOME"));
        Assert.Equal(@"C:\jdk21\bin;C:\jdk17\bin;C:\Windows", t.Get("Path"));
        Assert.Equal(["java17", "java21"], t.Service.State.Load().Applied.Select(a => a.Profile));

        t.Service.Unapply("java21");
        Assert.Equal(@"C:\jdk17", t.Get("JAVA_HOME"));
        Assert.Equal(@"C:\jdk17\bin;C:\Windows", t.Get("Path"));
        t.Service.Unapply("java17");
        Assert.Null(t.Get("JAVA_HOME"));
        Assert.Equal(@"C:\Windows", t.Get("Path"));
    }

    [Fact]
    public void Turning_off_an_earlier_stacked_profile_keeps_the_later_one_working()
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows");
        t.Save("a", Var("Path", @"C:\a", VarOp.Prepend), Var("FOO", "a"));
        t.Save("b", Var("Path", @"C:\b", VarOp.Prepend), Var("FOO", "b"));
        t.Service.Apply("a");
        t.Service.Apply("b");
        Assert.Equal(@"C:\b;C:\a;C:\Windows", t.Get("Path"));

        t.Service.Unapply("a");
        Assert.Equal(@"C:\b;C:\Windows", t.Get("Path"));
        Assert.Equal("b", t.Get("FOO")); // b still owns FOO

        t.Service.Unapply("b");
        Assert.Equal(@"C:\Windows", t.Get("Path"));
        Assert.Null(t.Get("FOO")); // FOO did not exist before a
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "a")]
    public void A_list_entry_two_profiles_ask_for_stays_until_both_are_off(string firstOff, string secondOff)
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows");
        t.Save("a", Var("Path", @"C:\X", VarOp.Prepend));
        t.Save("b", Var("Path", @"C:\X;C:\Y", VarOp.Prepend));
        t.Service.Apply("a");
        t.Service.Apply("b");
        Assert.Equal(@"C:\X;C:\Y;C:\Windows", t.Get("Path"));

        t.Service.Unapply(firstOff);
        Assert.Contains(@"C:\X", PathList.Split(t.Get("Path"))); // the other profile still asks for it
        Assert.Equal(secondOff == "b", PathList.Contains(PathList.Split(t.Get("Path")), @"C:\Y"));

        t.Service.Unapply(secondOff);
        Assert.Equal(@"C:\Windows", t.Get("Path"));
    }

    [Theory]
    [InlineData("a", "m", "b")]
    [InlineData("a", "b", "m")]
    [InlineData("m", "a", "b")]
    [InlineData("b", "m", "a")]
    public void Shared_list_entries_survive_any_turn_off_order_with_a_profile_in_between(string first, string second, string third)
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows");
        t.Save("a", Var("Path", @"C:\X", VarOp.Prepend));
        t.Save("m", Var("Path", @"C:\M", VarOp.Prepend));
        t.Save("b", Var("Path", @"C:\X", VarOp.Prepend));
        foreach (var p in new[] { "a", "m", "b" }) t.Service.Apply(p);

        var wanted = new Dictionary<string, string> { ["a"] = @"C:\X", ["m"] = @"C:\M", ["b"] = @"C:\X" };
        var on = new List<string> { "a", "m", "b" };
        foreach (var p in new[] { first, second, third })
        {
            t.Service.Unapply(p);
            on.Remove(p);
            var entries = PathList.Split(t.Get("Path"));
            foreach (var still in on) Assert.Contains(wanted[still], entries);
            Assert.Equal(on.Count == 0 ? 1 : entries.Count, entries.Count);
            Assert.Contains(@"C:\Windows", entries);
            if (!on.Any(o => wanted[o] == @"C:\X")) Assert.DoesNotContain(@"C:\X", entries);
            if (!on.Contains("m")) Assert.DoesNotContain(@"C:\M", entries);
        }
        Assert.Equal(@"C:\Windows", t.Get("Path"));
    }

    [Fact]
    public void Conflicts_list_values_another_profile_that_is_on_has_set()
    {
        using var t = new TestDeck();
        t.Save(new Profile { Name = "a", Variables = [Var("FOO", "A"), Var("SAME", "1"), Var("Path", @"C:\a", VarOp.Prepend)] });
        t.Save(new Profile { Name = "b", Variables = [Var("FOO", "B"), Var("SAME", "1"), Var("Path", @"C:\b", VarOp.Prepend)] });
        t.Save(new Profile { Name = "j17", Variables = [Var("JAVA_HOME", "17")] });
        t.Save(new Profile { Name = "j21", Variables = [Var("JAVA_HOME", "21")] });
        t.Service.Apply("a");
        t.Service.Apply("j17");

        var conflict = Assert.Single(t.Service.FindConflicts(t.Service.Profiles.Get("b")));
        Assert.Equal(("FOO", "a", "A", "B"), (conflict.Name, conflict.OtherProfile, conflict.OtherValue, conflict.NewValue));
        var jConflict = Assert.Single(t.Service.FindConflicts(t.Service.Profiles.Get("j21")));
        Assert.Equal(("JAVA_HOME", "j17", "17", "21"), (jConflict.Name, jConflict.OtherProfile, jConflict.OtherValue, jConflict.NewValue));
        Assert.Contains(t.Service.Apply("b").Warnings, w => w.Contains("FOO") && w.Contains("'a'"));
    }

    [Fact]
    public void Turning_off_after_an_outside_change_only_removes_own_list_entries_and_keeps_edited_values()
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows");
        t.Save("p", Var("Path", @"C:\jdk\bin", VarOp.Prepend), Var("JAVA_HOME", @"C:\jdk"));
        t.Service.Apply("p");

        // An installer appends to PATH, the user edits JAVA_HOME by hand.
        t.Set("Path", t.Get("Path") + @";C:\installer");
        t.Set("JAVA_HOME", @"D:\custom");

        // Path still contains our entry, so only JAVA_HOME counts as drift.
        Assert.Equal(["User\\JAVA_HOME"], t.Service.Status().Single().Drift);
        var result = t.Service.Unapply("p");

        Assert.Equal(@"C:\Windows;C:\installer", t.Get("Path"));
        Assert.Equal(@"D:\custom", t.Get("JAVA_HOME"));
        Assert.Contains(result.Warnings, w => w.Contains("JAVA_HOME"));
    }

    [Fact]
    public void Machine_scope_without_admin_rights_fails_before_writing_anything()
    {
        using var t = new TestDeck();
        t.Env.MachineWritable = false;
        t.Save("p", Var("A", "1"), Var("B", "2", scope: EnvScope.Machine));

        Assert.Throws<ElevationRequiredException>(() => t.Service.Apply("p"));
        Assert.Null(t.Get("A"));
        Assert.False(t.Service.IsActive("p"));
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"));

        var result = t.Service.Apply("p", dryRun: true);

        Assert.Single(result.Changes);
        Assert.Null(t.Get("A"));
        Assert.False(t.Service.IsActive("p"));
        Assert.Empty(t.Service.Backups.List());
    }

    [Fact]
    public void Reapplying_an_edited_profile_replaces_the_previous_version()
    {
        using var t = new TestDeck();
        t.Set("Path", @"C:\Windows");
        t.Save("p", Var("Path", @"C:\v1", VarOp.Prepend));
        t.Service.Apply("p");
        t.Save("p", Var("Path", @"C:\v2", VarOp.Prepend));

        Assert.True(t.Service.Status().Single().Outdated);
        t.Service.Apply("p");

        Assert.Equal(@"C:\v2;C:\Windows", t.Get("Path"));
        Assert.Equal(2, t.Service.State.Load().Applied.Single().Version);
    }

    [Fact]
    public void Set_without_explicit_kind_uses_expand_string_only_for_variable_references()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", @"%USERPROFILE%\go"), Var("B", @"C:\plain"));
        t.Service.Apply("p");

        Assert.Equal(ValueKind.ExpandString, t.Env.Get(EnvScope.User, "A")!.Kind);
        Assert.Equal(ValueKind.String, t.Env.Get(EnvScope.User, "B")!.Kind);
    }
}
