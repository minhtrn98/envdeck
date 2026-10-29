using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

public class FeatureTests
{
    [Fact]
    public void Restore_with_prune_returns_to_the_backup_and_its_on_state()
    {
        using var t = new TestDeck();
        t.Set("KEEP", "1");
        t.Set("Path", @"C:\Windows");
        var backup = t.Service.Backup([EnvScope.User, EnvScope.Machine], "clean");

        t.Save("p", Var("Path", @"C:\x", VarOp.Prepend));
        t.Service.Apply("p");
        t.Set("KEEP", "changed");
        t.Set("EXTRA", "1");

        var preview = t.Service.Restore(backup.Id, null, prune: true, dryRun: true);
        Assert.Equal(3, preview.Changes.Count);
        Assert.Equal("changed", t.Get("KEEP"));

        t.Service.Restore(backup.Id, null, prune: true);

        Assert.Equal("1", t.Get("KEEP"));
        Assert.Null(t.Get("EXTRA"));
        Assert.Equal(@"C:\Windows", t.Get("Path"));
        Assert.False(t.Service.IsActive("p"));
        Assert.Equal($"before restore {backup.Id}", t.Service.Backups.Get("latest").Reason);
    }

    [Fact]
    public void Automatic_backups_are_pruned_but_manual_ones_are_kept()
    {
        using var t = new TestDeck(new DeckSettings { AutoBackupLimit = 2 });
        t.Service.Backup([EnvScope.User], "manual");
        t.Save("p", Var("A", "1"));
        for (var i = 0; i < 3; i++)
        {
            t.Service.Apply("p");
            t.Service.Unapply("p");
        }

        var backups = t.Service.Backups.List();
        Assert.Equal(2, backups.Count(b => b.Auto));
        Assert.Single(backups, b => !b.Auto);
    }

    [Fact]
    public void Diff_reports_list_entries_and_versions()
    {
        using var t = new TestDeck();
        t.Save("p", Var("A", "1"), Var("B", "x"));
        t.Save("p", Var("A", "2"), Var("C", "y"));
        t.Set("Path", @"C:\a;C:\b");
        var backup = t.Service.Backup([EnvScope.User], null);
        t.Set("Path", @"C:\b;C:\c");

        var (_, _, versions) = t.Service.Diff("p@1", "p");
        var (_, _, env) = t.Service.Diff($"backup:{backup.Id}", "env:user");

        Assert.Equal(
            [(DiffKind.Changed, "A"), (DiffKind.Removed, "B"), (DiffKind.Added, "C")],
            versions.Select(r => (r.Kind, r.Name)));
        var path = Assert.Single(env);
        Assert.Equal([@"C:\a"], path.OnlyLeft);
        Assert.Equal([@"C:\c"], path.OnlyRight);
    }

    [Fact]
    public void Search_matches_names_and_values_in_profiles_and_environment()
    {
        using var t = new TestDeck();
        t.Save("p", Var("JAVA_HOME", @"C:\jdk"), Var("GOPATH", @"C:\go"));
        t.Set("MAVEN_HOME", @"C:\jdk-tools\maven");

        var both = t.Service.Search("jdk", new SearchOptions());
        var keysOnly = t.Service.Search("^go", new SearchOptions { Regex = true, Values = false });

        Assert.Equal(["profile:p/JAVA_HOME", "env:user/MAVEN_HOME"], both.Select(h => $"{h.Source}/{h.Name}"));
        Assert.Equal("GOPATH", Assert.Single(keysOnly).Name);
        Assert.Throws<DeckException>(() => t.Service.Search("(", new SearchOptions { Regex = true }));
    }

    [Fact]
    public void Export_and_import_move_profiles_with_history_to_a_new_machine()
    {
        using var source = new TestDeck();
        source.Save("p", Var("A", "1"));
        source.Save("p", Var("A", "2"));
        source.Service.Backup([EnvScope.User], "before reinstall");
        var zip = Path.Combine(source.Root, "bundle.zip");
        source.Service.Export(zip, null, withHistory: true, withBackups: true);

        using var target = new TestDeck();
        var result = target.Service.Import(zip, overwrite: false);

        Assert.Equal(["p"], result.Imported);
        Assert.Single(result.Backups);
        Assert.Equal(2, target.Service.Profiles.Get("p").Version);
        Assert.Equal("1", target.Service.Profiles.GetVersion("p", 1).Variables[0].Value);
        Assert.Empty(target.Restart().ExternalEdits);

        var again = target.Service.Import(zip, overwrite: false);
        Assert.Equal(["p"], again.Skipped);
    }

    [Fact]
    public void Capture_copies_variables_and_can_make_them_portable()
    {
        using var t = new TestDeck();
        var home = Environment.GetEnvironmentVariable("USERPROFILE")!;
        t.Set("GOPATH", home + @"\go");

        var (profile, warnings) = t.Service.Capture("go", [(EnvScope.User, "GOPATH"), (EnvScope.User, "MISSING")], portable: true);

        Assert.Single(warnings);
        var v = Assert.Single(profile.Variables);
        Assert.Equal(@"%USERPROFILE%\go", v.Value);
        Assert.Null(v.Kind); // ExpandString is implied by the %VAR% reference
    }

    [Fact]
    public void Portabilize_prefers_the_most_specific_folder()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        var home = Environment.GetEnvironmentVariable("USERPROFILE")!;

        var result = Portability.Portabilize($@"{local}\Programs\x;{home}\bin;{home}x;D:\other");

        Assert.Equal($@"%LOCALAPPDATA%\Programs\x;%USERPROFILE%\bin;{home}x;D:\other", result);
    }

    [Fact]
    public void Shell_script_resolves_references_to_values_set_earlier_in_the_script()
    {
        var profile = new Profile
        {
            Name = "java",
            Version = 1,
            Variables = [Var("JAVA_HOME", @"C:\jdk 17"), Var("Path", @"%JAVA_HOME%\bin", VarOp.Prepend), Var("OLD", null, VarOp.Remove)],
        };

        var pwsh = ShellScript.Render([profile], ShellFormat.Pwsh);
        var cmd = ShellScript.Render([profile], ShellFormat.Cmd);

        Assert.Contains(@"${env:Path} = 'C:\jdk 17\bin;' + ${env:Path}", pwsh);
        Assert.Contains("Remove-Item -LiteralPath 'Env:OLD'", pwsh);
        Assert.Contains(@"set ""Path=C:\jdk 17\bin;%Path%""", cmd);
    }

    [Fact]
    public void Path_list_comparison_ignores_case_quotes_and_trailing_slash()
    {
        Assert.True(PathList.Same(@"C:\Tools\", @"""c:\tools"""));
        Assert.Equal(@"C:\b", PathList.Remove(@"C:\a\;C:\b;;", [@"c:\A"]));
    }
}
