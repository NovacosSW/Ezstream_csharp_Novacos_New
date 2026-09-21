using System.Globalization;

using EzStream.Core.Config;

namespace EzStream.Tray;

internal static class SettingsConfigMapper
{
    public static void PopulateSources(DataGridView grid, RecorderConfig config)
    {
        grid.Rows.Clear();
        foreach (var source in config.Sources)
            grid.Rows.Add(source.Url?.OriginalString ?? string.Empty, source.Path, source.FilePrefix);
    }

    public static RecorderConfig Read(
        RecorderConfig loaded,
        decimal minutes,
        decimal retentionDays,
        decimal logRetentionDays,
        string documentRoot,
        DataGridView grid)
    {
        var config = loaded.Clone();
        config.SegmentMinutes = (int)minutes;
        config.DisuseTermDays = (int)retentionDays;
        config.LogRetentionDays = (int)logRetentionDays;
        config.DocumentRoot = string.IsNullOrWhiteSpace(documentRoot)
            ? RecorderConfig.DefaultDocumentRoot()
            : documentRoot.Trim();
        config.Sources.Clear();

        foreach (DataGridViewRow row in grid.Rows)
            AddSource(config, row);

        return config;
    }

    private static void AddSource(RecorderConfig config, DataGridViewRow row)
    {
        if (row.IsNewRow)
            return;

        var url = CellText(row, "url");
        if (string.IsNullOrWhiteSpace(url))
            return;

        config.Sources.Add(new SourceConfig
        {
            Url = new Uri(url, UriKind.Absolute),
            Path = CellText(row, "path"),
            FilePrefix = CellText(row, "prefix") is { Length: > 0 } prefix ? prefix : "video",
        });
    }

    private static string CellText(DataGridViewRow row, string columnName)
        => Convert.ToString(row.Cells[columnName].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
}
