using System.Net;

namespace Sso.Tests;

/// <summary>
/// A browser across two in-memory servers: SSO (http://localhost) and an application (http://app.test). It keeps
/// cookies per host and follows redirects by hand, sending each request to the server its host names.
/// </summary>
internal sealed class OidcTestBrowser(HttpMessageHandler sso, HttpMessageHandler app) : IDisposable
{
    public const string AppOrigin = "http://app.test";

    private readonly CookieContainer _cookies = new();
    private readonly HttpMessageInvoker _sso = new(sso);
    private readonly HttpMessageInvoker _app = new(app);

    public void SetSsoCookie(string name, string value) =>
        _cookies.Add(new Uri("http://localhost"), new Cookie(name, value));

    /// <summary>GET the address and follow redirects; returns the last response and every address visited.</summary>
    public async Task<(HttpResponseMessage Response, List<Uri> Visited)> GoAsync(
        string url,
        int maxRedirects = 10
    )
    {
        var visited = new List<Uri>();
        var uri = new Uri(url);
        for (var i = 0; ; i++)
        {
            visited.Add(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (_cookies.GetCookieHeader(uri) is { Length: > 0 } header)
                request.Headers.Add("Cookie", header);
            var invoker = uri.Host == "localhost" ? _sso : _app;
            var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                foreach (var setCookie in setCookies)
                    _cookies.SetCookies(uri, setCookie);
            if (i == maxRedirects)
                return (response, visited);
            if (await FormPostAsync(response) is { } posted)
            {
                // response_mode=form_post: the page posts its hidden fields to the application.
                visited.Add(posted.Action);
                using var post = new HttpRequestMessage(HttpMethod.Post, posted.Action);
                post.Content = new FormUrlEncodedContent(posted.Fields);
                if (_cookies.GetCookieHeader(posted.Action) is { Length: > 0 } postCookies)
                    post.Headers.Add("Cookie", postCookies);
                response = await (posted.Action.Host == "localhost" ? _sso : _app).SendAsync(
                    post,
                    TestContext.Current.CancellationToken
                );
                if (response.Headers.TryGetValues("Set-Cookie", out var postSet))
                    foreach (var setCookie in postSet)
                        _cookies.SetCookies(posted.Action, setCookie);
                uri = posted.Action;
            }
            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther))
                return (response, visited);
            var location = response.Headers.Location!;
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static async Task<(Uri Action, Dictionary<string, string> Fields)?> FormPostAsync(
        HttpResponseMessage response
    )
    {
        if (
            response.StatusCode != HttpStatusCode.OK
            || response.Content.Headers.ContentType?.MediaType != "text/html"
        )
            return null;
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var action = System.Text.RegularExpressions.Regex.Match(
            html,
            "<form method=\"post\" action=\"([^\"]+)\""
        );
        if (!action.Success || !html.Contains("document.forms[0].submit()"))
            return null;
        var fields = System
            .Text.RegularExpressions.Regex.Matches(
                html,
                "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"/>"
            )
            .ToDictionary(
                m => WebUtility.HtmlDecode(m.Groups[1].Value),
                m => WebUtility.HtmlDecode(m.Groups[2].Value)
            );
        return (new Uri(WebUtility.HtmlDecode(action.Groups[1].Value)), fields);
    }

    public void Dispose()
    {
        _sso.Dispose();
        _app.Dispose();
    }
}
