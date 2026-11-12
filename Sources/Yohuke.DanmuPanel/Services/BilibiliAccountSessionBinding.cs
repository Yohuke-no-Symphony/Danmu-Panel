using System.ComponentModel;
using Yohuke.DanmuCore.Services;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.Services;

internal sealed class BilibiliAccountSessionBinding(IBilibiliAccountSessionService sessions) : IDisposable
{
    private readonly Dictionary<string, BilibiliAccount> accounts = new();
    private bool isDisposed;

    internal void Synchronize(IEnumerable<BilibiliAccount> currentAccounts)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        var current = currentAccounts.ToDictionary(account => account.Id);
        foreach (var accountId in accounts.Keys.Except(current.Keys).ToArray())
        {
            Remove(accountId);
        }

        foreach (var account in current.Values)
        {
            Register(account);
        }
    }

    internal void Register(BilibiliAccount account)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (accounts.TryGetValue(account.Id, out var previous))
        {
            if (ReferenceEquals(previous, account))
            {
                return;
            }

            Remove(account.Id);
        }

        accounts.Add(account.Id, account);
        sessions.Register(account.Id, account.Cookie);
        account.PropertyChanged += OnAccountChanged;
    }

    internal void Remove(string accountId)
    {
        if (accounts.Remove(accountId, out var account))
        {
            account.PropertyChanged -= OnAccountChanged;
            sessions.Remove(accountId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        foreach (var accountId in accounts.Keys.ToArray())
        {
            Remove(accountId);
        }

        isDisposed = true;
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(BilibiliAccount.Cookie) && sender is BilibiliAccount account)
        {
            sessions.UpdateCredentials(account.Id, account.Cookie);
        }
    }
}
