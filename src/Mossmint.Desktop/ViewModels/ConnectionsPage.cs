using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
using Mossmint.Connectors.Abstractions;
using Mossmint.Desktop.Controls;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.ViewModels;

public sealed record ConnectionRow(Connection Connection, string Title, string Detail);

// Optional bank connections, SimpleFIN for now. Nothing here touches the
// network until the user connects or presses Sync now. The first sync is
// previewed; later ones come straight in with a summary, and anything odd
// goes to the review queue (ROADMAP.md, M7 decisions).
public sealed partial class ConnectionsPageViewModel(AppServices services)
    : PageViewModel(Destination.Connections, "Connections", "Optional bank connections. Mossmint works fully without them.")
{
    // The official Bridge; a token for any other host gets a warning first.
    private static readonly string[] KnownHosts = ["bridge.simplefin.org", "beta-bridge.simplefin.org"];

    private Pending? _pending;

    // The session this page's state belongs to; see ProfileContext.IsCurrent.
    private IProfileSession? _shownFor;

    public ObservableCollection<ConnectionRow> Connections { get; } = [];

    public ObservableCollection<DestinationRow> Linking { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartConnect))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenHost), nameof(TokenError), nameof(HostWarning))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _setupToken = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartConnect))]
    private bool _isLinking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(CanStartConnect))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private ImportPreviewViewModel? _preview;

    [ObservableProperty]
    private ConnectionRow? _confirmingDisconnect;

    [ObservableProperty]
    private bool _eraseOnDisconnect;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private StatusTone _statusTone = StatusTone.Neutral;

    [ObservableProperty]
    private string? _linkError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(SyncCommand), nameof(ContinueCommand), nameof(ImportCommand), nameof(StartDisconnectCommand))]
    private bool _isBusy;

    public bool HasConnections => Connections.Count > 0;

    public bool HasPreview => Preview is not null;

    public bool CanStartConnect => !HasConnections && !IsConnecting && !IsLinking && Preview is null;

    public string ProviderName => services.SimpleFin.DisplayName;

    public string? TokenHost => TryHost(out var host, out _) ? host : null;

    public string? TokenError => SetupToken.Trim().Length == 0 || TryHost(out _, out _) ? null : Error();

    public string? HostWarning => TokenHost is { } host && !KnownHosts.Contains(host)
        ? $"This token points to {host}, not the SimpleFIN Bridge. Connect only if you trust that server with your bank data."
        : null;

    public override void OnNavigatedTo()
    {
        // Another profile is open now: nothing begun for the last one carries over.
        if (!services.Context.IsCurrent(_shownFor))
        {
            Reset();
            _shownFor = services.Context.IsUnlocked ? services.Context.Current : null;
        }

        Refresh();
    }

    partial void OnIsBusyChanged(bool value) => services.Context.ActiveWork += value ? 1 : -1;

    [RelayCommand]
    private void StartConnect()
    {
        (IsConnecting, SetupToken, Status) = (true, "", null);
    }

    [RelayCommand]
    private void CancelConnect() => (IsConnecting, SetupToken) = (false, "");

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        IsBusy = true;
        try
        {
            var (provider, session) = (services.SimpleFin, services.Context.Current);
            var credential = await provider.ClaimAsync(SetupToken.Trim(), CancellationToken.None);
            if (!services.Context.IsCurrent(session))
            {
                return;
            }

            var connection = new Connection(Guid.CreateVersion7(services.Clock.GetUtcNow()), provider.Id.Value, credential, null,
                new Dictionary<string, Guid>(), services.Clock.GetUtcNow());

            // Saved at once: the token works only once, so the credential must not be lost.
            session.Imports.SaveConnection(connection);
            (IsConnecting, SetupToken) = (false, "");
            Refresh();
            await Fetch(connection);
        }
        catch (ConnectorException e)
        {
            Show(e.Message, StatusTone.Critical);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Sync(ConnectionRow row)
    {
        IsBusy = true;
        try
        {
            await Fetch(row.Connection);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Continue()
    {
        if (_pending is not { } pending || !services.Context.IsCurrent(pending.Session))
        {
            Reset();
            return;
        }

        if (Linking.FirstOrDefault(r => r.Selected is null) is { } unchosen)
        {
            LinkError = $"Choose an account for {unchosen.Label}, or leave it out.";
            return;
        }

        // Leaving every account out is allowed here: the connection just brings nothing in.
        var chosen = Linking.Where(r => r.Selected!.Account is not null || r.IsNew).ToList();
        string? error = null;
        var targets = chosen.Count == 0
            ? new Dictionary<string, Account>()
            : DestinationRow.Resolve(chosen, pending.Session.Data.Settings().Currency, () => Guid.CreateVersion7(services.Clock.GetUtcNow()), out error);
        if (targets is null)
        {
            LinkError = error;
            return;
        }

        // Two bank accounts feeding one account would mix two balances into one.
        var taken = pending.Links.Where(l => l.Value != Guid.Empty && !targets.ContainsKey(l.Key)).Select(l => l.Value);
        if (targets.Values.Select(a => a.Id.Value).Concat(taken).GroupBy(id => id).Any(g => g.Count() > 1))
        {
            LinkError = "Two bank accounts can't feed the same Mossmint account. Leave one out, or make a new account for it.";
            return;
        }

        foreach (var row in Linking)
        {
            // A row left out is remembered as Guid.Empty, so it isn't asked about again.
            var target = targets.GetValueOrDefault(row.Source);
            pending.Links[row.Source] = target?.Id.Value ?? Guid.Empty;
            if (row.IsNew && target is not null)
            {
                pending.NewAccounts[target.Id.Value] = target;
            }
        }

        (IsLinking, LinkError) = (false, null);
        Linking.Clear();
        IsBusy = true;
        try
        {
            await Plan(pending);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelLinking()
    {
        (IsLinking, LinkError, _pending) = (false, null, null);
        Linking.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        if (_pending is not { } pending || Preview is not { } preview)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Commit(pending, preview.Plan);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelPreview() => (Preview, _pending) = (null, null);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void StartDisconnect(ConnectionRow row) => (ConfirmingDisconnect, EraseOnDisconnect) = (row, false);

    [RelayCommand]
    private void CancelDisconnect() => ConfirmingDisconnect = null;

    [RelayCommand]
    private void ConfirmDisconnect()
    {
        if (ConfirmingDisconnect is not { } row)
        {
            return;
        }

        var imports = services.Context.Current.Imports;
        imports.RemoveConnection(row.Connection.Id);
        var kept = "what the connection brought in stays";
        if (EraseOnDisconnect)
        {
            var (transactions, balances) = imports.EraseConnection(new ConnectorId(row.Connection.Connector));
            kept = $"removed {Count(transactions, "transaction")} and {Count(balances, "balance")} it brought in";
        }

        // A sync still waiting on links or a preview belongs to the connection just removed.
        (ConfirmingDisconnect, Preview, _pending, IsLinking, LinkError) = (null, null, null, false, null);
        Linking.Clear();
        Refresh();
        Show($"Disconnected. Mossmint deleted the key it kept and {kept}. Remove Mossmint's access in {ProviderName} too.", StatusTone.Neutral);
    }

    // Fetches since the last sync, then asks about accounts it hasn't seen,
    // previews a first sync, or brings a later one straight in.
    private async Task Fetch(Connection connection)
    {
        Status = null;
        var session = services.Context.Current;
        try
        {
            var sync = await services.SimpleFin.Open(connection.Secret).SyncAsync(connection.Cursor, CancellationToken.None);
            if (!services.Context.IsCurrent(session))
            {
                return;
            }

            var pending = new Pending(session, connection, sync, new Dictionary<string, Guid>(connection.Links), []);
            _pending = pending;
            var unseen = sync.Accounts.Where(a => !connection.Links.ContainsKey(a.ExternalId)).ToList();
            if (unseen.Count == 0)
            {
                await Plan(pending);
                return;
            }

            var accounts = session.Data.Accounts();
            Linking.Clear();
            foreach (var account in unseen)
            {
                var choices = DestinationRow.ChoicesFor(accounts, canLeaveOut: true);
                var shortName = account.Name[(account.Name.LastIndexOf("· ", StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..];
                var match = choices.FirstOrDefault(c => c.Account is { } a
                    && (string.Equals(a.Name, account.Name, StringComparison.CurrentCultureIgnoreCase) || string.Equals(a.Name, shortName, StringComparison.CurrentCultureIgnoreCase)));
                Linking.Add(new DestinationRow(account.ExternalId, account.Name, choices, match, shortName, account.Kind));
            }

            IsLinking = true;
        }
        catch (ConnectorException e)
        {
            Show(e.Message, e.Failure is ConnectorFailure.Unavailable ? StatusTone.Warning : StatusTone.Critical);
        }
    }

    private async Task Plan(Pending pending)
    {
        var session = pending.Session;
        var existing = session.Data.Accounts().ToDictionary(a => a.Id.Value);
        var destinations = new Dictionary<string, Account>();
        foreach (var (externalId, id) in pending.Links.Where(l => l.Value != Guid.Empty))
        {
            if ((existing.GetValueOrDefault(id) ?? pending.NewAccounts.GetValueOrDefault(id)) is { } account)
            {
                destinations[externalId] = account;
            }
        }

        var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
        var plan = await Task.Run(() => services.Imports.Plan(session,
            ConnectorBatches.From(pending.Sync, ProviderName, today, session.Data.Settings().Currency, destinations)));
        if (!services.Context.IsCurrent(session))
        {
            return;
        }

        if (pending.Connection.LastSyncAt is null || !plan.CanCommit)
        {
            Preview = new ImportPreviewViewModel(plan, []);
            return;
        }

        await Commit(pending, plan);
    }

    private async Task Commit(Pending pending, ImportPlan plan)
    {
        var session = pending.Session;
        if (!services.Context.IsCurrent(session) || !session.Imports.Connections().Any(c => c.Id == pending.Connection.Id))
        {
            Reset();
            return;
        }

        // A problem the bank reported for a linked account keeps the window
        // open, so the next sync asks for those days again; an account linked
        // after the first sync starts it over, so it gets its own 90 days.
        var problem = pending.Sync.Errors.Any(e => e.AccountExternalId is { } id && pending.Links.GetValueOrDefault(id) != Guid.Empty);
        var newlyLinked = pending.Connection.LastSyncAt is not null
            && pending.Links.Any(l => l.Value != Guid.Empty && !pending.Connection.Links.ContainsKey(l.Key));
        var cursor = newlyLinked ? null : problem ? pending.Connection.Cursor : pending.Sync.Cursor;

        try
        {
            await Task.Run(() =>
            {
                ImportService.Commit(session, plan);
                session.Imports.SaveConnection(pending.Connection with
                {
                    Cursor = cursor,
                    Links = pending.Links,
                    LastSyncAt = services.Clock.GetUtcNow(),
                });
            });
            var added = plan.Changes.Count(c => c.Entity == "Transaction" && c.Kind == ChangeKind.Add);
            var updated = plan.Changes.Count(c => c.Entity == "Transaction" && c.Kind == ChangeKind.Update);
            var review = plan.Commit.ReviewItems.Count;
            Show($"Synced. {Count(added, "new transaction")}, {updated} updated" +
                (plan.Changes.Any(c => c.Entity == "Balance" && c.Kind == ChangeKind.Add) ? ", balances refreshed" : "") +
                (review > 0 ? $". {Count(review, "item")} to review on the Imports page." : "."), review > 0 ? StatusTone.Warning : StatusTone.Healthy);
            (Preview, _pending) = (null, null);
            Refresh();
        }
        catch (StaleImportPlanException e)
        {
            Show(e.Message, StatusTone.Warning);
        }
        catch (InvalidOperationException e)
        {
            Show($"Nothing was imported: {e.Message}", StatusTone.Critical);
        }
    }

    private void Refresh()
    {
        if (!services.Context.IsUnlocked)
        {
            return;
        }

        Connections.Clear();
        foreach (var connection in services.Context.Current.Imports.Connections())
        {
            var host = Uri.TryCreate(connection.Secret, UriKind.Absolute, out var url) ? url.IdnHost : "an unknown server";
            var detail = connection.LastSyncAt is { } at
                ? $"Last synced {at.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)}. Feeds {connection.Links.Count(l => l.Value != Guid.Empty)} account(s)."
                : "Not synced yet.";
            Connections.Add(new ConnectionRow(connection, $"{ProviderName} · {host}", detail));
        }

        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(CanStartConnect));
    }

    private void Reset()
    {
        (IsConnecting, SetupToken, IsLinking, LinkError, Preview, _pending, ConfirmingDisconnect, Status) = (false, "", false, null, null, null, null, null);
        Linking.Clear();
    }

    private bool TryHost(out string? host, out string? error)
    {
        try
        {
            (host, error) = (services.SimpleFin.HostOf(SetupToken.Trim()), null);
            return true;
        }
        catch (FormatException e)
        {
            (host, error) = (null, e.Message);
            return false;
        }
    }

    private string? Error() => TryHost(out _, out var error) ? null : error;

    private void Show(string message, StatusTone tone) => (Status, StatusTone) = (message, tone);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private bool IsIdle() => !IsBusy;

    private bool CanConnect() => !IsBusy && TokenHost is not null;

    private bool CanImport() => !IsBusy && Preview is { CanCommit: true };

    // A fetched sync waiting on links, a preview or a commit.
    private sealed record Pending(IProfileSession Session, Connection Connection, ConnectorSync Sync, Dictionary<string, Guid> Links, Dictionary<Guid, Account> NewAccounts);
}
