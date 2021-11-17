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
        await foreach (var e in AsyncTree.EnumerateAsync(Path.Join("../../../../.git/objects", treeHex.AsSpan(0, 2), treeHex.AsSpan(2)), true))
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
        ReadOnlyMemory<byte> want = treeHex.ParseHex(), have = "1ff0c423042b46cb1d617b81efb715defbe8054d".ParseHex();
        using HttpClient hc = new()
        {
            BaseAddress = new("https://github.com/wegylexy/git-core.git/")
        };
        using var r = await hc.PostUploadPackAsync(new(new[] { want }, new[] { have }));
        List<UnpackedObject> os = new();
        List<ReadOnlyMemory<byte>> ts = new(), hs = new();
        await foreach (var o in r.Pack)
        {
            var co = o;
        Triage:
            Assert.False(co.Hash.Span.SequenceEqual(have.Span));
            _output.WriteLine(co.ToString());
            switch (co.Type)
            {
                case ObjectType.Blob:
                    os.Add(co);
                    break;
                case ObjectType.Tree:
                    ts.Add(co.Hash);
                    await foreach (var e in co.AsTree())
                    {
                        _output.WriteLine("\t" + e.ToString());
                        hs.Add(e.Hash);
                    }
                    break;
                case ObjectType.ReferenceDelta:
                    {
                        var b = os.FirstOrDefault(b => b.Hash.Span.SequenceEqual(co.Hash.Span));
                        if (b.Type == default)
                        {
                            _output.WriteLine("\t(reference not found)");
                        }
                        else
                        {
                            co = co.Delta(b);
                            Assert.Contains(hs, h => h.Span.SequenceEqual(co.Hash.Span));
                            os.Add(co);
                            goto Triage;
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException("Unexpected type");
            }
        }
        Assert.Contains(ts, h => h.Span.SequenceEqual(want.Span));
    }
}