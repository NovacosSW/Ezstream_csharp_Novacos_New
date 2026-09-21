namespace EzStream.CoverageTool;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var mainForm = new MainForm();
        Application.Run(mainForm);
    }
}
