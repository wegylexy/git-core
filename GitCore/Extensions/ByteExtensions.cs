using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public static class ByteExtensions
{
    internal static int ToHex(this int c) => c + (c < 10 ? '0' : 'a' - 10);

    public static string ToHexString(this in ReadOnlySpan<byte> span)
    {
        Span<char> cs = stackalloc char[span.Length * 2];
        for (var i = 0; i < span.Length; ++i)
        {
            cs[i * 2] = (char)ToHex(span[i] >> 4);
            cs[i * 2 + 1] = (char)ToHex(span[i] & 0xF);
        }
        return new(cs);
    }

    public static string ToHexString(this byte[] bytes) =>
        ((ReadOnlySpan<byte>)bytes).ToHexString();

    public static string ToHexString(this in ReadOnlyMemory<byte> memory) =>
        memory.Span.ToHexString();

    public static void ToHexASCII(this in ReadOnlySpan<byte> span, Span<byte> ascii)
    {
        for (var i = 0; i < span.Length; ++i)
        {
            ascii[i * 2] = (byte)ToHex(span[i] >> 4);
            ascii[i * 2 + 1] = (byte)ToHex(span[i] & 0xF);
        }
    }

    public static void ToHexASCII(this byte[] bytes, Span<byte> ascii) =>
        ((ReadOnlySpan<byte>)bytes).ToHexASCII(ascii);

    public static void ToHexASCII(this in ReadOnlyMemory<byte> memory, Span<byte> ascii) =>
        memory.Span.ToHexASCII(ascii);

    public static byte[] ToHexASCII(this in ReadOnlySpan<byte> span)
    {
        var ascii = GC.AllocateUninitializedArray<byte>(span.Length * 2);
        span.ToHexASCII(ascii);
        return ascii;
    }

    public static byte[] ToHexASCII(this byte[] bytes) =>
        ((ReadOnlySpan<byte>)bytes).ToHexASCII();

    public static byte[] ToHexASCII(this in ReadOnlyMemory<byte> memory) =>
        memory.Span.ToHexASCII();

    internal static int ParseHex(this int ascii) => ascii - (ascii >= 'a' ? 'a' - 10 : ascii >= 'A' ? 'A' - 10 : '0') is < 0x10 and var c ? c :
        throw new ArgumentOutOfRangeException(nameof(ascii));

    public static void ParseHex(this in ReadOnlySpan<char> hex, Span<byte> bytes)
    {
        var length = hex.Length / 2;
        for (var i = 0; i < length; ++i)
        {
            bytes[i] = (byte)((ParseHex(hex[i * 2]) << 4) | ParseHex(hex[i * 2 + 1]));
        }
    }

    public static void ParseHex(this in ReadOnlyMemory<char> hex, Span<byte> bytes) =>
        hex.Span.ParseHex(bytes);

    public static void ParseHex(this string hex, Span<byte> bytes) =>
        ((ReadOnlySpan<char>)hex).ParseHex(bytes);

    public static byte[] ParseHex(this in ReadOnlySpan<char> hex)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(hex.Length / 2);
        hex.ParseHex(bytes);
        return bytes;
    }

    public static byte[] ParseHex(this in ReadOnlyMemory<char> hex) =>
        hex.Span.ParseHex();

    public static byte[] ParseHex(this string hex) =>
        ((ReadOnlySpan<char>)hex).ParseHex();

    public static void ParseHex(this in ReadOnlySpan<byte> hex, Span<byte> bytes)
    {
        var length = hex.Length / 2;
        for (var i = 0; i < length; ++i)
        {
            bytes[i] = (byte)((ParseHex(hex[i * 2]) << 4) | ParseHex(hex[i * 2 + 1]));
        }
    }

    public static void ParseHex(this byte[] hex, Span<byte> bytes) =>
        ((ReadOnlySpan<byte>)hex).ParseHex(bytes);

    public static void ParseHex(this in ReadOnlyMemory<byte> hex, Span<byte> bytes) =>
        hex.Span.ParseHex(bytes);

    public static byte[] ParseHex(this in ReadOnlySpan<byte> hex)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(hex.Length / 2);
        hex.ParseHex(bytes);
        return bytes;
    }

    public static byte[] ParseHex(this byte[] hex) =>
        ((ReadOnlySpan<byte>)hex).ParseHex();

    public static byte[] ParseHex(this in ReadOnlyMemory<byte> hex) =>
        hex.Span.ParseHex();

    public static async ValueTask CopyToAsync(this ReadOnlySequence<byte> sequence, Stream stream, CancellationToken cancellationToken = default)
    {
        foreach (var memory in sequence)
        {
            await stream.WriteAsync(memory, cancellationToken);
        }
    }

    public static void CopyTo(this ReadOnlySequence<byte> sequence, Stream stream)
    {
        foreach (var memory in sequence)
        {
            stream.Write(memory.Span);
        }
    }

    public static async Task<ReadOnlySequence<byte>> ToSequenceAsync(this Stream stream, int segmentSize = 4096, CancellationToken cancellationToken = default)
    {
        Stack<ReadOnlyMemory<byte>> segments = new();
        long runningIndex = 0;
        for (; ; )
        {
            var segment = GC.AllocateUninitializedArray<byte>(segmentSize);
            var read = await stream.ReadAsync(segment, cancellationToken);
            if (read == 0)
            {
                break;
            }
            segments.Push(segment.AsMemory(0, read));
            runningIndex += read;
        }
        if (segments.TryPop(out var last))
        {
            BytesSegment end = new(last, null, runningIndex -= last.Length), start = end;
            while (segments.TryPop(out var previous))
            {
                start = new(previous, start, runningIndex -= previous.Length);
            }
            return new(start, 0, end, end.Memory.Length);
        }
        else
        {
            return ReadOnlySequence<byte>.Empty;
        }
    }
}