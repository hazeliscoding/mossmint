using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mossmint.Connectors.Abstractions;

namespace Mossmint.Connectors.SimpleFin;

// SimpleFIN (https://www.simplefin.org/protocol.html): the user makes a setup
// token at a SimpleFIN server such as the Bridge, Mossmint claims it once for
// an access URL, and that URL, credentials and all, is the only secret. Every
// request is HTTPS, nothing follows a redirect, and no message carries the
// credential. See docs/connectors/ADR-simplefin.md.
public sealed class SimpleFinProvider : IConnectorProvider
{
    public static readonly ConnectorId ProviderId = new("simplefin");
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public SimpleFinProvider(TimeProvider clock)
        : this(clock, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = true })
    {
    }

    // For tests: the handler stands in for the network.
    public SimpleFinProvider(TimeProvider clock, HttpMessageHandler handler)
    {
        _clock = clock;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public ConnectorId Id => ProviderId;

    public string DisplayName => "SimpleFIN";

    public string HostOf(string setupToken) => ClaimUrl(setupToken).Host;

    public async Task<string> ClaimAsync(string setupToken, CancellationToken cancellationToken)
    {
        var claim = ClaimUrl(setupToken);
        using var response = await Send(new HttpRequestMessage(HttpMethod.Post, claim) { Content = new ByteArrayContent([]) }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ConnectorException(ConnectorFailure.Refused,
                "SimpleFIN refused this setup token. It may have been used already: if you didn't use it, disable it in SimpleFIN, then make a new one.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectorException(ConnectorFailure.Unavailable, $"SimpleFIN answered {(int)response.StatusCode} instead of a connection. Try again later.");
        }

        var access = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (!Uri.TryCreate(access, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            throw new ConnectorException(ConnectorFailure.Invalid, "SimpleFIN's answer wasn't a secure access address, so nothing was saved.");
        }

        return access;
    }

    public IConnector Open(string credential) => new SimpleFinConnector(this, credential);

    internal TimeProvider Clock => _clock;

    internal async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The inner exception is dropped: its message can echo the request address.
            throw new ConnectorException(ConnectorFailure.Unavailable, "Couldn't reach SimpleFIN. Check the internet connection and try again.");
        }
    }

    internal static Uri ClaimUrl(string setupToken)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(setupToken.Trim()));
        }
        catch (FormatException)
        {
            throw new FormatException("That isn't a SimpleFIN setup token. Copy the whole token from SimpleFIN and paste it again.");
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0
            ? url
            : throw new FormatException("That token doesn't point to a secure SimpleFIN address, so Mossmint won't use it.");
    }

    // The access URL carries its credentials as user info; requests send them as
    // Basic auth to the same address without them.
    internal static (Uri Root, AuthenticationHeaderValue Auth) Split(string credential)
    {
        if (!Uri.TryCreate(credential, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            throw new ConnectorException(ConnectorFailure.Invalid, "The saved SimpleFIN connection is damaged. Disconnect it and connect again.");
        }

        var parts = url.UserInfo.Split(':', 2);
        var pair = $"{Uri.UnescapeDataString(parts[0])}:{Uri.UnescapeDataString(parts[1])}";
        var root = new UriBuilder(url) { UserName = "", Password = "" }.Uri;
        return (root, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair))));
    }
}
