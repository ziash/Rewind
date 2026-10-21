using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using OpenAPI.Net.Helpers;

namespace Rewind.OpenApi;

/// <summary>
/// cTrader Open API framing (spec §10): each message is a ProtoMessage envelope (payload type, payload bytes, optional
/// clientMsgId), sent as a 4-byte big-endian length followed by the envelope's bytes.
/// </summary>
public static class Protocol
{
    private static readonly Lazy<Dictionary<uint, MessageParser>> Parsers = new(BuildTable);
    private static readonly ConcurrentDictionary<Type, uint> PayloadTypes = new();

    public static async Task<ProtoMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, ct))
            return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > 10_000_000)
            throw new InvalidDataException($"Message length {length} is not plausible");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct))
            return null;
        return ProtoMessage.Parser.ParseFrom(body);
    }

    public static byte[] Frame(IMessage message, string? clientMsgId)
    {
        var envelope = new ProtoMessage { PayloadType = PayloadTypeOf(message), Payload = message.ToByteString() };
        if (!string.IsNullOrEmpty(clientMsgId))
            envelope.ClientMsgId = clientMsgId;
        var body = envelope.ToByteArray();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>
    /// The message inside an envelope, or null if its type is unknown. The library's MessageFactory first, then a table
    /// of every generated message class (copied from Trader's src/Trader.Core/OpenApi/PayloadDecoder.cs, commit a41db3d:
    /// the 2022 library lacks some mappings).
    /// </summary>
    public static IMessage? Decode(ProtoMessage envelope) =>
        MessageFactory.GetMessage(envelope) ?? (Parsers.Value.TryGetValue(envelope.PayloadType, out var p) ? p.ParseFrom(envelope.Payload) : null);

    private static uint PayloadTypeOf(IMessage message) => PayloadTypes.GetOrAdd(message.GetType(), t =>
        t.GetProperty("PayloadType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(Activator.CreateInstance(t)) is Enum e
            ? Convert.ToUInt32(e)
            : throw new InvalidOperationException($"{t.Name} has no payload type"));

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private static Dictionary<uint, MessageParser> BuildTable()
    {
        var table = new Dictionary<uint, MessageParser>();
        foreach (var type in typeof(ProtoOAApplicationAuthReq).Assembly.GetTypes())
        {
            if (!typeof(IMessage).IsAssignableFrom(type) || type.IsAbstract ||
                type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not MessageParser parser ||
                type.GetProperty("PayloadType", BindingFlags.Public | BindingFlags.Instance) is not { } payloadProperty)
                continue;
            if (payloadProperty.GetValue(Activator.CreateInstance(type)) is Enum e)
                table.TryAdd(Convert.ToUInt32(e), parser);
        }
        return table;
    }
}
