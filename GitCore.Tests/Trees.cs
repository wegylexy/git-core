using System.IO.Compression;
using System.Security.Cryptography;
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
        await foreach (var e in Tree.EnumerateAsync(Path.Join("../../../../.git/objects", treeHex.AsSpan(0, 2), treeHex.AsSpan(2)), true))
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
}