using System.Buffers;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.GitCore.Tests;

public class Trees
{
    private readonly ITestOutputHelper _output;

    public Trees(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("f0d3a70ceaa69fb70811f58254dc738e0f939eac")]
    [InlineData("d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d")]
    public async Task TreeAsync(string treeHex)
    {
        var count = 0;
        await foreach (var e in ReadOnlyTree.EnumerateAsync(Path.Join("../../../../.git/objects", treeHex.AsSpan(0, 2), treeHex.AsSpan(2)), true))
        {
            _output.WriteLine(e.ToString());
            ++count;
            if (treeHex == "f0d3a70ceaa69fb70811f58254dc738e0f939eac")
            {
                switch (e.Path)
                {
                    case ".gitattributes":
                        Assert.Equal("1ff0c423042b46cb1d617b81efb715defbe8054d", e.Hash.ToHexString());
                        break;
                    case ".gitignore":
                        Assert.Equal("9491a2fda28342ab358eaf234e1afe0c07a53d62", e.Hash.ToHexString());
                        break;
                }
            }
        }
        Assert.True(count > 1);
    }

    [Theory]
    [InlineData("f0d3a70ceaa69fb70811f58254dc738e0f939eac")]
    [InlineData("d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d")]
    public async Task RemoteTreeAsync(string treeHex)
    {
        using HttpClient hc = new()
        {
            BaseAddress = new("https://github.com/wegylexy/git-core.git/")
        };
        using var hrm = await hc.PostUploadPackAsync(new(treeHex.ParseHex()), HttpCompletionOption.ResponseHeadersRead);
        using var s = await hrm.Content.ReadAsStreamAsync();
        {
            var a = ArrayPool<byte>.Shared.Rent(8);
            try
            {
                for (var read = 0; read < 8;)
                {
                    var r = await s.ReadAsync(a.AsMemory(read, 8 - read));
                    if (r == 0)
                    {
                        throw new EndOfStreamException();
                    }
                    read += r;
                }
                Assert.Equal(Encoding.ASCII.GetBytes("0008NAK\n"), a.Take(8));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(a);
            }
        }
        ReadOnlyPack rop = new(s);
        List<UnpackedObject> os = new();
        await foreach (var o in rop)
        {
            _output.WriteLine(o.ToString());
            switch (o.Type)
            {
                case ObjectType.Tree:
                    {
                        StackStream ss = new(o.AsStream());
                        ss.Push(o.Prolog);
                        await foreach (var e in new ReadOnlyTree(ss))
                        {
                            _output.WriteLine("\t" + e.ToString());
                        }
                    }
                    break;
                case ObjectType.ReferenceDelta:
                    {
                        var bo = os.First(b => b.Hash.Span.SequenceEqual(o.Hash.Span));
                        MemoryStream ms = new();
                        var hash = await o.DeltaAsync(bo.Type, bo.AsStream(), ms);
                        ms.TryGetBuffer(out var a);
                        UnpackedObject d = new(bo.Type, ms.Length, new(a.Array!, a.Offset, a.Count), hash);
                        os.Add(d);
                        _output.WriteLine("\t" + d.ToString());
                    }
                    break;
                case ObjectType.Blob:
                    os.Add(o);
                    break;
                default:
                    throw new NotSupportedException("Unexpected type");
            }
        }
    }
}