using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class IO
{
    [Fact]
    public void Junction()
    {
        var temp = Path.GetTempPath();
        DirectoryInfo a = new(Path.Join(temp, Guid.NewGuid().ToString()));
        Assert.False(a.Exists);
        try
        {
            var b = Path.Join(temp, Guid.NewGuid().ToString());
            a.CreateJunctionPoint(b);
            Assert.Equal(b, a.LinkTarget);
        }
        finally
        {
            try
            {
                a.Delete(true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}