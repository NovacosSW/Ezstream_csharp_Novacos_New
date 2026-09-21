using System.Globalization;
using System.Resources;

namespace EzStream.Tray;

internal static class UiText
{
    private static readonly ResourceManager ResourceManager = new("EzStream.Tray.UiText", typeof(UiText).Assembly);

    public static string Get(string name) => ResourceManager.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {name}");
}
