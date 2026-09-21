using System.Threading;

namespace EzStream.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 중복 실행 방지
        using var mutex = new Mutex(true, "EzStream.Tray.SingleInstance", out bool createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        using var trayApp = new TrayApp();
        Application.Run(trayApp);
    }
}
