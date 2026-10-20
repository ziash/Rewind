using System.Buffers.Binary;
using SharpCompress.Compressors.LZMA;

namespace Rewind.Download;

/// <summary>One minute of one side (bid or ask), prices as whole numbers (price × factor).</summary>
internal readonly record struct MinuteBar(DateTime TimeUtc, long Open, long High, long Low, long Close, double Volume);

/// <summary>A .bi5 file that is not what spec §4 describes.</summary>
internal sealed class Bi5Exception(string message) : Exception(message);

/// <summary>
/// Reads a Dukascopy minute-candle file (spec §4): LZMA-compressed ("lzma alone" format: 5 property bytes, 8 bytes
/// uncompressed size, data), inside one 24-byte big-endian record per minute: seconds since 00:00 UTC (int32),
/// open, close, low, high (int32, price × factor), volume (float32). An empty file means no data that day.
/// </summary>
internal static class Bi5
{
    private const int RecordSize = 24;
    private const int HeaderSize = 13;

    public static List<MinuteBar> Read(byte[] file, DateOnly day)
    {
        if (file.Length == 0)
            return [];
        if (file.Length < HeaderSize)
            throw new Bi5Exception($"file too short ({file.Length} bytes)");

        var data = Decompress(file);
        if (data.Length % RecordSize != 0)
            throw new Bi5Exception($"{data.Length} bytes is not a whole number of {RecordSize}-byte records");

        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var bars = new List<MinuteBar>(data.Length / RecordSize);
        for (var i = 0; i < data.Length; i += RecordSize)
        {
            var r = data.AsSpan(i, RecordSize);
            var seconds = BinaryPrimitives.ReadInt32BigEndian(r);
            int open = BinaryPrimitives.ReadInt32BigEndian(r[4..]), close = BinaryPrimitives.ReadInt32BigEndian(r[8..]),
                low = BinaryPrimitives.ReadInt32BigEndian(r[12..]), high = BinaryPrimitives.ReadInt32BigEndian(r[16..]);
            var volume = BinaryPrimitives.ReadSingleBigEndian(r[20..]);

            if (seconds < 0 || seconds >= 86_400 || seconds % 60 != 0)
                throw new Bi5Exception($"record {i / RecordSize}: time {seconds} s is not a minute of the day");
            if (low <= 0 || low > Math.Min(open, close) || high < Math.Max(open, close))
                throw new Bi5Exception($"record {i / RecordSize}: open {open} close {close} low {low} high {high} do not fit together");
            bars.Add(new MinuteBar(start.AddSeconds(seconds), open, high, low, close, volume));
        }
        return bars;
    }

    private static byte[] Decompress(byte[] file)
    {
        var properties = file[..5];
        var size = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(5, 8));
        using var input = new MemoryStream(file, HeaderSize, file.Length - HeaderSize);
        using var lzma = LzmaStream.Create(properties, input, file.Length - HeaderSize, size, leaveOpen: false);
        using var output = new MemoryStream();
        try
        {
            lzma.CopyTo(output);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new Bi5Exception($"cannot decompress ({ex.Message})");
        }
        return output.ToArray();
    }
}
