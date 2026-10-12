using System.Net;
using System.Text;
using Mossmint.Connectors.Abstractions;
using Mossmint.Connectors.SimpleFin;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Import.Tests;

// Every exchange goes through a scripted handler: nothing here reaches a network.
public sealed class SimpleFinTests
{
    private const string Access = "https://demo-user:p4ss-w0rd@bridge.example/simplefin";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly string Token = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bridge.example/simplefin/claim/abc123"));

    internal const string AccountSet = """
        {
          "errlist": [ { "code": "con.auth", "msg": "Sign in again at your bank.", "conn_id": "CON-1", "account_id": "ACT-2" } ],
          "connections": [ { "conn_id": "CON-1", "name": "Example Bank", "org_id": "ex", "sfin_url": "https://bridge.example/simplefin" } ],
          "accounts": [
            {
              "id": "ACT-1", "name": "Everyday Checking", "conn_id": "CON-1", "currency": "USD",
              "balance": "1295.50", "available-balance": "1290.00", "balance-date": 1791806400,
              "transactions": [
                { "id": "TRN-1", "posted": 1791460800, "amount": "1200.00", "description": "PAYROLL" },
                { "id": "TRN-2", "posted": 1791547200, "amount": "-4.50", "description": "CORNER\u0007 CAFE" },
                { "id": "TRN-3", "posted": 0, "amount": "-9.99", "description": "STILL PENDING", "pending": true }
              ]
            },
            { "id": "ACT-2", "name": "Rewards Card", "conn_id": "CON-1", "currency": "USD", "balance": "-311.42", "balance-date": 1791806400, "transactions": [] },
            { "id": "ACT-3", "name": "Points", "conn_id": "CON-1", "currency": "https://bridge.example/currency/points", "balance": "5000", "balance-date": 1791806400 }
          ]
        }
        """;

    [Fact]
    public void The_token_names_the_host_before_anything_is_sent()
    {
        new SimpleFinProvider(new FixedTime(Now)).HostOf(Token).Should().Be("bridge.example");
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("aHR0cDovL2JyaWRnZS5leGFtcGxlL2NsYWlt")]
    [InlineData("bm90IGEgdXJs")]
    public void A_token_that_is_not_an_https_claim_url_is_refused(string token) =>
        FluentActions.Invoking(() => new SimpleFinProvider(new FixedTime(Now)).HostOf(token)).Should().Throw<FormatException>();

    [Fact]
    public async Task Claiming_posts_to_the_claim_url_and_returns_the_access_url()
    {
        var handler = new Scripted(_ => Respond(HttpStatusCode.OK, Access + "\n"));

        var credential = await new SimpleFinProvider(new FixedTime(Now), handler).ClaimAsync(Token, CancellationToken.None);

        credential.Should().Be(Access);
        handler.Requests.Should().ContainSingle().Which.Should().Be((HttpMethod.Post, "https://bridge.example/simplefin/claim/abc123", (string?)null));
    }

