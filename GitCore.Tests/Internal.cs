using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Internal
{
    [Fact]
    public void Stack()
    {
        Span<Guid> expected = stackalloc Guid[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            expected[i] = Guid.NewGuid();
        }
        Span<byte> actual = stackalloc byte[Unsafe.SizeOf<Guid>() * expected.Length];
        for (var chunkSize = 1; chunkSize < actual.Length; ++chunkSize)
        {
            actual.Clear();
            using StackStream ss = new(new MemoryStream(expected[^1].ToByteArray()));
            for (var i = expected.Length - 1; i-- > 0;)
            {
                ss.Push(expected[i].ToByteArray());
            }
            for (var read = 0; read < actual.Length; read += ss.Read(actual[read..Math.Min(read + chunkSize, actual.Length)])) { }
            Assert.True(actual.SequenceEqual(MemoryMarshal.Cast<Guid, byte>(expected)));
        }
    }


    [Fact]
    public void Inflate()
    {
        Span<Guid> expected = stackalloc Guid[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            expected[i] = Guid.NewGuid();
        }
        using MemoryStream deflated = new();
        for (var i = 0; i < expected.Length; ++i)
        {
            using ZLibStream zls = new(deflated, CompressionLevel.SmallestSize, true);
            zls.Write(expected[i].ToByteArray());
            zls.Flush();
        }
        deflated.Position = 0;
        using StackStream ss = new(deflated);
        Span<byte> actual = stackalloc byte[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            using ZLibStream zls = new(ss, CompressionMode.Decompress, true);
            var read = zls.Read(actual);
            Assert.Equal(16, read);
            Assert.Equal(expected[i], new(actual));
            ss.Push(zls.GetInputBuffer());
        }
        Assert.Equal(-1, ss.ReadByte());
    }
}