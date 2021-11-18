using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace FlyByWireless.GitCore;

public sealed class ByteROMEqualityComparer : IEqualityComparer<ReadOnlyMemory<byte>>
{
    public static ByteROMEqualityComparer Instance { get; } = new();

    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceEqual(y.Span);

    public int GetHashCode([DisallowNull] ReadOnlyMemory<byte> obj) => MemoryMarshal.Cast<byte, int>(obj.Span)[0];
}