    [Fact]
    public async Task A_used_token_is_refused_with_advice_to_disable_it()
    {
        var provider = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.Forbidden, "")));

        var claim = () => provider.ClaimAsync(Token, CancellationToken.None);

        (await claim.Should().ThrowAsync<ConnectorException>()).Which.Should().Match<ConnectorException>(e => e.Failure == ConnectorFailure.Refused && e.Message.Contains("disable"));
    }

    [Fact]
    public async Task A_claim_answer_that_is_not_an_https_access_url_is_refused()
    {
        var provider = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, "http://user:pw@bridge.example/simplefin")));

        var claim = () => provider.ClaimAsync(Token, CancellationToken.None);

        (await claim.Should().ThrowAsync<ConnectorException>()).Which.Failure.Should().Be(ConnectorFailure.Invalid);
    }

    [Fact]
    public async Task A_first_sync_asks_for_ninety_days_with_basic_auth_and_maps_what_comes_back()
    {
        var handler = new Scripted(_ => Respond(HttpStatusCode.OK, AccountSet));

        var sync = await new SimpleFinProvider(new FixedTime(Now), handler).Open(Access).SyncAsync(null, CancellationToken.None);

        var (method, url, auth) = handler.Requests.Single();
        method.Should().Be(HttpMethod.Get);
        url.Should().Be($"https://bridge.example/simplefin/accounts?version=2&start-date={Now.AddDays(-90).ToUnixTimeSeconds()}");
        auth.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("demo-user:p4ss-w0rd")));
        sync.Connector.Should().Be(SimpleFinProvider.ProviderId);
        sync.Cursor.Should().Be(Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        sync.Accounts.Select(a => (a.ExternalId, a.Name, a.Kind)).Should().Equal(
            ("ACT-1", "Example Bank · Everyday Checking", AccountKind.Checking), ("ACT-2", "Example Bank · Rewards Card", AccountKind.CreditCard));
        sync.Balances.Select(b => (b.AccountExternalId, b.Balance, b.AsOf)).Should().Equal(
            ("ACT-1", Money.Usd(1295.50m), new DateOnly(2026, 10, 12)), ("ACT-2", Money.Usd(-311.42m), new DateOnly(2026, 10, 12)));
        sync.Transactions.Select(t => (t.ExternalId, t.Amount, t.Description, t.IsPending)).Should().Equal(
            ("TRN-1", Money.Usd(1200m), "PAYROLL", false), ("TRN-2", Money.Usd(-4.50m), "CORNER CAFE", false));
        sync.Errors.Should().BeEquivalentTo([
            new ConnectorError("ACT-2", "Sign in again at your bank."),
            new ConnectorError("ACT-3", "Points uses a currency Mossmint doesn't support, so it was left out."),
        ]);
    }

    [Fact]
    public async Task A_later_sync_starts_five_days_before_the_last_one()
    {
        var handler = new Scripted(_ => Respond(HttpStatusCode.OK, """{ "errlist": [], "connections": [], "accounts": [] }"""));
        var last = Now.AddDays(-2);

        await new SimpleFinProvider(new FixedTime(Now), handler).Open(Access).SyncAsync(last.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);

        handler.Requests.Single().Url.Should().EndWith($"start-date={last.AddDays(-5).ToUnixTimeSeconds()}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ConnectorFailure.Refused)]
    [InlineData(HttpStatusCode.PaymentRequired, ConnectorFailure.NeedsAttention)]
    [InlineData(HttpStatusCode.InternalServerError, ConnectorFailure.Unavailable)]
    [InlineData(HttpStatusCode.Found, ConnectorFailure.Unavailable)]
    public async Task A_failed_sync_says_why_without_the_credential(HttpStatusCode status, ConnectorFailure failure)
    {
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(status, ""))).Open(Access);

        var sync = () => connector.SyncAsync(null, CancellationToken.None);

        var thrown = (await sync.Should().ThrowAsync<ConnectorException>()).Which;
        thrown.Failure.Should().Be(failure);
        thrown.ToString().Should().NotContain("p4ss-w0rd");
    }

    [Fact]
    public void A_look_alike_host_is_shown_in_its_ascii_form()
    {
        // The "е" is Cyrillic: shown as typed, this host would pass for bridge.example.
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bridgе.example/simplefin/claim/abc"));

        new SimpleFinProvider(new FixedTime(Now)).HostOf(token).Should().StartWith("xn--");
    }

    [Fact]
    public async Task Text_that_could_reorder_or_hide_itself_is_stripped()
    {
        var json = AccountSet.Replace("PAYROLL", "PAY‮ROLL​", StringComparison.Ordinal);
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, json))).Open(Access);

        var sync = await connector.SyncAsync(null, CancellationToken.None);

        sync.Transactions[0].Description.Should().Be("PAYROLL");
    }

    [Fact]
    public async Task An_oversized_answer_is_refused_rather_than_read()
    {
        var huge = new string(' ', 17 * 1024 * 1024);
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, huge))).Open(Access);

        var sync = () => connector.SyncAsync(null, CancellationToken.None);

        (await sync.Should().ThrowAsync<ConnectorException>()).Which.Failure.Should().Be(ConnectorFailure.Unavailable);
    }

    [Fact]
    public async Task An_impossible_date_is_invalid_rather_than_a_crash()
    {
        var json = AccountSet.Replace("1791806400", "99999999999999", StringComparison.Ordinal);
        json.Should().Contain("99999999999999");
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, json))).Open(Access);

        var sync = () => connector.SyncAsync(null, CancellationToken.None);

        (await sync.Should().ThrowAsync<ConnectorException>()).Which.Failure.Should().Be(ConnectorFailure.Invalid);
    }

    [Theory]
    [InlineData("Credit Union Checking", AccountKind.Checking)]
    [InlineData("Everyday Savings", AccountKind.Savings)]
    [InlineData("Rewards Visa Card", AccountKind.CreditCard)]
    [InlineData("Platinum Credit Card", AccountKind.CreditCard)]
    [InlineData("Auto Loan", AccountKind.InstallmentLoan)]
    public async Task Account_kinds_are_guessed_from_names_without_mistaking_a_credit_union(string name, AccountKind kind)
    {
        var json = AccountSet.Replace("\"name\": \"Everyday Checking\"", $"\"name\": \"{name}\"", StringComparison.Ordinal);
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, json))).Open(Access);

        var sync = await connector.SyncAsync(null, CancellationToken.None);

        sync.Accounts[0].Kind.Should().Be(kind);
    }

    [Fact]
    public async Task A_problem_with_a_whole_bank_connection_is_reported_for_each_of_its_accounts()
    {
        var json = AccountSet.Replace(", \"account_id\": \"ACT-2\"", "", StringComparison.Ordinal);
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, json))).Open(Access);

        var sync = await connector.SyncAsync(null, CancellationToken.None);

        sync.Errors.Where(e => e.Message == "Sign in again at your bank.").Select(e => e.AccountExternalId).Should().BeEquivalentTo(["ACT-1", "ACT-2", "ACT-3"]);
    }

    [Fact]
    public async Task A_garbled_answer_is_invalid()
    {
        var connector = new SimpleFinProvider(new FixedTime(Now), new Scripted(_ => Respond(HttpStatusCode.OK, "<html>maintenance</html>"))).Open(Access);

        var sync = () => connector.SyncAsync(null, CancellationToken.None);

        (await sync.Should().ThrowAsync<ConnectorException>()).Which.Failure.Should().Be(ConnectorFailure.Invalid);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
