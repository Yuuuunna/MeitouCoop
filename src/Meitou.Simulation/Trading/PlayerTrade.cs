using Meitou.Simulation.Items;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Trading;

public sealed record OpenPlayerTrade(CharacterId Actor, PlayerId Counterparty, CharacterId CounterpartyActor, int PaymentFaction) : SimCommand;
public abstract record PlayerTradeCommand(ulong SessionId, ulong Revision) : SimCommand;
public sealed record AcceptPlayerTrade(ulong SessionId, ulong Revision, CharacterId Actor, int PaymentFaction) : PlayerTradeCommand(SessionId, Revision);
public sealed record PlayerTradeItemOffer(InventoryItemToken Item, int Count);
public sealed record RevisePlayerTrade(ulong SessionId, ulong Revision, long Money, ulong InventoryRevision, IReadOnlyList<PlayerTradeItemOffer> Items) : PlayerTradeCommand(SessionId, Revision);
public sealed record ConfirmPlayerTrade(ulong SessionId, ulong Revision) : PlayerTradeCommand(SessionId, Revision);
public sealed record CancelPlayerTrade(ulong SessionId, ulong Revision) : PlayerTradeCommand(SessionId, Revision);
public sealed record PlayerTradeGoods(InventoryItemToken Item, string Name, int Count);
public sealed record PlayerTradeSideView(PlayerId Player, CharacterId Actor, int PaymentFaction, bool Accepted, bool Confirmed, long Money, ulong InventoryRevision, IReadOnlyList<PlayerTradeGoods> Items);
/// <summary>Only the two participants receive this view; available goods belong exclusively to the viewer's role.</summary>
public sealed record PlayerTradeView(ulong SessionId, ulong Revision, PlayerTradeSideView Own, PlayerTradeSideView Other, IReadOnlyList<PlayerTradeGoods> Available, IReadOnlyList<int> PaymentFactions);