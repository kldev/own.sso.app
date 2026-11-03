using System.Globalization;

namespace Sso.Api.Web;

/// <summary>Labels of codes, dates and times as the pages show them (English, one market for now).</summary>
public static class Labels
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>
    /// The wall clock of the operator. Pages render on the server (the container runs in UTC), so local time is never
    /// the machine's: it is this zone. One zone for now (PRODUCT.md, "Not yet": a zone per city).
    /// </summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(
        "Europe/Warsaw"
    );

    private static DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).DateTime;

    public static string Role(string? role) =>
        role switch
        {
            Roles.Admin => "SSO admin",
            Roles.Member => "Member",
            _ => role ?? "",
        };

    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpper(Culture),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpper(Culture),
        };
    }

    public static string FirstName(string? name) =>
        (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>"29 Oct 2026, 14:05" in the operator's zone.</summary>
    public static string DateTime(DateTimeOffset? at) =>
        at is { } value ? Local(value).ToString("d MMM yyyy, HH:mm", Culture) : "";
}
