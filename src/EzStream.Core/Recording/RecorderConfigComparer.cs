using System.Collections.ObjectModel;

using EzStream.Core.Config;

namespace EzStream.Core.Recording;

internal static class RecorderConfigComparer
{
    public static bool RequiresRecorderRebuild(RecorderConfig current, RecorderConfig previous)
        => !string.Equals(current.DocumentRoot, previous.DocumentRoot, StringComparison.OrdinalIgnoreCase)
            || !SourcesEqual(current.Sources, previous.Sources)
            || !string.Equals(current.FfmpegLogLevel, previous.FfmpegLogLevel, StringComparison.OrdinalIgnoreCase);

    private static bool SourcesEqual(Collection<SourceConfig> first, Collection<SourceConfig> second)
    {
        if (first.Count != second.Count)
            return false;

        for (int index = 0; index < first.Count; index++)
        {
            if (!SourceEquals(first[index], second[index]))
                return false;
        }

        return true;
    }

    private static bool SourceEquals(SourceConfig first, SourceConfig second)
        => first.Url == second.Url
            && string.Equals(first.SafePath, second.SafePath, StringComparison.Ordinal)
            && string.Equals(first.SafePrefix, second.SafePrefix, StringComparison.Ordinal);
}
