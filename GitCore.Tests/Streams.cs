using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Streams
{
    [Fact]
    public async Task SequenceAsync()
    {
        var expected = Guid.NewGuid().ToByteArray();
        SequenceStream ss = new(new(expected));
        {
            var actual = new byte[ss.Length];
            ss.Read(actual);
            Assert.Equal(expected, actual);
        }
        ss.Position = 0;
        {
            var s = await ss.ToSequenceAsync(4);
            var actual = new byte[s.Length];
            s.Slice(0).CopyTo(actual);
            Assert.Equal(expected, actual);
        }
    }

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

    [Theory]
    [InlineData(false, 0x20000)]
    [InlineData(true, 0x20000)]
    public void TextInput(bool alwaysConvert, int size)
    {
        static IEnumerable<byte> E()
        {
            for (var i = 0; ;)
            {
                for (var j = i; j-- > 0;)
                {
                    yield return 32;
                }
                yield return 13;
                yield return 10;
            }
        }
        using MemoryStream ms = new(E().Take(size).ToArray());
        using TextInputStream tis = new(ms, alwaysConvert);
        var buffer = GC.AllocateUninitializedArray<byte>(size);
        var read = 0;
        while (read < buffer.Length)
        {
            var r = tis.Read(buffer.AsSpan(read, buffer.Length - read));
            if (r == 0)
            {
                break;
            }
            read += r;
        }
        Assert.DoesNotContain<byte>(13, buffer.Take(read - 1));
    }

    [Theory]
    [InlineData(false, 0x20000)]
    [InlineData(true, 0x20000)]
    public async Task TextInputAsync(bool alwaysConvert, int size)
    {
        static IEnumerable<byte> E()
        {
            for (var i = 0; ;)
            {
                for (var j = i; j-- > 0;)
                {
                    yield return 32;
                }
                yield return 13;
                yield return 10;
            }
        }
        using MemoryStream ms = new(E().Take(size).ToArray());
        using TextInputStream tis = new(ms, alwaysConvert);
        var buffer = GC.AllocateUninitializedArray<byte>(size);
        var read = 0;
        while (read < buffer.Length)
        {
            var r = await tis.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (r == 0)
            {
                break;
            }
            read += r;
        }
        Assert.DoesNotContain<byte>(13, buffer.Take(read - 1));
    }
}