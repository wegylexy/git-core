using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.GitCore.Tests;

public class Https
{
    const string AuthTestUrl = "https://authenticationtest.com/HTTPAuth/";

    private readonly ITestOutputHelper _output;

    public Https(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AnonymousAsync()
    {
        using HttpClient hc = new()
        {
            BaseAddress = new Uri(AuthTestUrl)
        };
        using var r = await hc.GetAsync(null as Uri);
        _output.WriteLine(r.StatusCode.ToString());
        Assert.False(r.IsSuccessStatusCode);
    }

    [Fact]
    public async Task BasicAsync()
    {
        using HttpClient hc = new()
        {
            BaseAddress = new Uri(AuthTestUrl)
        };
        hc.Authenticate("user", "pass");
        using var r = await hc.GetAsync(null as Uri);
        _output.WriteLine(r.StatusCode.ToString());
        Assert.True(r.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UploadPackAdvertisementAsync()
    {
        using HttpClient hc = new()
        {
            BaseAddress = new("https://github.com/wegylexy/git-core.git/")
        };
        var upa = await hc.GetUploadPackAsync();
        var e = upa.GetAsyncEnumerator();
        await e.MoveNextAsync();
        _output.WriteLine(string.Join(' ', upa.Capabilities));
        Assert.Superset(new HashSet<string>
        {
            "no-progress",
            "no-done"
        }, upa.Capabilities);
        do
        {
            _output.WriteLine($"{e.Current.Key} -> {e.Current.Value.ToHexString()}");
            Assert.False(e.Current.Key.Contains(' '));
            Assert.False(e.Current.Key.Contains('\n'));
        }
        while (await e.MoveNextAsync());
    }
}