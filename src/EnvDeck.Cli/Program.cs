using System.Text;
using EnvDeck.Cli;
using EnvDeck.Core;

Console.OutputEncoding = Encoding.UTF8;

CliArgs a;
try
{
    a = CliArgs.Parse(args);
}
catch (UsageException ex)
{
    Out.Error(ex.Message);
    return 2;
}

if (a.Flag("version"))
{
    Out.Line($"envdeck {DeckService.ToolVersion}");
    return 0;
}
if (a.Command is null or "help" || a.Flag("help"))
{
    Help.Print(a.Command is null or "help" ? a.ArgOrNull(0) : a.Command);
    return 0;
}

try
{
    var svc = DeckService.CreateDefault();
    foreach (var name in svc.ExternalEdits) Out.Dim($"'{name}' was edited by hand; saved as a new version.");
    if (svc.Env.IsSandbox) Out.Dim("[sandbox mode: HKCU\\Software\\EnvDeck\\Sandbox]");

    return a.Command switch
    {
        "list" or "ls" => Commands.List(svc, a),
        "show" => Commands.Show(svc, a),
        "new" => Commands.New(svc, a),
        "set" => Commands.Set(svc, a),
        "unset" => Commands.Unset(svc, a),
        "capture" => Commands.Capture(svc, a),
        "rename" => Commands.Rename(svc, a),
        "delete" or "rm" => Commands.Delete(svc, a),
        "edit" => Commands.Edit(svc, a),
        "apply" or "on" => Commands.Apply(svc, a),
        "unapply" or "off" => Commands.Unapply(svc, a),
        "status" => Commands.Status(svc, a),
        "history" => Commands.History(svc, a),
        "rollback" => Commands.Rollback(svc, a),
        "backup" => Commands.Backup(svc, a),
        "backups" => Commands.Backups(svc, a),
        "restore" => Commands.Restore(svc, a),
        "export" => Commands.Export(svc, a),
        "import" => Commands.Import(svc, a),
        "search" or "find" => Commands.Search(svc, a),
        "diff" or "compare" => Commands.Diff(svc, a),
        "shell" => Commands.Shell(svc, a),
        "env" => Commands.Env(svc, a),
        "doctor" => Commands.Doctor(svc, a),
        "config" => Commands.Config(svc, a),
        "ui" => Commands.Ui(a),
        _ => throw new UsageException($"Unknown command '{a.Command}'. Run 'envdeck help'."),
    };
}
catch (UsageException ex)
{
    Out.Error(ex.Message);
    return 2;
}
catch (ElevationRequiredException ex)
{
    Out.Error(ex.Message);
    Out.Dim("Run it from an elevated terminal, e.g. 'sudo envdeck ...' (Windows 11 sudo) or a terminal opened as administrator.");
    return 3;
}
catch (Exception ex) when (ex is DeckException or IOException or UnauthorizedAccessException or InvalidDataException)
{
    Out.Error(ex.Message);
    return 1;
}
