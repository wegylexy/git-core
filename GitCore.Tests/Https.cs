﻿using Xunit;
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
            var r = e.Current.Key;
            if (r.StartsWith("refs/heads/"))
            {
                r = string.Concat("branch ", r.AsSpan(11));
            }
            else if (r.StartsWith("refs/tags/"))
            {
                r = string.Concat("tag ", r.AsSpan(10));
            }
            _output.WriteLine($"{r} -> commit {e.Current.Value.ToHexString()}");
            Assert.False(e.Current.Key.Contains(' '));
            Assert.False(e.Current.Key.Contains('\n'));
        }
        while (await e.MoveNextAsync());
    }

    [Theory]
    [InlineData("f0d3a70ceaa69fb70811f58254dc738e0f939eac")]
    [InlineData("8c0e16d92cfa0c59b4c3c1dabc52b56f66852ae6")]
    public async Task UploadPackAsync(string leafHex)
    {
        ReadOnlyMemory<byte> leaf = leafHex.ParseHex(), have = "985f7c92b19e5de0f28fefb96a9d004d6c4f4841".ParseHex();
        using HttpClient hc = new()
        {
            BaseAddress = new("https://github.com/wegylexy/git-core.git/")
        };
        using var r = await hc.PostUploadPackAsync(new(
            want: new[] { leaf },
            depth: 1,
            have: new[] { have }
        ));
        if (r.Acknowledged.Any())
        {
            foreach (var h in r.Acknowledged)
            {
                _output.WriteLine($"ACK {h.ToHexString()}");
            }
        }
        else
        {
            _output.WriteLine("NAK");
        }
        foreach (var h in r.Shallow)
        {
            _output.WriteLine($"shallow {h.ToHexString()}");
        }
        foreach (var h in r.Unshallow)
        {
            _output.WriteLine($"unshallow {h.ToHexString()}");
        }
        _output.WriteLine($"PACK: {await r.Pack.CountAsync()} objects");
        Dictionary<ReadOnlyMemory<byte>, UnpackedObject> os = new(ByteROMEqualityComparer.Instance);
        HashSet<ReadOnlyMemory<byte>> ts = new(ByteROMEqualityComparer.Instance), hs = new(ByteROMEqualityComparer.Instance);
        await foreach (var o in r.Pack)
        {
            var co = o;
        Triage:
            Assert.False(co.Hash.Span.SequenceEqual(have.Span));
            if (co == o)
            {
                _output.WriteLine(co.ToString());
            }
            else
            {
                _output.WriteLine("\t" + co.ToString());
            }
            if (!co.IsDelta)
            {
                os.Add(co.Hash, co);
            }
            switch (co.Type)
            {
                case ObjectType.Blob:
                    Assert.Equal(co.Data.Length, co.AsStream().Length);
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
                    if (os.TryGetValue(co.Hash, out var b))
                    {
                        co = co.Delta(b);
                        _output.WriteLine(hs.Contains(co.Hash) ? "\t(seen above)" : "\t(unseen above)");
                        goto Triage;
                    }
                    else
                    {
                        _output.WriteLine("\t(not in pack)");
                    }
                    break;
                case ObjectType.Commit:
                    {
                        var c = co.ToCommitContent();
                        _output.WriteLine("\ttree " + c.Tree.ToHexString());
                        if (!c.Parent.IsEmpty)
                        {
                            _output.WriteLine("\tparent " + c.Parent.ToHexString());
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException("Unexpected type");
            }
        }
        Assert.Contains(leaf, (IDictionary<ReadOnlyMemory<byte>, UnpackedObject>)os); // may fail due to external reference
    }
}