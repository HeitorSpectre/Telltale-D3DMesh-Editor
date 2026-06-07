using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Reinsert;
using TelltaleD3DMeshEditor.UI;

namespace TelltaleD3DMeshEditor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (ReinsertCli.TryRun(args))
        {
            return;
        }

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
        Application.Run(new MainForm());
    }

    private static void ShowUnhandledException(Exception ex, string context)
    {
        var logPath = ErrorLog.Write(ex, context);
        MessageBox.Show(
            $"The tool hit an unexpected error and wrote a log:\n{logPath}\n\n{ex.Message}",
            "Telltale D3DMesh Editor",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
