using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlyByWireless.GitCore;

public static class IOExtensions
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MountPointReparseHeader
    {
        public readonly uint ReparseTag;
        public readonly ushort
            ReparseDataLength,
            Reserved,
            SubstituteNameOffset,
            SubstituteNameLength,
            PrintNameOffset,
            PrintNameLength;

        public MountPointReparseHeader(string target)
        {
            ReparseTag = 0xA0000003;
            ReparseDataLength = (ushort)(20 + target.Length * 4);
            Reserved = 0;
            SubstituteNameOffset = 0;
            SubstituteNameLength = (ushort)(8 + target.Length * 2);
            PrintNameOffset = (ushort)(SubstituteNameLength + 2);
            PrintNameLength = (ushort)(target.Length * 2);
        }
    }

    public static void CreateJunctionPoint(this DirectoryInfo junction, string target)
    {
        [DllImport("Kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern nint CreateFile
        (
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            nint securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            nint templateFile
        );

        [DllImport("Kernel32", SetLastError = true)]
        static extern bool DeviceIoControl
        (
            nint device,
            uint ioControlCode,
            in byte inBuffer, uint inBufferSize,
            in byte outBuffer, uint outBufferSize,
            out uint bytesReturned,
            nint overlapped
        );

        target = Path.GetFullPath(target);
        var headerSize = Unsafe.SizeOf<MountPointReparseHeader>();
        var bufferSize = headerSize + 12 + target.Length * 4;
        Span<byte> buffer = stackalloc byte[bufferSize];
        MemoryMarshal.AsRef<MountPointReparseHeader>(buffer) = new(target);
        MemoryMarshal.Cast<char, byte>(@"\??\").CopyTo(buffer[headerSize..]);
        var t = MemoryMarshal.Cast<char, byte>(target);
        t.CopyTo(buffer[(headerSize + 8)..]);
        t.CopyTo(buffer[(headerSize + 10 + target.Length * 2)..]);
        junction.Create();
        using SafeFileHandle handle = new(CreateFile(junction.FullName, 0x40000000,
            FileShare.ReadWrite | FileShare.Delete, 0,
            FileMode.Open, 0x02200000, 0
        ), true);
        if (Marshal.GetLastWin32Error() != 0)
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            throw new InvalidOperationException("Unknown error opening reparse point.");
        }
        if (!DeviceIoControl(handle.DangerousGetHandle(), 0x000900A4,
            in MemoryMarshal.AsRef<byte>(buffer), (uint)buffer.Length,
            in Unsafe.NullRef<byte>(), 0, out uint bytesReturned, 0
        ))
        {
            if (Marshal.GetLastWin32Error() != 0)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
            throw new InvalidOperationException("Unknown error creating junction point.");
        }
    }
}