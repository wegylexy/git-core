using System.Globalization;
using System.Text;

namespace FlyByWireless.GitCore;

public class UploadPackAdvertisement : IAsyncEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>>
{
    private readonly HttpResponseMessage _response;

    public ISet<string> Capabilities { get; } = new HashSet<string>();

    internal UploadPackAdvertisement(HttpResponseMessage response)
    {
        if (response.EnsureSuccessStatusCode().Content.Headers.ContentType is not { MediaType: "application/x-git-upload-pack-advertisement" })
        {
            using (response) { }
            throw new InvalidOperationException("Unexpected media type");
        }
        _response = response;
    }

    public async IAsyncEnumerator<KeyValuePair<string, ReadOnlyMemory<byte>>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        const string service = "001e# service=git-upload-pack\n0000";
        try
        {
            using var s = await _response.Content.ReadAsStreamAsync(cancellationToken);
            using StreamReader reader = new(s, Encoding.UTF8);
            var buffer = GC.AllocateUninitializedArray<char>(service.Length);
            if (await reader.ReadBlockAsync(buffer, cancellationToken) != service.Length || !buffer.SequenceEqual(service))
            {
                throw new EndOfStreamException();
            }
            while (!reader.EndOfStream)
            {
                if (await reader.ReadBlockAsync(buffer.AsMemory(0, 4), cancellationToken) != 4)
                {
                    throw new EndOfStreamException();
                }
                var r = int.Parse(buffer.AsSpan(0, 4), NumberStyles.AllowHexSpecifier);
                if (r > 4)
                {
                    r -= 4;
                    if (buffer.Length < r)
                    {
                        Array.Resize(ref buffer, r);
                    }
                    if (await reader.ReadBlockAsync(buffer.AsMemory(0, r), cancellationToken) != r)
                    {
                        throw new EndOfStreamException();
                    }
                    if (buffer[--r] != '\n')
                    {
                        throw new InvalidDataException("Unexpected end of line");
                    }
                    var line = buffer.AsMemory(0, r);
                    var i = line.Span.IndexOf(' ');
                    var hex = ((ReadOnlySpan<char>)line.Span[..i]).ParseHex();
                    var e = line.Span[(i + 1)..].IndexOf('\0');
                    if (e < 0)
                    {
                        yield return new(new(line.Span[(i + 1)..]), hex);
                    }
                    else
                    {
                        for (var b = i + e + 2; ;)
                        {
                            var n = line.Span[b..].IndexOf(' ');
                            if (n < 0)
                            {
                                Capabilities.Add(new(line.Span[b..]));
                                break;
                            }
                            Capabilities.Add(new(line.Span[b..(b + n)]));
                            b += n + 1;
                        }
                        yield return new(new(line.Span[(i + 1)..(i + 1 + e)]), hex);
                    }
                }
                else if (r != 0)
                {
                    throw new InvalidDataException("Length out of range");
                }
            }
        }
        finally
        {
            using (_response) { }
        }
    }
}