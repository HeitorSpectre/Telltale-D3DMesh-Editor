using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Core.Localization;
using TelltaleD3DMeshEditor.Reinsert;
using TelltaleD3DMeshEditor.UI;

namespace TelltaleD3DMeshEditor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Load the active UI language before any window is built. Uses the saved preference, or the
        // closest match to the Windows UI culture on first run.
        Loc.Initialize(AppPreferences.Load().Language);

        // "--open-archives <paths...>" is used by the elevated relaunch to resume an Open Archive
        // action inside a protected game folder; consume it before the CLI dispatcher sees it.
        var launchArchivePaths = GetLaunchArchivePaths(args);
        if (launchArchivePaths is not null)
        {
            args = [];
        }

        var launchMeshPath = GetLaunchMeshPath(args);
        if (launchMeshPath is null && launchArchivePaths is null && ReinsertCli.TryRun(args))
        {
            return;
        }

        FileAssociationService.RegisterD3DMeshAssociation();
        FileAssociationService.RegisterSkeletonAssociation();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnhandledException(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ErrorLog.Write(ex, "Unhandled process exception");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(launchMeshPath) { PendingArchivePaths = launchArchivePaths });
    }

    private static string[]? GetLaunchArchivePaths(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--open-archives", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var paths = args.Skip(1).Where(File.Exists).Select(Path.GetFullPath).ToArray();
        return paths.Length > 0 ? paths : null;
    }

    private static string? GetLaunchMeshPath(string[] args)
    {
        if (args.Length != 1 ||
            !args[0].EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(args[0]))
        {
            return null;
        }

        return Path.GetFullPath(args[0]);
    }

    private static void ShowUnhandledException(Exception ex, string context)
    {
        var logPath = ErrorLog.Write(ex, context);
        MessageBox.Show(
            Loc.T("msg.unhandled.body", logPath, ex.Message),
            Loc.T("app.title"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
