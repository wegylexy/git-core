using System.IO.Compression;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

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