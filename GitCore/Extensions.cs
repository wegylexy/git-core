using System.Buffers;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("FlyByWireless.GitCore.Tests")]

namespace FlyByWireless.GitCore;

public static class GitCoreExtensions
{
    private static readonly Func<ZLibStream, ReadOnlyMemory<byte>> _getInputBuffer;

    static GitCoreExtensions()
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

    public static Task<HttpResponseMessage> PostAsync(this HttpClient client, HttpCompletionOption completionOption, CancellationToken cancellationToken = default) =>
        client.SendAsync(new(HttpMethod.Post, null as Uri), completionOption, cancellationToken);

    public static string ToHexString(this in ReadOnlySpan<byte> span)
    {
        const string alphabet = "0123456789abcdef";
        Span<char> cs = stackalloc char[span.Length * 2];
        for (var i = 0; i < span.Length; ++i)
        {
            cs[i * 2] = alphabet[span[i] >> 4];
            cs[i * 2 + 1] = alphabet[span[i] & 0xF];
        }
        return new(cs);
    }

    public static string ToHexString(this byte[] bytes) => ((ReadOnlySpan<byte>)bytes).ToHexString();

    public static string ToHexString(this in ReadOnlyMemory<byte> memory) =>
        memory.Span.ToHexString();

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
}