using System.Collections.ObjectModel;
using System.Globalization;

using EzStream.Core.Ipc;

namespace EzStream.Tray;

internal static class StatusListRenderer
{
    public static void Render(ListView list, Collection<SourceStatus> sources)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var source in sources)
                list.Items.Add(CreateItem(source));
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private static ListViewItem CreateItem(SourceStatus source)
    {
        var item = new ListViewItem(source.Path);
        item.SubItems.Add(source.State);
        item.SubItems.Add(source.CurrentFile == null ? "-" : Path.GetFileName(source.CurrentFile));
        item.SubItems.Add(source.SegmentStartedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-");
        item.SubItems.Add(FormatBytes(source.RecordedBytes));
        item.SubItems.Add(source.LastError ?? string.Empty);
        item.ForeColor = StateColor(source.State);
        return item;
    }

    private static Color StateColor(string state) => state switch
    {
        "PLAYING" => Color.ForestGreen,
        "STOPPED" => Color.Gray,
        "INIT" => Color.DarkOrange,
        _ => Color.Black,
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0";

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
