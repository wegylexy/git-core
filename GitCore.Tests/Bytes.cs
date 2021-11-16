using System.Text;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Bytes
{
    [Fact]
    public void Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var hex = string.Join(string.Empty, bytes.Select(b => b.ToString("x2")));
        Assert.Equal(hex, bytes.ToHexString());
        Assert.Equal(bytes, hex.ParseHex());
        var ascii = Encoding.ASCII.GetBytes(hex);
        Assert.Equal(ascii, bytes.ToHexASCII());
        Assert.Equal(bytes, ascii.ParseHex());
    }
}