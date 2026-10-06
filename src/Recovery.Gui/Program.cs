namespace Recovery.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Theme.InitDpi();

        // A recovery tool must not vanish mid-task: report unexpected errors and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportError(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        int pageArg = Array.IndexOf(args, "--page");
        string? page = pageArg >= 0 && pageArg + 1 < args.Length ? args[pageArg + 1] : null;
        Application.Run(new MainForm(page));
    }

    private static void ReportError(Exception? ex)
    {
        if (ex is null)
            return;
        var answer = MessageBox.Show(
            $"Something went wrong:\n\n{ex.Message}\n\nThe app will keep running. Copy the technical details to the clipboard?",
            "Unexpected error", MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            try { Clipboard.SetText(ex.ToString()); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }
    }
}
