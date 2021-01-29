using Compactor.PInvok;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CompactorUI.Defrag
{
    class VolumeBitmapBuffer
    {
        private readonly SafeFileHandle hVolume;
        public readonly DriveInfo Volume;
        public readonly bool Moveable;
        public struct ClustersRange
        {
            public long startLCN;
            public long ClusterCount;
        }

        public VolumeBitmapBuffer(DriveInfo volume)
        {
            Volume = volume;
            hVolume = kernel32.CreateFile("\\\\.\\" + Volume.Name, FileAccess.Read, FileShare.ReadWrite, FileMode.Open, kernel32.CreateFileFlag.NONE);
            if (hVolume.IsInvalid)
                throw new Win32Exception();
        }
        public VolumeBitmapBuffer(string volume)
            : this(new DriveInfo(volume)) { }


        private ClustersRange? GetExtents(long startCluster, long count)
        {
            long c = 0;

            while (c < count)
            {
                kernel32.VOLUME_BITMAP_BUFFER volumeBitmap = kernel32.GetVolumeBitmap(hVolume, startCluster >> 6);
            }
            return null;
        }
    }
}