using Compactor.PInvok;
using static Compactor.PInvok.Kernel32;

namespace Compactor
{
    public enum CompressionAlgorithm : uint
    {
        NONE = WofCompressionAlgorithm.NONE,
        LZNT1 = NtfsCompression.LZNT1 | WofCompressionAlgorithm.NONE,
        XPRESS4K = WofCompressionAlgorithm.XPRESS4K,
        XPRESS8K = WofCompressionAlgorithm.XPRESS8K,
        XPRESS16K = WofCompressionAlgorithm.XPRESS16K,
        LZX = WofCompressionAlgorithm.LZX,
    }
}
