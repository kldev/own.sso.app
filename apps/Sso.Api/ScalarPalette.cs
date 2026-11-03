namespace Sso.Api;

/// <summary>
/// Scalar's dark mode in Great SSO's palette (Styles/app.css, the .dark tokens, as hex). Fonts are the
/// system's: nothing is fetched from a CDN.
/// </summary>
internal static class ScalarPalette
{
    public const string Css = """
        .dark-mode {
          --scalar-background-1: #09090b;
          --scalar-background-2: #18181b;
          --scalar-background-3: #27272a;
          --scalar-background-accent: #134e4a;
          --scalar-color-1: #fafafa;
          --scalar-color-2: #d4d4d8;
          --scalar-color-3: #a1a1aa;
          --scalar-color-accent: #2dd4bf;
          --scalar-border-color: #27272a;
          --scalar-color-green: #6fcf97;
          --scalar-color-red: #f87171;
          --scalar-color-yellow: #e8c06a;
          --scalar-color-blue: #60a5fa;
          --scalar-color-orange: #f2a65a;
          --scalar-color-purple: #b39ddb;
          --scalar-sidebar-background-1: #111113;
          --scalar-sidebar-color-1: #fafafa;
          --scalar-sidebar-color-2: #a1a1aa;
          --scalar-sidebar-border-color: #1f1f23;
          --scalar-sidebar-item-hover-background: #1f2937;
          --scalar-sidebar-item-active-background: #134e4a;
          --scalar-sidebar-color-active: #2dd4bf;
        }
        """;
}
