using Compactor.PInvok;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Compactor
{
    public static class Compactor
    {
        public static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";

        public static uint GetClusterSize(DriveInfo drive)
        {
            Kernel32.GetDiskFreeSpace(drive, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _);
            return sectorsPerCluster * bytesPerSector;
        }
        public static uint GetClusterSize(DirectoryInfo directory)
            => GetClusterSize(new DriveInfo(directory.Root.FullName));
        public static uint GetClusterSize(FileInfo file)
            => GetClusterSize(file.Directory);

        private static readonly string[] FileLength_FormatUnits = { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
        public static string FileLengthToString(long length, int digits = 2)
        {
            double len = length;
            int i = 0;
            while (i < FileLength_FormatUnits.Length && len > 1024)
            {
                len /= 1024;
                i++;
            }
            return len.ToString("F" + digits.ToString() + FileLength_FormatUnits[i]);
        }

        private static long LengthOnDisk(long length, uint clusterSize)
        {
            uint mask = clusterSize - 1;
            return (length & mask) > 0 ? (length & ~mask) + clusterSize : length;
        }

        #region NTFS compression
        private static SafeFileHandle CreateFile(string path, FileAccess fileAccess, FileShare fileShare, Kernel32.CreateFileFlag flags = Kernel32.CreateFileFlag.NONE)
        {
            if (path.StartsWith(WindowsDirectory))
                throw new AccessViolationException();
            return Kernel32.CreateFile(path, fileAccess, fileShare, FileMode.Open, flags);
        }
        internal static SafeFileHandle CreateFile(FileInfo fileInfo, FileAccess fileAccess, FileShare fileShare)
            => CreateFile(fileInfo.FullName, fileAccess, fileShare);
        internal static SafeFileHandle CreateDirectory(DirectoryInfo directoryInfo, FileAccess fileAccess)
            => CreateFile(directoryInfo.FullName, fileAccess, FileShare.ReadWrite, Kernel32.CreateFileFlag.FILE_FLAG_BACKUP_SEMANTICS);

        internal static void SetDirectoryCompression(SafeFileHandle fileHandle, Kernel32.NtfsCompression algorithm)
            => Kernel32.SetCompression(fileHandle, algorithm);
        internal static void SetFileCompression(SafeFileHandle fileHandle, CompressionAlgorithm algorithm)
        {
            if (algorithm.HasFlag(CompressionAlgorithm.NONE))
            {
                Kernel32.DeleteExternalBacking(fileHandle);
                 Kernel32.SetCompression(fileHandle, (Kernel32.NtfsCompression)(ushort)algorithm);
            }
            else
            {
                Kernel32.SetCompression(fileHandle, Kernel32.NtfsCompression.NONE);
                Kernel32.SetExternalBacking(fileHandle, (Kernel32.WofCompressionAlgorithm)algorithm);
            }
        }

        public static bool SetCompression(FileInfo fileInfo, CompressionAlgorithm algorithm, uint clusterSize, double minCompression = 1)
        {
            if (fileInfo.Length > clusterSize)
            {
                bool ro = fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly);
                if (ro) fileInfo.Attributes &= ~FileAttributes.ReadOnly;
                try
                {
                    if (algorithm.HasFlag(CompressionAlgorithm.NONE))
                        using (SafeFileHandle fHandle = CreateFile(fileInfo, FileAccess.ReadWrite, FileShare.None))
                            SetFileCompression(fHandle, algorithm);
                    else
                    {
                        if (fileInfo.Attributes.HasFlag(FileAttributes.Compressed))
                            using (SafeFileHandle fHandle = CreateFile(fileInfo, FileAccess.ReadWrite, FileShare.None))
                                Kernel32.SetCompression(fHandle, Kernel32.NtfsCompression.NONE);
                        using (SafeFileHandle fHandle = CreateFile(fileInfo, FileAccess.Read, FileShare.None))
                        {
                            SetFileCompression(fHandle, algorithm);
                            if (algorithm != CompressionAlgorithm.NONE)
                                if (LengthOnDisk(Kernel32.GetCompressedFileLength(fileInfo.FullName), clusterSize) / (double)LengthOnDisk(fileInfo.Length, clusterSize) > minCompression)
                                    SetFileCompression(fHandle, CompressionAlgorithm.NONE);
                        }
                    }
                }
                catch (Exception e)
                {
                    return false;
                }
                finally
                {
                    if (ro) fileInfo.Attributes |= FileAttributes.ReadOnly;
                }
            }

            return true;
        }
        public static bool SetCompression(FileInfo fileInfo, CompressionAlgorithm algorithm, double minCompression = 1)
            => SetCompression(fileInfo, algorithm, GetClusterSize(fileInfo), minCompression);

        public static void SetCompression(DirectoryInfo directoryInfo, Kernel32.NtfsCompression algorithm)
        {
            using (SafeFileHandle directoryHandle = CreateDirectory(directoryInfo, FileAccess.ReadWrite))
                SetDirectoryCompression(directoryHandle, algorithm);
        }
        public static void SetCompression(DirectoryInfo directoryInfo, CompressionAlgorithm algorithm)
            => SetCompression(
                directoryInfo,
                (algorithm == CompressionAlgorithm.NONE)
                ? Kernel32.NtfsCompression.NONE
                : Kernel32.NtfsCompression.LZNT1);

        public class ThreaParameter
        {
            private readonly Func<ThreaParameter, bool> OnFileTreatedFnc;
            public readonly uint ClusterSize;
            public readonly CompressionAlgorithm Algorithm;
            public readonly DirectoryInfo Directory;
            public readonly double MinCompression;

            private readonly Queue<FileInfo> ToDo = new Queue<FileInfo>();
            public FileInfo CurrentFile { get; private set; }
            public long LengthToDo { get; private set; } = 0;
            public bool LengthToDoFinished = false;
            public long LengthDone { get; private set; } = 0;

            public void Enqueue(FileInfo file)
            {
                lock (this)
                {
                    LengthToDo += LengthOnDisk(file.Length, ClusterSize);
                    ToDo.Enqueue(file);
                }
            }

            public FileInfo Dequeue()
            {
                if (CurrentFile is object)
                    LengthDone += LengthOnDisk(CurrentFile.Length, ClusterSize);
                while (ToDo.Count == 0 && !LengthToDoFinished)
                    Thread.Sleep(100);
                lock (this)
                {
                    return CurrentFile = ToDo.Dequeue();
                }
            }

            public double Progress => LengthDone / (double)LengthToDo;

            public ThreaParameter(DirectoryInfo directory, CompressionAlgorithm algorithm, double minCompression = 1, Func<ThreaParameter, bool> onFileTreated =null )
            {
                Directory = directory;
                ClusterSize = GetClusterSize(Directory);
                Algorithm = algorithm;
                MinCompression = minCompression;
                OnFileTreatedFnc = onFileTreated;
            }

            public void FileTreated()
            {
                if (OnFileTreatedFnc is object && !OnFileTreatedFnc(this))
                    ToDo.Clear();
            }
        }

        private static void FillToDoThread(DirectoryInfo directory, ThreaParameter parameters)
        {
            if (directory is object)
            {
                SetCompression(directory, parameters.Algorithm);
                foreach (FileSystemInfo fileSystemInfo in directory.EnumerateFileSystemInfos())
                    if (fileSystemInfo is FileInfo fileInfo)
                        parameters.Enqueue(fileInfo);
                    else
                        FillToDoThread(fileSystemInfo as DirectoryInfo, parameters);
            }
        }

        private static void FillToDoThread(object obj)
        {
            if (obj is ThreaParameter parameters)
            {
                FillToDoThread(parameters.Directory, parameters);
                parameters.LengthToDoFinished = true;
            }
            else
                throw new InvalidCastException();
        }

        public static void ApplyCompressionThread(object obj)
        {
            if (obj is ThreaParameter parameters)
            {
                FileInfo current;
                while ((current = parameters.Dequeue()) is object)
                {
                    SetCompression(current, parameters.Algorithm, parameters.ClusterSize, parameters.MinCompression);
                    parameters.FileTreated();
                }
            }
            else
                throw new InvalidCastException();
        }

        public static void SetCompression(DirectoryInfo directory, CompressionAlgorithm algorythm, bool recursive = false, double minCompression = 1, Func<ThreaParameter, bool> onFileTreated = null)
        {
            if (recursive)
            {
                ThreaParameter parameters = new ThreaParameter(directory, algorythm, minCompression, onFileTreated);
                Thread FillToDo = new Thread(FillToDoThread);
                FillToDo.Start(parameters);
                Thread CompactorThread = new Thread(ApplyCompressionThread);
                CompactorThread.Start(parameters);
                CompactorThread.Join();
            }
            else
                SetCompression(directory, algorythm);
        }
        #endregion
    }
}