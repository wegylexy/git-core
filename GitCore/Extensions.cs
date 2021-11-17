using System.Buffers;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web;

[assembly: InternalsVisibleTo("FlyByWireless.GitCore.Tests")]

namespace FlyByWireless.GitCore;

public static class ZLibExtensions
{
    private static readonly Func<ZLibStream, ReadOnlyMemory<byte>> _getInputBuffer;

    static ZLibExtensions()
    {
        var stream = Expression.Parameter(typeof(ZLibStream));
        var _deflateStream = Expression.Field(stream, "_deflateStream");
        var _zlibStream = Expression.Field(Expression.Field(_deflateStream, "_inflater"), "_zlibStream");
        var _buffer = (Func<ZLibStream, byte[]>)Expression.Lambda(Expression.Field(_deflateStream, "_buffer"), stream).Compile();
        var AvailIn = (Func<ZLibStream, uint>)Expression.Lambda(Expression.Property(_zlibStream, "AvailIn"), stream).Compile();
        var NextIn = (Func<ZLibStream, nint>)Expression.Lambda(Expression.Property(_zlibStream, "NextIn"), stream).Compile();
        _getInputBuffer = stream =>
            (int)AvailIn(stream) is not 0 and var availIn && _buffer(stream) is not null and var buffer ?
            new(buffer, (int)(NextIn(stream) - Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0)), availIn) :
            default;
    }

    internal static ReadOnlyMemory<byte> GetInputBuffer(this ZLibStream stream) =>
        _getInputBuffer(stream);
}

public static class HttpExtensions
{
    public static void Authenticate<T>(this T client, string username, string password) where T : HttpClient =>
        client.DefaultRequestHeaders.Authorization = new("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{HttpUtility.UrlEncode(username)}:{HttpUtility.UrlEncode(password)}"))
        );

    public static async Task<UploadPackAdvertisement> GetUploadPackAsync(this HttpClient client, HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead, CancellationToken cancellationToken = default) =>
        new(await client.GetAsync("info/refs?service=git-upload-pack", completionOption, cancellationToken));

    public static async Task<HttpResponseMessage> PostUploadPackAsync(this HttpClient client, UploadPackRequest request, HttpCompletionOption completionOption, CancellationToken cancellationToken = default)
    {
        var r = await client.SendAsync(new(HttpMethod.Post, "git-upload-pack")
        {
            Content = request
        }, completionOption, cancellationToken);
        if (r.EnsureSuccessStatusCode().Content.Headers.ContentType is not { MediaType: "application/x-git-upload-pack-result" })
        {
            throw new InvalidOperationException("Unexpected media type");
        }
        return r;
    }
}

public static class ByteExtensions
{
    public static string ToHexString(this in ReadOnlySpan<byte> span)
    {
        static char C(int c) => (char)(c + (c < 10 ? '0' : 'a' - 10));
        Span<char> cs = stackalloc char[span.Length * 2];
        for (var i = 0; i < span.Length; ++i)
        {
            cs[i * 2] = C(span[i] >> 4);
            cs[i * 2 + 1] = C(span[i] & 0xF);
        }
        return new(cs);
    }

    public static string ToHexString(this byte[] bytes) =>
        ((ReadOnlySpan<byte>)bytes).ToHexString();

    public static string ToHexString(this in ReadOnlyMemory<byte> memory) =>
        memory.Span.ToHexString();

    public static void ToHexASCII(this in ReadOnlySpan<byte> span, Span<byte> ascii)
    {
        static byte A(int c) => (byte)(c + (c < 10 ? '0' : 'a' - 10));
        for (var i = 0; i < span.Length; ++i)
        {
            ascii[i * 2] = A(span[i] >> 4);
            ascii[i * 2 + 1] = A(span[i] & 0xF);
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

    public static void ParseHex(this in ReadOnlySpan<char> hex, Span<byte> bytes)
    {
        static int C(char a) => a - (a >= 'a' ? 'a' - 10 : a >= 'A' ? 'A' - 10 : '0') is < 0x10 and var c ? c :
            throw new ArgumentOutOfRangeException(nameof(hex));
        var length = hex.Length / 2;
        for (var i = 0; i < length; ++i)
        {
            bytes[i] = (byte)((C(hex[i * 2]) << 4) | C(hex[i * 2 + 1]));
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
        static int C(byte a) => a - (a >= 'a' ? 'a' - 10 : a >= 'A' ? 'A' - 10 : '0') is < 0x10 and var c ? c :
            throw new ArgumentOutOfRangeException(nameof(hex));
        var length = hex.Length / 2;
        for (var i = 0; i < length; ++i)
        {
            bytes[i] = (byte)((C(hex[i * 2]) << 4) | C(hex[i * 2 + 1]));
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

    public static async Task<ReadOnlyMemory<byte>> HashObjectAsync(this Stream stream, ObjectType type = ObjectType.Blob, string hashAlgorithm = nameof(SHA1), CancellationToken cancellationToken = default)
    {
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        using StackStream ss = new(stream, true);
        ss.Push(Encoding.ASCII.GetBytes(FormattableString.Invariant(@$"{type switch
        {
            ObjectType.Commit => "commit",
            ObjectType.Tree => "tree",
            ObjectType.Blob => "blob",
            ObjectType.Tag => "tag",
            _ => throw new InvalidDataException("Invalid object type")
        }} {stream.Length}")));
        return await ha.ComputeHashAsync(ss, cancellationToken);
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