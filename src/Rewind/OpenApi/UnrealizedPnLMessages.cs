using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Rewind.OpenApi;

// Copied from Trader src/Trader.Core/OpenApi/UnrealizedPnLMessages.cs (commit 34ae014), for the whole-account run (spec §7.2).
// ProtoOAGetPositionUnrealizedPnLReq / Res (payload types 2187 / 2188) came after the 2022 cTrader.OpenAPI.Net library, so they
// are written out here by hand from Spotware's OpenApiMessages.proto. The library builds the envelope from the
// PayloadType property (by reflection) and the bytes from WriteTo; PayloadDecoder knows the reply's parser.

/// <summary>Asks for the unrealized profit/loss of every open position of the account.</summary>
public sealed class ProtoOAGetPositionUnrealizedPnLReq : IMessage<ProtoOAGetPositionUnrealizedPnLReq>
{
    public const uint Type = 2187;

    public static ProtoOAGetPositionUnrealizedPnLReq From(ByteString payload)
    {
        var r = new ProtoOAGetPositionUnrealizedPnLReq();
        r.MergeFrom(payload.CreateCodedInput());
        return r;
    }

    public uint PayloadType => Type;
    public long CtidTraderAccountId { get; set; }

    public MessageDescriptor Descriptor => null!;

    public void WriteTo(CodedOutputStream output)
    {
        output.WriteRawTag(8);
        output.WriteEnum((int)Type);
        output.WriteRawTag(16);
        output.WriteInt64(CtidTraderAccountId);
    }

    public int CalculateSize() => 2 + CodedOutputStream.ComputeEnumSize((int)Type) + CodedOutputStream.ComputeInt64Size(CtidTraderAccountId);

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 16)
                CtidTraderAccountId = input.ReadInt64();
            else
                input.SkipLastField();
        }
    }

    public void MergeFrom(ProtoOAGetPositionUnrealizedPnLReq message) => CtidTraderAccountId = message.CtidTraderAccountId;
    public ProtoOAGetPositionUnrealizedPnLReq Clone() => new() { CtidTraderAccountId = CtidTraderAccountId };
    public bool Equals(ProtoOAGetPositionUnrealizedPnLReq? other) => other?.CtidTraderAccountId == CtidTraderAccountId;
}

/// <summary>One position's unrealized result, in the account currency × 10^MoneyDigits of the reply.</summary>
public sealed record PositionUnrealizedPnL(long PositionId, long Gross, long Net);

/// <summary>The reply: each open position's gross and net (after commission and swap) unrealized result.</summary>
public sealed class ProtoOAGetPositionUnrealizedPnLRes : IMessage<ProtoOAGetPositionUnrealizedPnLRes>
{
    public const uint Type = 2188;

    public static MessageParser<ProtoOAGetPositionUnrealizedPnLRes> Parser { get; } = new(() => new ProtoOAGetPositionUnrealizedPnLRes());

    public uint PayloadType => Type;
    public long CtidTraderAccountId { get; set; }
    public List<PositionUnrealizedPnL> Positions { get; } = [];
    public uint MoneyDigits { get; set; }

    public MessageDescriptor Descriptor => null!;

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 16:
                    CtidTraderAccountId = input.ReadInt64();
                    break;
                case 26:
                    Positions.Add(ReadPosition(input.ReadBytes().CreateCodedInput()));
                    break;
                case 32:
                    MoneyDigits = input.ReadUInt32();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    private static PositionUnrealizedPnL ReadPosition(CodedInputStream input)
    {
        long id = 0, gross = 0, net = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8: id = input.ReadInt64(); break;
                case 16: gross = input.ReadInt64(); break;
                case 24: net = input.ReadInt64(); break;
                default: input.SkipLastField(); break;
            }
        }
        return new PositionUnrealizedPnL(id, gross, net);
    }

    // Only ever received; writing is for tests (the replay broker and unit tests build replies with it).
    public void WriteTo(CodedOutputStream output)
    {
        output.WriteRawTag(8);
        output.WriteEnum((int)Type);
        output.WriteRawTag(16);
        output.WriteInt64(CtidTraderAccountId);
        foreach (var p in Positions)
        {
            output.WriteRawTag(26);
            output.WriteLength(PositionSize(p));
            output.WriteRawTag(8);
            output.WriteInt64(p.PositionId);
            output.WriteRawTag(16);
            output.WriteInt64(p.Gross);
            output.WriteRawTag(24);
            output.WriteInt64(p.Net);
        }
        output.WriteRawTag(32);
        output.WriteUInt32(MoneyDigits);
    }

    private static int PositionSize(PositionUnrealizedPnL p) =>
        3 + CodedOutputStream.ComputeInt64Size(p.PositionId) + CodedOutputStream.ComputeInt64Size(p.Gross) + CodedOutputStream.ComputeInt64Size(p.Net);

    public int CalculateSize() =>
        2 + CodedOutputStream.ComputeEnumSize((int)Type) + CodedOutputStream.ComputeInt64Size(CtidTraderAccountId)
        + Positions.Sum(p => 1 + CodedOutputStream.ComputeLengthSize(PositionSize(p)) + PositionSize(p))
        + 1 + CodedOutputStream.ComputeUInt32Size(MoneyDigits);

    public void MergeFrom(ProtoOAGetPositionUnrealizedPnLRes message)
    {
        CtidTraderAccountId = message.CtidTraderAccountId;
        Positions.AddRange(message.Positions);
        MoneyDigits = message.MoneyDigits;
    }

    public ProtoOAGetPositionUnrealizedPnLRes Clone()
    {
        var c = new ProtoOAGetPositionUnrealizedPnLRes { CtidTraderAccountId = CtidTraderAccountId, MoneyDigits = MoneyDigits };
        c.Positions.AddRange(Positions);
        return c;
    }

    public bool Equals(ProtoOAGetPositionUnrealizedPnLRes? other) =>
        other is not null && other.CtidTraderAccountId == CtidTraderAccountId && other.MoneyDigits == MoneyDigits && other.Positions.SequenceEqual(Positions);
}
