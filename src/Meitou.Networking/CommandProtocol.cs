using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Networking;
public sealed record ExpectedRoleGrant(EntityId Entity, int Faction, ulong Revision);
public sealed record ExpectedCommandAuthority(ExpectedRoleGrant[] Roles, int Faction, ulong MembershipRevision);
public sealed record DecodedPlayerCommand(Guid World, ulong Request, SimCommand Command, ExpectedCommandAuthority Expected);
public sealed record CommandReceiptMessage(Guid World, AuthorityReceipt Receipt);
/// <summary>
/// Explicit wire type IDs. No runtime type names, internal/debug commands, issuer or client scheduling tick enter the decoder.
/// </summary>
public static class CommandProtocol
{
    static readonly IReadOnlyDictionary<ushort, Type> Types = new Dictionary<ushort, Type>
    {
        [1] = typeof(MoveOrder),
        [2] = typeof(FollowOrder),
        [3] = typeof(StopCommand),
        [4] = typeof(SelectCommand),
        [5] = typeof(AttackOrder),
        [6] = typeof(ShootAtOrder),
        [7] = typeof(OrdersCommand),
        [8] = typeof(RunSpeedCommand),
        [9] = typeof(StandUpCommand),
        [10] = typeof(ManoeuvreCommand),
        [20] = typeof(OpenTrade),
        [21] = typeof(BuyItem),
        [22] = typeof(SellItem),
        [23] = typeof(CloseTrade),
        [24] = typeof(ArrangeStock),
        [30] = typeof(ArrangeInventory),
        [31] = typeof(QuickMoveItem),
        [32] = typeof(ReadMapItem),
        [33] = typeof(TransferInventoryItem),
        [34] = typeof(OpenContainerOrder),
        [35] = typeof(CloseContainer),
        [40] = typeof(TalkOrder),
        [41] = typeof(DialogueChoice),
        [42] = typeof(EndConversation),
        [43] = typeof(ConfirmCharacterTrade),
        [44] = typeof(CloseCharacterTrade),
        [50] = typeof(NewSquadCommand),
        [51] = typeof(RenameSquadCommand),
        [52] = typeof(RemoveSquadCommand),
        [53] = typeof(MoveToSquadCommand),
        [54] = typeof(ReorderSquadCommand),
        [55] = typeof(ActiveSquadCommand),
        [56] = typeof(DismissCommand),
        [57] = typeof(RenameFactionCommand),
        [58] = typeof(SquadFormationCommand),
        [59] = typeof(SquadAiCommand),
        [70] = typeof(PutInMachine),
        [71] = typeof(TakeFromMachine),
        [72] = typeof(QueueCraft),
        [73] = typeof(CancelCraft),
        [74] = typeof(RepeatCrafts),
        [75] = typeof(SetMachinePower),
        [76] = typeof(IncinerateFurnace),
        [77] = typeof(QueueResearch),
        [78] = typeof(CancelResearch),
        [80] = typeof(RemoveJob),
        [81] = typeof(MoveJob),
        [82] = typeof(AddOrderJob),
        [83] = typeof(OperateOrder),
        [84] = typeof(UseTurretOrder),
        [85] = typeof(AddJob),
        [86] = typeof(ProspectOrder),
        [90] = typeof(BuildOrder),
        [91] = typeof(DismantleOrder),
        [92] = typeof(ConfirmDismantle),
        [93] = typeof(CancelSite),
        [94] = typeof(PauseSite),
        [95] = typeof(UpgradeSite),
        [96] = typeof(DowngradeSite),
        [97] = typeof(SetBuildingPublic),
        [98] = typeof(RenameTown),
        [99] = typeof(SetTownPublic),
        [100] = typeof(PlaceBuildings),
        [101] = typeof(BuyBuilding),
        [110] = typeof(TreatOrder),
        [111] = typeof(SplintOrder),
        [112] = typeof(RestOrder),
        [113] = typeof(MedicJobCommand),
        [120] = typeof(EditCharacter),
        [130] = typeof(PickUpOrder),
        [131] = typeof(PutDownOrder),
        [132] = typeof(LootOrder),
        [133] = typeof(OpenLootOrder),
        [134] = typeof(StealthKoOrder),
        [135] = typeof(RescueJobCommand),
        [136] = typeof(TakeLootItem),
        [137] = typeof(PutLootItem),
        [138] = typeof(CloseLoot),
        [139] = typeof(DisposeCorpseOrder),
        [140] = typeof(PickLockOrder),
        [141] = typeof(GetOutOrder),
        [142] = typeof(DoorOrder),
        [143] = typeof(SetFreeOrder),
        [144] = typeof(GetInCageOrder),
        [145] = typeof(ForceLockOrder),
        [146] = typeof(ShackleOrder),
        [147] = typeof(PutShacklesOrder),
        [148] = typeof(UseBedOrder),
        [149] = typeof(PutInBedOrder),
        [150] = typeof(ToggleDoorsOrder),
        [151] = typeof(ToggleDoorLocksOrder),
        [152] = typeof(PutInCageOrder),
        [153] = typeof(PayBailOrder),
        [154] = typeof(DoorLockOrder),
        [160] = typeof(LiftItem),
        [161] = typeof(PlaceHeld),
        [162] = typeof(GiveHeld),
        [163] = typeof(DropHeld),
        [164] = typeof(ReturnHeld),
        [165] = typeof(PickUpItemOrder),
        [180] = typeof(InspectCommand),
        [181] = typeof(InspectBuildingCommand),
        [182] = typeof(ShowInventoryCommand),
        [183] = typeof(PointedCommand),
        [184] = typeof(AreaArrival),
        [170] = typeof(OpenPlayerTrade),
        [171] = typeof(AcceptPlayerTrade),
        [172] = typeof(RevisePlayerTrade),
        [173] = typeof(ConfirmPlayerTrade),
        [174] = typeof(CancelPlayerTrade),
    };
    static readonly IReadOnlyDictionary<Type, ushort> Ids = Types.ToDictionary(p => p.Value, p => p.Key);
    static readonly JsonSerializerOptions Json = Options();
    sealed record Envelope(Guid World, ulong Request, ushort Kind, ExpectedCommandAuthority Expected, JsonElement Body);
    static JsonSerializerOptions Options()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            for (int i = info.Properties.Count - 1; i >= 0; i--)
            {
                var property = info.Properties[i];
                if (property.Set is null || typeof(SimCommand).IsAssignableFrom(info.Type) && property.Name is ("Issuer" or "Tick" or "ControlGrants" or "FactionGrantRevision"))
                    info.Properties.RemoveAt(i);
            }
        });
        var result = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            IncludeFields = true,
            MaxDepth = 16,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        result.Converters.Add(new FiniteSingle());
        result.Converters.Add(new KnownEnums());
        result.Converters.Add(new CommandAppearanceJson());
        return result;
    }

    public static bool Supports(SimCommand command) => command is not null && Ids.ContainsKey(command.GetType());
    public static WireFrame Encode(Guid world, ulong request, SimCommand command, ExpectedCommandAuthority expected)
    {
        if (command is null || !Ids.TryGetValue(command.GetType(), out ushort kind))
            throw new InvalidDataException("Command is not exposed by the wire protocol.");
        ValidateEnvelope(world, request, expected);
        ValidateGeometry(command);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(world, request, kind, expected, JsonSerializer.SerializeToElement(command, command.GetType(), Json)), Json);
        if (payload.Length > WireFrame.MaximumPayload)
            throw new InvalidDataException("Command is too large.");
        ValidateJson(payload);
        return new WireFrame(WireMessage.Command, payload);
    }

    public static DecodedPlayerCommand Decode(WireFrame frame)
    {
        if (frame.Kind != WireMessage.Command || frame.Payload.Length > WireFrame.MaximumPayload)
            throw new InvalidDataException("Expected a bounded command frame.");
        try
        {
            ValidateJson(frame.Payload.Span);
            var envelope = JsonSerializer.Deserialize<Envelope>(frame.Payload.Span, Json) ?? throw new InvalidDataException("Empty command envelope.");
            ValidateEnvelope(envelope.World, envelope.Request, envelope.Expected);
            if (!Types.TryGetValue(envelope.Kind, out var type) || envelope.Body.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Unknown command type or body.");
            var command = (SimCommand? )envelope.Body.Deserialize(type, Json) ?? throw new InvalidDataException("Empty command body.");
            ValidateGeometry(command);
            return new DecodedPlayerCommand(envelope.World, envelope.Request, command with { Tick = 0 }, envelope.Expected);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed player command.", e);
        }
    }

    public static WireFrame EncodeReceipt(Guid world, AuthorityReceipt receipt) => new(WireMessage.CommandReceipt, JsonSerializer.SerializeToUtf8Bytes(new CommandReceiptMessage(world, receipt), Json));
    public static CommandReceiptMessage DecodeReceipt(WireFrame frame)
    {
        if (frame.Kind != WireMessage.CommandReceipt || frame.Payload.Length > 4096)
            throw new InvalidDataException("Expected a command receipt.");
        try
        {
            ValidateJson(frame.Payload.Span);
            var result = JsonSerializer.Deserialize<CommandReceiptMessage>(frame.Payload.Span, Json) ?? throw new InvalidDataException("Empty receipt.");
            if (result.World == Guid.Empty || result.Receipt is null || !Enum.IsDefined(result.Receipt.Status) || result.Receipt.Tick < 0)
                throw new InvalidDataException("Invalid receipt.");
            return result;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed command receipt.", e);
        }
    }

    static void ValidateEnvelope(Guid world, ulong request, ExpectedCommandAuthority expected)
    {
        if (world == Guid.Empty || request == 0 || expected?.Roles is null || expected.Roles.Length > 4096 || expected.Faction < -1 || expected.Faction == -1 && expected.MembershipRevision != 0)
            throw new InvalidDataException("Invalid command scope.");
        var entities = new HashSet<EntityId>();
        foreach (var role in expected.Roles)
            if (role is null || role.Entity.IsNone || role.Faction < 0 || role.Revision == 0 || !entities.Add(role.Entity))
                throw new InvalidDataException("Invalid expected control grant.");
    }

    static void ValidateGeometry(SimCommand command)
    {
        // Finite alone permits enormous coordinates whose distances/formation arithmetic overflow.
        static void Position(Vector3 p)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || MathF.Abs(p.X) > WorldLayout.HalfWorldSize || MathF.Abs(p.Z) > WorldLayout.HalfWorldSize || MathF.Abs(p.Y) > WorldLayout.WorldSize)
                throw new InvalidDataException("Command position is outside the world bounds.");
        }

        switch (command)
        {
            case MoveOrder move:
                Position(move.Target);
                break;
            case NewSquadCommand squad:
                Position(squad.At);
                break;
            case PlaceBuildings placement:
                if (placement.Plans is null || placement.Plans.Count > 2048)
                    throw new InvalidDataException("Invalid placement batch.");
                foreach (var plan in placement.Plans)
                {
                    if (plan is null)
                        throw new InvalidDataException("Empty placement.");
                    Position(plan.Position);
                    float norm = plan.Rotation.LengthSquared();
                    if (!float.IsFinite(norm) || MathF.Abs(norm - 1) > .01f)
                        throw new InvalidDataException("Placement rotation must be a unit quaternion.");
                }

                break;
        }
    }

    static void ValidateJson(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 16 });
        var containers = new Stack<JsonContainer>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > 100000)
                throw new InvalidDataException("Command has too many values.");
            if (reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.PropertyName) && containers.TryPeek(out var parent) && parent.Fields is null)
                if (++parent.Count > 4096 || reader.TokenType == JsonTokenType.Null)
                    throw new InvalidDataException("Invalid or oversized command array.");
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new() { Fields = new(StringComparer.Ordinal) });
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(new());
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (reader.ValueSpan.Length > 128 || !containers.Peek().Fields!.Add(reader.GetString()!))
                        throw new InvalidDataException("Invalid or duplicate command field.");
                    break;
                case JsonTokenType.String:
                    if (reader.ValueSpan.Length > 4096)
                        throw new InvalidDataException("Command string is too long.");
                    break;
            }
        }
    }

    sealed class JsonContainer
    {
        public HashSet<string>? Fields;
        public int Count;
    }

    sealed class KnownEnums : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsEnum;
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) => (JsonConverter)Activator.CreateInstance(typeof(KnownEnum<>).MakeGenericType(type))!;
    }

    sealed class KnownEnum<T> : JsonConverter<T> where T : struct, Enum
    {
        static readonly bool Flags = typeof(T).IsDefined(typeof(FlagsAttribute), false);
        static readonly ulong Mask = Enum.GetValues<T>().Aggregate(0ul, (value, item) => value | Bits(item));
        static ulong Bits(T value) => Enum.GetUnderlyingType(typeof(T)) == typeof(ulong) ? Convert.ToUInt64(value) : unchecked((ulong)Convert.ToInt64(value));
        static void Validate(T value)
        {
            if (Flags ? (Bits(value) & ~Mask) != 0 : !Enum.IsDefined(value))
                throw new JsonException("Unknown command enum value.");
        }

        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<T>(ref reader);
            Validate(value);
            return value;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            Validate(value);
            JsonSerializer.Serialize(writer, value);
        }
    }

    sealed class FiniteSingle : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            float value = reader.GetSingle();
            return float.IsFinite(value) ? value : throw new JsonException("Non-finite command number.");
        }

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            if (!float.IsFinite(value))
                throw new JsonException("Non-finite command number.");
            writer.WriteNumberValue(value);
        }
    }
}