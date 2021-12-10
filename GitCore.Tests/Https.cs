using System.Buffers;
using System.IO.Compression;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.GitCore.Tests;

public class Https
{
    private readonly ITestOutputHelper _output;

    public Https(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AnonymousAsync()
    {
        using HttpClient hc = new()
        {
            BaseAddress = new Uri("https://authenticationtest.com/HTTPAuth/")
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
            BaseAddress = new Uri("https://authenticationtest.com/HTTPAuth/")
        };
        hc.Authorize("user", "pass");
        using var r = await hc.GetAsync(null as Uri);
        _output.WriteLine(r.StatusCode.ToString());
        Assert.True(r.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UserInfoAsync()
    {
        using HttpClient hc = new(new AuthorizingHttpClientHandler())
        {
            BaseAddress = new Uri("https://user:pass@authenticationtest.com/HTTPAuth/")
        };
        using var r = await hc.GetAsync(null as Uri);
        _output.WriteLine(r.StatusCode.ToString());
        Assert.True(r.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ZipAsync()
    {
        ZipArchive zip;
        {
            using HttpClient hc = new();
            using var r = await hc.GetAsync("https://github.com/wegylexy/git-core/archive/refs/heads/master.zip", HttpCompletionOption.ResponseHeadersRead);
            _output.WriteLine(r.StatusCode.ToString());
            r.EnsureSuccessStatusCode();
            _output.WriteLine($"Chunked: {r.Headers.TransferEncodingChunked is true}");
            _output.WriteLine($"Size: {r.Content.Headers.ContentLength?.ToString() ?? "unknown"}");
            zip = await r.Content.ReadAsZipAsync(progress: read => _output.WriteLine($"Read: {read}"));
        }
        using (zip)
        {
            foreach (var e in zip.Entries)
            {
                _output.WriteLine($"{e.FullName}\t{e.CompressedLength}/{e.Length}");
            }
        }
    }

    [Fact]
    public async Task UploadPackAdvertisementAsync()
    {
        using HttpClient hc = new();
        var upa = await hc.GetUploadPackAsync(new("https://github.com/wegylexy/git-core.git/"));
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
            if (r == "HEAD")
            {
                _output.WriteLine($"HEAD -> commit {e.Current.Value.ToHexString()}");
            }
            else if (r.StartsWith("refs/heads/"))
            {
                _output.WriteLine($"branch {r.AsSpan(11)} -> commit {e.Current.Value.ToHexString()}");
            }
            else if (r.StartsWith("refs/tags/"))
            {
                if (r.EndsWith("^{}"))
                {
                    _output.WriteLine($"tag {r.AsSpan()[10..^3]} -> commit {e.Current.Value.ToHexString()}");
                }
                else
                {
                    _output.WriteLine($"tag {r.AsSpan(10)} -> tag {e.Current.Value.ToHexString()}");
                }
            }
            else
            {
                _output.WriteLine($"{r} -> {e.Current.Value.ToHexString()}");
            }
            Assert.False(e.Current.Key.Contains(' '));
            Assert.False(e.Current.Key.Contains('\n'));
        }
        while (await e.MoveNextAsync());
    }

    [Theory]
    [InlineData("../../../..", "https://github.com/wegylexy/git-core.git/", "f0d3a70ceaa69fb70811f58254dc738e0f939eac", "1ff0c423042b46cb1d617b81efb715defbe8054d")]
    [InlineData("../../../..", "https://github.com/wegylexy/git-core.git/", "e952cd0312c660f7443e323afea25bad5eeeb78c", "985f7c92b19e5de0f28fefb96a9d004d6c4f4841")]
    public async Task UploadPackAsync(string local, string remote, string wantHex, string? haveHex = null)
    {
        ReadOnlyMemory<byte> want = wantHex.ParseHex(), have = haveHex?.ParseHex();
        using HttpClient hc = new(new AuthorizingHttpClientHandler());
        using var r = await hc.PostUploadPackAsync(new(remote),
            new(want: new[] { want }, depth: 1, have: have.IsEmpty ? null : new[] { have })
            {
                Capabilities = { "multi_ack", "include-tag" }
            }
        );
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
            var derived = co != o;
            if (derived)
            {
                _output.WriteLine("\t" + co.ToString());
                //Assert.Contains(co.Hash, hs);
                if (!hs.Contains(co.Hash))
                {
                    _output.WriteLine("\t(unexpected)");
                }
            }
            else
            {
                _output.WriteLine(co.ToString());
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
                        _output.WriteLine((derived ? "\t\t" : "\t") + e.ToString());
                        hs.Add(e.Hash);
                    }
                    break;
                case ObjectType.ReferenceDelta:
                    {
                        if (!os.TryGetValue(co.Hash, out var b))
                        {
                            ReadOnlySequence<byte> sequence;
                            {
                                using var fs = File.OpenRead(Path.Join(local, ".git/objects", co.Hash[..1].ToHexString(), co.Hash[1..].ToHexString()));
                                using ZLibStream z = new(fs, CompressionMode.Decompress);
                                sequence = await z.ToSequenceAsync();
                            }
                            var start = 0;
                            foreach (var s in sequence)
                            {
                                var i = s.Span.IndexOf<byte>(0);
                                if (i >= 0)
                                {
                                    start += i + 1;
                                    break;
                                }
                                start += s.Length;
                            }
                            b = new((int)sequence.FirstSpan[0] switch
                            {
                                'b' => ObjectType.Blob,
                                't' => sequence.Slice(1).FirstSpan[0] == 'r' ? ObjectType.Tree : ObjectType.Tag,
                                'c' => ObjectType.Tree,
                                _ => throw new InvalidDataException("Unexpected type")
                            }, sequence.Slice(start), co.Hash);
                        }
                        co = co.Delta(b); // TODO: test case for 64KB https://github.com/git/git/blob/master/Documentation/technical/pack-format.txt#L128-L131
                    }
                    goto Triage;
                case ObjectType.Commit:
                    {
                        var c = co.ToCommitContent();
                        hs.Add(c.Tree);
                        _output.WriteLine("\ttree " + c.Tree.ToHexString());
                        if (!c.Parent.IsEmpty)
                        {
                            _output.WriteLine("\tparent " + c.Parent.ToHexString());
                        }
                    }
                    break;
                case ObjectType.Tag:
                    {
                        var t = co.ToTagContent();
                        _output.WriteLine($"\t{t.Name} -> {t.Type.ToString().ToLowerInvariant()} {t.Object.ToHexString()}");
                    }
                    break;
                default:
                    throw new NotSupportedException("Unexpected type");
            }
        }
        Assert.Contains(want, (IDictionary<ReadOnlyMemory<byte>, UnpackedObject>)os); // may fail due to external reference
    }
}