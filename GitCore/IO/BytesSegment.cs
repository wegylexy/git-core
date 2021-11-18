using System.Buffers;

namespace FlyByWireless.GitCore;

internal sealed class BytesSegment : ReadOnlySequenceSegment<byte>
{
    public BytesSegment(ReadOnlyMemory<byte> memory, ReadOnlySequenceSegment<byte>? next, long runningIndex)
    {
        Memory = memory;
        Next = next;
        RunningIndex = runningIndex;
    }
}