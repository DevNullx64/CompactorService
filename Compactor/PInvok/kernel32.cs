using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;

namespace Compactor.PInvok
{
    public static class kernel32
    {
        [Flags]
        public enum CreateFileFlag
        {
            NONE = 0x00000000,
            FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string filename,
            FileAccess access,
            FileShare share,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            CreateFileFlag flagsAndAttributes,
            IntPtr templateFile);
        public static SafeFileHandle CreateFile(string path, FileAccess access, FileShare share, FileMode creationDisposition, CreateFileFlag flags) {
        SafeFileHandle result = CreateFile(path, access, share, IntPtr.Zero, creationDisposition, flags, IntPtr.Zero);
            if (result.IsInvalid)
                throw new Win32Exception();
            return result;
        }

        private const int ERROR_SUCCESS = 0;


        private const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

        [DllImport("kernel32.dll")]
        private static extern uint GetCompressedFileSizeW(
            [In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            [Out] out int lpFileSizeHigh);
        public static long GetCompressedFileLength(string lpFileName)
        {
            long result = GetCompressedFileSizeW(lpFileName, out int lpHosize) | (long)lpHosize << 32;
            if (result == INVALID_FILE_SIZE)
            {
                int lastError = Marshal.GetLastWin32Error();
                if (lastError != ERROR_SUCCESS)
                    throw new Win32Exception(lastError);
            }

            return result;
        }

        [DllImport("kernel32.dll", SetLastError = true, PreserveSig = true)]
        private static extern bool GetDiskFreeSpaceW(
            [In, MarshalAs(UnmanagedType.LPWStr)] string lpRootPathName,
            out uint lpSectorsPerCluster,
            out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters,
            out uint lpTotalNumberOfClusters);
        public static void GetDiskFreeSpace(this DriveInfo directoryInfo, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters)
        {
            if (!GetDiskFreeSpaceW(directoryInfo.Name, out sectorsPerCluster, out bytesPerSector, out numberOfFreeClusters, out totalNumberOfClusters))
                throw new Win32Exception();
        }

        public enum IoControlCode
        {
            FSCTL_SET_COMPRESSION = 0x0009C040,
            FSCTL_GET_COMPRESSION = 0x0009003C,

            FSCTL_SET_EXTERNAL_BACKING = 0x0009030C,
            FSCTL_GET_EXTERNAL_BACKING = 0x00090310,
            FSCTL_DELETE_EXTERNAL_BACKING = 0x00090314,

            FSCTL_GET_VOLUME_BITMAP = 0x0009006F,
            FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073,
            FSCTL_MOVE_FILE = 0x00090074,
        }

        //[DllImport("Kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        //private static extern bool DeviceIoControl(SafeHandle hDevice, IoControlCode IoControlCode, IntPtr InBuffer, uint nInBufferSize, IntPtr OutBuffer, uint nOutBufferSize, uint pBytesReturned, uint Overlapped);
        [DllImport("Kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(SafeHandle hDevice, IoControlCode IoControlCode, uint InBuffer, uint nInBufferSize, ref short OutBuffer, uint nOutBufferSize, uint pBytesReturned, uint Overlapped);
        [DllImport("Kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(SafeHandle hDevice, IoControlCode IoControlCode, [In] ref short InBuffer, uint nInBufferSize, uint OutBuffer, uint nOutBufferSize, uint pBytesReturned, uint Overlapped);

        public static void DeviceIoControl(SafeHandle hDevice, IoControlCode IoControlCode, short InBuffer)
        {
            if (!DeviceIoControl(hDevice, IoControlCode, ref InBuffer, 2, 0, 0, 0, 0))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_SUCCESS)
                    throw new Win32Exception(err);
            }
        }
        public static void DeviceIoControl(SafeHandle hDevice, IoControlCode IoControlCode, out short InBuffer)
        {
            InBuffer = 0;
            if (!DeviceIoControl(hDevice, IoControlCode, 0, 0, ref InBuffer, 2, 0, 0))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_SUCCESS)
                    throw new Win32Exception(err);
            }
        }

        public enum NtfsCompression : ushort
        {
            NONE = 0x0000,
            DEFAULT = 0x0001,
            LZNT1 = 0x0002
        }
        internal static bool SetCompression(SafeFileHandle fileHandle, NtfsCompression value)
        {
            if (value == NtfsCompression.DEFAULT)
                value = NtfsCompression.LZNT1;

            if (GetCompression(fileHandle) != value)
                DeviceIoControl(fileHandle, IoControlCode.FSCTL_SET_COMPRESSION, (short)value);

            return true;
        }
        internal static NtfsCompression GetCompression(SafeFileHandle fileHandle)
        {
            DeviceIoControl(fileHandle, IoControlCode.FSCTL_GET_COMPRESSION, out short lpOutBuffer);
            return (NtfsCompression)lpOutBuffer;
        }

        public enum WofCompressionAlgorithm : uint
        {
            NONE = 0xFFFF0000,
            XPRESS4K = 0x00000000,
            LZX = 0x00000001,
            XPRESS8K = 0x00000002,
            XPRESS16K = 0x00000003
        }

        [StructLayout(LayoutKind.Sequential)]
        private class WimFileProviderExternalInfo
        {
            public const int size = 20;

            private const int WOF_CURRENT_VERSION = 1;
            private const int WOF_PROVIDER_FILE = 2;
            private const int FILE_PROVIDER_CURRENT_VERSION = 1;

            public uint Version;
            public uint Provider;
            public uint Version2;
            public WofCompressionAlgorithm Algorithm;
            public uint Flags;

            public WimFileProviderExternalInfo() { }

            public WimFileProviderExternalInfo(WofCompressionAlgorithm algorithm)
            {
                Version = WOF_CURRENT_VERSION;
                Provider = WOF_PROVIDER_FILE;
                Version2 = FILE_PROVIDER_CURRENT_VERSION;
                Algorithm = algorithm;
                Flags = 0;
            }
        }

        [DllImport("Kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, IoControlCode controlCode, [In] WimFileProviderExternalInfo InBuffer, int InBufferSize, IntPtr OutBuffer, int nOutBufferSizen, IntPtr pBytesReturned, IntPtr Overlapped);
        public static bool SetExternalBacking(SafeFileHandle fileHandle, WofCompressionAlgorithm algorithm)
        {
            if (!(GetExternalBacking(fileHandle, out WofCompressionAlgorithm value) && value == algorithm))
                DeviceIoControl(
                   fileHandle,
                   IoControlCode.FSCTL_SET_EXTERNAL_BACKING,
                   new WimFileProviderExternalInfo(algorithm), WimFileProviderExternalInfo.size,
                   IntPtr.Zero, 0,
                   IntPtr.Zero, IntPtr.Zero);

            int e = Marshal.GetLastWin32Error();
            if (e != ERROR_SUCCESS)
                throw new Win32Exception(e);

            return true;
        }

        [DllImport("Kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, IoControlCode controlCode, IntPtr InBuffer, int nInBufferSize, [In,Out] WimFileProviderExternalInfo OutBuffer, int nOutBufferSize, ref uint pBytesReturned, IntPtr Overlapped);
        public static bool GetExternalBacking(SafeFileHandle fileHandle, out WofCompressionAlgorithm algorithm)
        {
            WimFileProviderExternalInfo OutBuffer = new WimFileProviderExternalInfo();
            uint bytesReturned = 0;

            bool result = DeviceIoControl(
                    fileHandle,
                    IoControlCode.FSCTL_GET_EXTERNAL_BACKING,
                    IntPtr.Zero, 0,
                    OutBuffer, WimFileProviderExternalInfo.size,
                    ref bytesReturned, IntPtr.Zero);

            algorithm = OutBuffer.Algorithm;

            if (result)
                return true;
            else
            {
                int lastError = Marshal.GetLastWin32Error();
                if (lastError == ERROR_SUCCESS)
                    return false;
                else
                    throw new Win32Exception(lastError);
            }

        }
        public static bool DeleteExternalBacking(SafeFileHandle fileHandle)
        {
            WimFileProviderExternalInfo OutBuffer = new WimFileProviderExternalInfo();
            uint bytesReturned = 0;
            if (!DeviceIoControl(
                    fileHandle,
                    IoControlCode.FSCTL_DELETE_EXTERNAL_BACKING,
                    IntPtr.Zero, 0,
                    OutBuffer, Marshal.SizeOf(OutBuffer),
                    ref bytesReturned, IntPtr.Zero))
            {
                int lastError = Marshal.GetLastWin32Error();
                if (lastError != ERROR_SUCCESS)
                    throw new Win32Exception(lastError);
            }

            return true;
        }

        #region defrag
        public struct Extent
        {
            public long NextVcn;
            public long LCN;
        }

        private const int GeExtentsCount = 512;
        public struct RETRIEVAL_POINTERS_BUFFER
        {
            public int ExtentCount;
            public int Padding;
            public long StartingVcn;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = GeExtentsCount)]
            public Extent[] Extents;
        }

        //indique que l'on n'a pas encore fini de lire la carte d'occupation
        public const int ERROR_MORE_DATA = 234;

        [DllImport("kernel32.dll")]
        private static extern int DeviceIoControl(SafeFileHandle hDevice, IoControlCode controlCode, ref long lpInBuffer, int nInBufferSize, ref RETRIEVAL_POINTERS_BUFFER lpOutBuffer, int nOutBufferSize, ref uint lpBytesReturned, IntPtr lpOverlapped);
        public static RETRIEVAL_POINTERS_BUFFER GetRetrievalPointers(SafeFileHandle hFile, long StartingAddress = 0)
        {
            uint bytesReturned = 0;
            int status = 0;
            RETRIEVAL_POINTERS_BUFFER FileBitmap = new RETRIEVAL_POINTERS_BUFFER();
            List<Extent> extents = new List<Extent>();

            do
            {
                DeviceIoControl(hFile, IoControlCode.FSCTL_GET_RETRIEVAL_POINTERS, ref StartingAddress, 8, ref FileBitmap, Marshal.SizeOf(FileBitmap), ref bytesReturned, IntPtr.Zero);
                status = Marshal.GetLastWin32Error();
                    extents.AddRange(FileBitmap.Extents.Take(Math.Min(GeExtentsCount, FileBitmap.ExtentCount)));
                StartingAddress = extents[extents.Count - 1].NextVcn;
            } while (status == ERROR_MORE_DATA);

            FileBitmap.Extents = extents.ToArray();
            FileBitmap.ExtentCount = extents.Count;

            return FileBitmap;
        }

        public struct MOVE_FILE_DATA
        {
            public SafeFileHandle FileHandle;
            public long StartingVcn;
            public long StartingLcn;
            public uint ClusterCount;
        }

        [DllImport("kernel32.dll")]
        private static extern int DeviceIoControl(SafeFileHandle hDevice, IoControlCode controlCode, ref MOVE_FILE_DATA lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, IntPtr lpBytesReturned, IntPtr lpOverlapped);
        public static void MoveFile(SafeFileHandle volHandle, SafeFileHandle fHandle, long fStartingVCN, uint fClusterCount, long newLCN)
        {
            MOVE_FILE_DATA moveFile = new MOVE_FILE_DATA();

            moveFile.ClusterCount = fClusterCount;
            moveFile.FileHandle = fHandle;
            moveFile.StartingLcn = newLCN;
            moveFile.StartingVcn = fStartingVCN;

            int ret = DeviceIoControl(
                volHandle,
                IoControlCode.FSCTL_MOVE_FILE,
                ref moveFile, Marshal.SizeOf(moveFile), 
                IntPtr.Zero, 0,
                IntPtr.Zero, IntPtr.Zero);
            if (ret == 0)
                throw new Win32Exception();
        }

        public class VOLUME_BITMAP_BUFFER
        {
            public const int BitmapBufferSize = 4096 / sizeof(ulong);

            public long StartingLcn;
            public long BitmapSize;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = BitmapBufferSize)]
            public ulong[] Bitmap = new ulong[VOLUME_BITMAP_BUFFER.BitmapBufferSize];

        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, IoControlCode controlCode, ref long lpInBuffer, int nInBufferSize, [Out] VOLUME_BITMAP_BUFFER lpOutBuffer, int nOutBufferSize, IntPtr lpBytesReturned, IntPtr lpOverlapped);
        public static void GetVolumeBitmap(SafeFileHandle hVol, long StartingAddress, VOLUME_BITMAP_BUFFER VolumeBitmap)
        {
            if (!DeviceIoControl(hVol, IoControlCode.FSCTL_GET_VOLUME_BITMAP, ref StartingAddress, sizeof(long), VolumeBitmap, Marshal.SizeOf(VolumeBitmap), IntPtr.Zero, IntPtr.Zero))
            {
                int status = Marshal.GetLastWin32Error();
                if (status != ERROR_MORE_DATA)
                    throw new Win32Exception(status);
            }
        }
        public static VOLUME_BITMAP_BUFFER GetVolumeBitmap(SafeFileHandle hVol, long StartingAddress)
        {
            VOLUME_BITMAP_BUFFER VolumeBitmap = new VOLUME_BITMAP_BUFFER();
            GetVolumeBitmap(hVol, StartingAddress, VolumeBitmap);
            return VolumeBitmap;
        }
    }
    #endregion
}