namespace Meitou.Simulation;

/// <summary>
/// Verified external subject, not a display name or a client-selected player ID.
/// </summary>
public sealed record AccountIdentity(string Provider, string Subject)
{
    public static bool IsValid(AccountIdentity? account) => account is not null && account.Provider is { Length: > 0 and <= 64 }
        && account.Provider.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')
        && account.Subject is { Length: > 0 and <= 256 } && account.Subject == account.Subject.Trim() && !account.Subject.Any(char.IsControl);
}

public sealed partial class WorldPlayers
{
    readonly Dictionary<AccountIdentity, PlayerId> _accounts = [];
    readonly HashSet<AccountIdentity> _uncommittedAccounts = [];
    public WorldPlayer? FindAccount(AccountIdentity account) => _accounts.TryGetValue(account, out var id) ? Find(id) : null;
    public bool AccountRequiresCheckpoint(AccountIdentity account) => _uncommittedAccounts.Contains(account);

    /// <summary>
    /// Trusted world owner only after the current binding's checkpoint commit succeeds. Loaded bindings are already durable.
    /// </summary>
    public void ConfirmAccountCheckpoint(AccountIdentity account) { EnsureMutable(); _uncommittedAccounts.Remove(account); }

    /// <summary>
    /// Trusted host only after authentication. The binding is part of the same checkpoint as roles and resources.
    /// </summary>
    public (WorldPlayer Player, bool Created) ResolveAccount(AccountIdentity account)
    {
        EnsureMutable();
        if (!AccountIdentity.IsValid(account)) throw new ArgumentException("Invalid account subject.", nameof(account));
        if (FindAccount(account) is { } existing) return (existing, false);
        var id = new PlayerId(checked(_players.Keys.Last() + 1));
        var player = Register(id); BindAccount(id, account); return (player, true);
    }

    /// <summary>
    /// Explicit administrative binding for an unbound/imported player. Never matches by nickname, and never overwrites a binding.
    /// </summary>
    public void BindAccount(PlayerId playerId, AccountIdentity account)
    {
        EnsureMutable();
        if (!AccountIdentity.IsValid(account)) throw new ArgumentException("Invalid account subject.", nameof(account));
        var player = Required(playerId);
        if (player.Account == account) return;
        if (player.Account is not null || _accounts.ContainsKey(account)) throw new InvalidOperationException("Player or account is already bound.");
        NextRevision(); EnableExplicitControl(); player.Account = account; _accounts.Add(account, playerId); _uncommittedAccounts.Add(account);
    }
}