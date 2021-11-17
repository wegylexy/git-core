﻿using System.Net;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed class UploadPackRequest : HttpContent
{
    private readonly IEnumerable<ReadOnlyMemory<byte>> _want;
    private readonly IEnumerable<ReadOnlyMemory<byte>>? _have;

    public UploadPackRequest
    (
        IEnumerable<ReadOnlyMemory<byte>> want,
        IEnumerable<ReadOnlyMemory<byte>>? have = null
    )
    {
        _want = want;
        _have = have;
        Headers.ContentType = new("application/x-git-upload-pack-request");
    }

    public UploadPackRequest(params ReadOnlyMemory<byte>[] want) : this(want, null)
    { }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        int length;
        byte[] a;
        Memory<byte> buffer;
        {
            using var e = _want.GetEnumerator();
            e.MoveNext();
            length = 10 + e.Current.Length * 2;
            a = GC.AllocateUninitializedArray<byte>(Math.Max(9, length));
            Encoding.ASCII.GetBytes(length.ToString("x4"), a.AsSpan(0, 4));
            a[4] = 0x77; // 'w'
            a[5] = 0x61; // 'a'
            a[6] = 0x6e; // 'n'
            a[7] = 0x74; // 't'
            a[8] = 0x20; // ' '
            a[length - 1] = 10; // '\n'
            buffer = a.AsMemory(0, length);
            do
            {
                e.Current.ToHexASCII(buffer.Span[9..]);
                await stream.WriteAsync(buffer);
            }
            while (e.MoveNext());
        }
        a[15] = a[14] = a[13] = a[12] = 0x30; // "0000";
        await stream.WriteAsync(a.AsMemory(12, 4));
        if (_have is not null)
        {
            a[4] = 0x68; // 'h'
            a[6] = 0x76; // 'v'
            a[7] = 0x65; // 'e'
            foreach (var id in _have)
            {
                id.ToHexASCII(buffer.Span[9..]);
                await stream.WriteAsync(buffer);
            }
        }
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