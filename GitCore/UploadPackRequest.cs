using System.Net;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed class UploadPackRequest : HttpContent
{
    public IEnumerable<ReadOnlyMemory<byte>> Want { get; }

    public int Depth { get; init; }

    public IEnumerable<ReadOnlyMemory<byte>>? Have { get; }

    public ISet<string> Capabilities { get; }

    public UploadPackRequest
    (
        IEnumerable<ReadOnlyMemory<byte>> want,
        int depth = default,
        IEnumerable<ReadOnlyMemory<byte>>? have = null,
        ISet<string>? capabilities = null
    )
    {
        Want = want;
        Depth = depth;
        Have = have;
        Capabilities = capabilities ?? new HashSet<string>
        {
            "thin-pack"
        };
        Headers.ContentType = new("application/x-git-upload-pack-request");
    }

    public UploadPackRequest(params ReadOnlyMemory<byte>[] want) : this((IEnumerable<ReadOnlyMemory<byte>>)want) { }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        byte[] a;
        byte a2, a3;
        Memory<byte> hex;
        {
            using var e = Want.GetEnumerator();
            e.MoveNext();
            a = GC.AllocateUninitializedArray<byte>(10 + e.Current.Length * 2);
            a2 = (byte)((a.Length >> 4) & 0b1111).ToHex();
            a3 = (byte)(a.Length & 0b1111).ToHex();
            hex = a.AsMemory(9, e.Current.Length * 2);
            // Writes first want
            a[4] = 0x77; // 'w'
            a[5] = 0x61; // 'a'
            a[6] = 0x6e; // 'n'
            a[7] = 0x74; // 't'
            a[8] = 0x20; // ' '
            e.Current.ToHexASCII(hex.Span);
            a[^1] = 10; // '\n'
            if (Capabilities.Count > 0)
            {
                var l = a.Length + Capabilities.Sum(c => 1 + c.Length);
                a[0] = (byte)((l >> 12) & 0b1111).ToHex();
                a[1] = (byte)((l >> 8) & 0b1111).ToHex();
                a[2] = (byte)((l >> 4) & 0b1111).ToHex();
                a[3] = (byte)(l & 0b1111).ToHex();
                await stream.WriteAsync(a.AsMemory(0, a.Length - 1));
                // Writes capabilities
                foreach (var c in Capabilities)
                {
                    if (c.Length <= hex.Length)
                    {
                        for (var i = 0; i < c.Length; ++i)
                        {
                            hex.Span[i] = (byte)c[i];
                        }
                        await stream.WriteAsync(a.AsMemory(8, 1 + c.Length));
                    }
                    else
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(' ' + c));
                    }
                }
                await stream.WriteAsync(a.AsMemory(a.Length - 1, 1));
                a[1] = a[0] = 0x30; // "00"
                a[2] = a2;
                a[3] = a3;
            }
            else
            {
                a[1] = a[0] = 0x30; // "00"
                a[2] = a2;
                a[3] = a3;
                await stream.WriteAsync(a);
            }
            // Writes subsequent want (if any)
            while (e.MoveNext())
            {
                e.Current.ToHexASCII(hex.Span);
                await stream.WriteAsync(a);
            }
        }
        // Deepens
        if (Depth != default)
        {
            var length = 4 + Encoding.ASCII.GetBytes(FormattableString.Invariant($"deepen {Depth}\n"), a.AsSpan(13, a.Length - 13));
            a[9] = (byte)((length >> 12) & 0b1111).ToHex();
            a[10] = (byte)((length >> 8) & 0b1111).ToHex();
            a[11] = (byte)((length >> 4) & 0b1111).ToHex();
            a[12] = (byte)(length & 0b1111).ToHex();
            await stream.WriteAsync(a.AsMemory(9, length));
        }
        // Flushes
        a[15] = a[14] = a[13] = a[12] = 0x30; // "0000";
        await stream.WriteAsync(a.AsMemory(12, 4));
        // Writes have (if any)
        if (Have is not null)
        {
            a[4] = 0x68; // 'h'
            a[6] = 0x76; // 'v'
            a[7] = 0x65; // 'e'
            foreach (var id in Have)
            {
                id.ToHexASCII(hex.Span);
                await stream.WriteAsync(a);
            }
        }
        // Writes done
        a[2] = 0x30; // '0'
        a[3] = 0x39; // '9'
        a[4] = 0x64; // 'd'
        a[5] = 0x6f; // 'o'
        a[6] = 0x6e; // 'n'
        a[7] = 0x65; // 'e'
        a[8] = 10; // '\n'
        await stream.WriteAsync(a.AsMemory(0, 9));
        await stream.FlushAsync();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = default;
        return false;
    }
}