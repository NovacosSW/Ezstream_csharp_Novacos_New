using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EzStream.Tray;

/// <summary>외부 .ico 자산 없이 런타임에 트레이 아이콘을 생성한다.</summary>
internal static class IconFactory
{
    public static Icon CreateRecIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // 카메라 몸통
            using var body = new SolidBrush(Color.FromArgb(40, 44, 52));
            g.FillRoundedRectangle(body, new Rectangle(3, 9, 20, 14), 3);
            // 렌즈
            using var lens = new SolidBrush(Color.FromArgb(120, 170, 255));
            g.FillEllipse(lens, 7, 12, 8, 8);
            // 녹화 표시등
            using var dot = new SolidBrush(active ? Color.FromArgb(230, 60, 60) : Color.Gray);
            g.FillEllipse(dot, 22, 6, 8, 8);
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}

file static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);
}
