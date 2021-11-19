using System.Net;
using System.Runtime.InteropServices;
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
            "multi_ack",
            "thin-pack"
        };
        Headers.ContentType = new("application/x-git-upload-pack-request");
    }

    public UploadPackRequest(params ReadOnlyMemory<byte>[] want) : this((IEnumerable<ReadOnlyMemory<byte>>)want) { }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        byte[] a;
        Memory<byte> hex;
        {
            using var e = Want.GetEnumerator();
            e.MoveNext();
            a = GC.AllocateUninitializedArray<byte>(10 + e.Current.Length * 2);
            byte
                a2 = (byte)((a.Length >> 4) & 0b1111).ToHex(),
                a3 = (byte)(a.Length & 0b1111).ToHex();
            hex = a.AsMemory(9, e.Current.Length * 2);
            // Writes first want
            MemoryMarshal.AsRef<int>(a.AsSpan(4, 8)) =
                BitConverter.IsLittleEndian ? 0x74_6e_61_77 : 0x77_61_6e_74; // "want"
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
        MemoryMarshal.AsRef<int>(a.AsSpan(12, 4)) = 0x30_30_30_30; // "0000";
        await stream.WriteAsync(a.AsMemory(12, 4));
        // Writes have (if any)
        if (Have is not null)
        {
            MemoryMarshal.AsRef<int>(a.AsSpan(4, 8)) =
                BitConverter.IsLittleEndian ? 0x65_76_61_68 : 0x68_61_76_65; // "have"
            foreach (var id in Have)
            {
                id.ToHexASCII(hex.Span);
                await stream.WriteAsync(a);
            }
        }
        // Writes done
        MemoryMarshal.AsRef<long>(a.AsSpan(0, 8)) =
            BitConverter.IsLittleEndian ? 0x65_6e_6f_64_39_30_30_30 : 0x30_30_30_39_64_6f_6e_65; // "0009done"
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