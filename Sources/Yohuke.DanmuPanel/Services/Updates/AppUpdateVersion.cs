namespace Yohuke.DanmuPanel.Services.Updates;

internal static class AppUpdateVersion
{
    internal static Version Current { get; } = typeof(AppUpdateVersion).Assembly.GetName().Version!;

    internal static Version Normalize(Version version)
    {
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    internal static Version ParseTag(string tagName)
    {
        var text = tagName.StartsWith('v') || tagName.StartsWith('V') ? tagName[1..] : tagName;
        var components = text.Split('.');
        if (components.Length is < 2 or > 4 ||
            components.Any(component => component.Length == 0 || !component.All(char.IsAsciiDigit)) ||
            !Version.TryParse(text, out var version))
        {
            throw new InvalidDataException("The GitHub release tag must contain two to four numeric version components.");
        }

        return Normalize(version);
    }
}
