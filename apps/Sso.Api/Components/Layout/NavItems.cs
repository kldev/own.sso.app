namespace Sso.Api.Components.Layout;

/// <summary>A menu entry. Context = the bounded context (database module) the page belongs to, null for the shell.</summary>
public sealed record NavItem(string Icon, string Path, string Label, string? Context = null);

/// <summary>A group of the menu; Label null = no heading.</summary>
public sealed record NavGroup(string? Label, IReadOnlyList<NavItem> Items);

/// <summary>
/// The menu: one group per area of the business, one entry per bounded context (docs/domain.md). Everyone is an admin
/// for now (ADR 0011), so nothing is hidden by role. A new module adds its group here, paths under admin/.
/// </summary>
public static class NavItems
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new(null, [new("layout-dashboard", "admin", "Dashboard")]),
        new(
            "SSO",
            [
                new("users", "admin/people", "People", "people"),
                new("app-window", "admin/applications", "Applications", "clients"),
                new("history", "admin/activity", "Activity", "activity"),
                new("bot", "admin/claude-code", "Claude Code", "platform"),
            ]
        ),
        new("Marketing", [new("mail", "admin/marketing/mail", "Mail", "marketing")]),
    ];
}
