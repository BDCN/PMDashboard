using System.Globalization;
using System.Reflection;

namespace PMDashboard.Services;

/// <summary>
/// The running build's version (from &lt;Version&gt; in PMDashboard.csproj) and build time.
/// Bump the version for every build that gets installed anywhere — publish.ps1 does it.
/// </summary>
public static class AppVersion
{
    private static readonly Assembly _asm = typeof(AppVersion).Assembly;

    /// <summary>Major.Minor.Patch, e.g. 1.2.3.</summary>
    public static Version Version { get; } = Parse(
        _asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? _asm.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>"1.2.3" — what people should read off the screen and quote back.</summary>
    public static string Number => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

    public static DateTime? BuildDateUtc { get; } =
        _asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDateUtc")?.Value is { } s
        && DateTime.TryParse(s, CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    /// <summary>"v1.2.3"</summary>
    public static string Short => "v" + Number;

    /// <summary>"v1.2.3 (built 2026-10-05 12:34)" — build time shown in local time.</summary>
    public static string Full => BuildDateUtc is { } d
        ? $"{Short} (built {d.ToLocalTime():yyyy-MM-dd HH:mm})"
        : Short;

    /// <summary>Parses "1.2.3", tolerating a "+metadata"/"-suffix" tail the SDK may append.</summary>
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var core = s.Split('+', '-', ' ')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }
}
