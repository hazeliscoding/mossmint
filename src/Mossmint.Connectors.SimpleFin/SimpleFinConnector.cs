using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Mossmint.Connectors.Abstractions;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Connectors.SimpleFin;

// One SimpleFIN connection. A sync asks for everything since five days before
// the last one (the Bridge's advice, so late-posting transactions aren't
// missed), at most 90 days back, the Bridge's limit per request. Pending
// transactions are left out until 0.8 can link them to what posts.
internal sealed class SimpleFinConnector(SimpleFinProvider provider, string credential) : IConnector
{
    private const int FirstSyncDays = 90;
    private const int MaxDays = 90;
    private const int OverlapDays = 5;
    private const int MaxText = 200;

    public ConnectorId Id => SimpleFinProvider.ProviderId;

    public string DisplayName => provider.DisplayName;

    public ConnectorCapabilities Capabilities => ConnectorCapabilities.Accounts | ConnectorCapabilities.Balances | ConnectorCapabilities.Transactions;

    public async Task<ConnectorSync> SyncAsync(string? cursor, CancellationToken cancellationToken)
    {
        var now = provider.Clock.GetUtcNow();
        var earliest = now.AddDays(-MaxDays);
        var start = cursor is not null && long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            ? DateTimeOffset.FromUnixTimeSeconds(last).AddDays(-OverlapDays)
            : now.AddDays(-FirstSyncDays);
        var errors = new List<ConnectorError>();
        if (start < earliest)
        {
            start = earliest;
            errors.Add(new ConnectorError(null, $"The last sync was more than {MaxDays - OverlapDays} days ago and SimpleFIN sends at most {MaxDays} days at a time, so older transactions may be missing."));
        }

        var (root, auth) = SimpleFinProvider.Split(credential);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{root.AbsoluteUri.TrimEnd('/')}/accounts?version=2&start-date={start.ToUnixTimeSeconds()}");
        request.Headers.Authorization = auth;
        using var response = await provider.Send(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden:
                throw new ConnectorException(ConnectorFailure.Refused,
                    "SimpleFIN no longer accepts this connection; it may have been disabled. Connect again with a new setup token.");
            case HttpStatusCode.PaymentRequired:
                throw new ConnectorException(ConnectorFailure.NeedsAttention, "SimpleFIN says your subscription needs attention. Check your SimpleFIN account, then sync again.");
            case not HttpStatusCode.OK:
                throw new ConnectorException(ConnectorFailure.Unavailable, $"SimpleFIN answered {(int)response.StatusCode}. Try again later.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return Map(json, now, provider.Clock.LocalTimeZone, errors);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new ConnectorException(ConnectorFailure.Invalid, "SimpleFIN's answer couldn't be read. Try again later.");
        }
    }

    private static ConnectorSync Map(string json, DateTimeOffset now, TimeZoneInfo zone, List<ConnectorError> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var reported = Items(root, "errlist")
            .Select(e => (Account: Text(e, "account_id"), Connection: Text(e, "conn_id"), Message: Clean(Text(e, "msg") ?? Text(e, "code") ?? "Unknown problem")))
            .ToList();
        if (reported.Count == 0)
        {
            reported.AddRange(Items(root, "errors").Where(e => e.ValueKind == JsonValueKind.String).Select(e => ((string?)null, (string?)null, Clean(e.GetString()!))));
        }

        var connections = Items(root, "connections").ToDictionary(c => Text(c, "conn_id") ?? "", c => Clean(Text(c, "name") ?? ""));
        var accounts = new List<ConnectorAccount>();
        var balances = new List<ConnectorBalance>();
        var transactions = new List<ConnectorTransaction>();
        foreach (var account in Items(root, "accounts"))
        {
            var id = Text(account, "id") ?? throw new FormatException("An account has no id.");
            var name = Clean(Text(account, "name") ?? id);
            var code = Text(account, "currency") ?? "";
            if (code.Length != 3 || !TryCurrency(code, out var currency))
            {
                errors.Add(new ConnectorError(id, $"{name} uses a currency Mossmint doesn't support, so it was left out."));
                continue;
            }

            var connection = connections.GetValueOrDefault(Text(account, "conn_id") ?? "");
            accounts.Add(new ConnectorAccount(id, string.IsNullOrEmpty(connection) ? name : $"{connection} · {name}", Kind(name), currency));
            var balanceDate = Day(account.GetProperty("balance-date").GetInt64(), zone);
            balances.Add(new ConnectorBalance(id, $"{id}:{balanceDate:yyyyMMdd}", new Money(Amount(Text(account, "balance")), currency), balanceDate));
            foreach (var t in Items(account, "transactions"))
            {
                var posted = t.TryGetProperty("posted", out var p) ? p.GetInt64() : 0;
                if ((t.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.True) || posted == 0)
                {
                    continue;
                }

                transactions.Add(new ConnectorTransaction(id, Text(t, "id") ?? throw new FormatException("A transaction has no id."), Day(posted, zone),
                    new Money(Amount(Text(t, "amount")), currency), false, Clean(Text(t, "description") ?? Text(t, "payee") ?? "")));
            }
        }

        // A problem with a whole bank connection is a problem for each of its accounts.
        var connectionOf = Items(root, "accounts").Where(a => Text(a, "id") is not null).ToDictionary(a => Text(a, "id")!, a => Text(a, "conn_id"));
        foreach (var (account, connection, message) in reported)
        {
            var affected = account is not null || connection is null ? [account] : connectionOf.Where(a => a.Value == connection).Select(a => (string?)a.Key).ToList();
            errors.AddRange((affected.Count == 0 ? [null] : affected).Select(id => new ConnectorError(id, message)));
        }

        return new ConnectorSync(SimpleFinProvider.ProviderId, now, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), accounts, balances, transactions, [])
        {
            Errors = errors,
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal Amount(string? text) =>
        decimal.Parse(text ?? throw new FormatException("An amount is missing."), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    // A SimpleFIN time is a moment; the day it falls on is the user's local day.
    private static DateOnly Day(long epochSeconds, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(epochSeconds), zone).DateTime);

    private static bool TryCurrency(string code, out Currency currency)
    {
        try
        {
            currency = Currency.Of(code.ToUpperInvariant());
            return true;
        }
        catch (ArgumentException)
        {
            currency = default;
            return false;
        }
    }

    // SimpleFIN only names accounts; this is a starting guess the user can change.
    // "Checking" and "saving" win over "credit", so a credit union's checking
    // account isn't taken for a card.
    private static AccountKind Kind(string name) => name.ToLowerInvariant() switch
    {
        var n when n.Contains("checking", StringComparison.Ordinal) => AccountKind.Checking,
        var n when n.Contains("saving", StringComparison.Ordinal) => AccountKind.Savings,
        var n when n.Contains("card", StringComparison.Ordinal) => AccountKind.CreditCard,
        var n when n.Contains("loan", StringComparison.Ordinal) || n.Contains("mortgage", StringComparison.Ordinal) => AccountKind.InstallmentLoan,
        _ => AccountKind.Checking,
    };

    // Text from the provider is shown as is, so control and format characters go
    // (a right-to-left override could make one payee read as another) and
    // length is capped.
    private static string Clean(string text)
    {
        var kept = new StringBuilder(Math.Min(text.Length, MaxText));
        foreach (var c in text.Where(c => char.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format)).Take(MaxText))
        {
            kept.Append(c);
        }

        return kept.ToString().Trim();
    }
}
