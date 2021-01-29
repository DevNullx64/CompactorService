using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Compactor
{
    public class DiskPerformanceWatcher: IDisposable
    {
        private static readonly Dictionary<char, DiskPerformanceWatcher> Instances = new Dictionary<char, DiskPerformanceWatcher>();
        protected static System.Management.ManagementObjectCollection ManagementGet(string query) => new System.Management.ManagementObjectSearcher(query).Get();
        protected static string LogicalDiskToDiskDrive(char logicalDisk)
        {
            foreach (var partition in ManagementGet("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + logicalDisk + ":'} WHERE AssocClass = Win32_LogicalDiskToPartition"))
                foreach (var drive in ManagementGet("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partition["DeviceID"] + "'} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
                    return drive["DeviceID"].ToString();
            return null;
        }


        private readonly System.Diagnostics.PerformanceCounter diskPerformance;
        private readonly Thread Refresher;
        private float[] Samples = new float[RefreshSample];
        private int iSamples = 0;
        private float Average_ = 0.0f;

        private void Refresh()
        {
            while (true)
            {
                Samples[iSamples++]= diskPerformance.NextValue();
                if (iSamples >= RefreshSample)
                    iSamples = 0;
                Thread.Sleep(RefreshInterval);
            }
        }
        public const int RefreshInterval = 1000;
        public const int RefreshSample = 5;

        public static readonly string[] InstanceNames = new System.Diagnostics.PerformanceCounterCategory("PhysicalDisk").GetInstanceNames();

        public static string PathToDiskDrive(string path)
            => LogicalDiskToDiskDrive(path[0]);

        public bool IsIdle(float avg , float max)
        {
            float avg_ = 0;

            for (int i = 0; i < RefreshSample; i++)
            {
                float s = Samples[i];
                if (s > max)
                    return false;
                else
                    avg_ += s;
            }
            return (avg_ / RefreshSample) < avg;
        }
        public void WaitIdle(float avg, float max)
        {
            while (true)
            {
                if (IsIdle(avg, max))
                    return;
                Thread.Sleep(RefreshInterval);
            }
        }

        protected DiskPerformanceWatcher(char logicalDisk)
        {
            string instanceName = LogicalDiskToDiskDrive(logicalDisk);
            if (instanceName != null)
            {
                char diskDriveNumber = LogicalDiskToDiskDrive(logicalDisk).Last();
                instanceName = InstanceNames.Where(e => e[0] == diskDriveNumber).FirstOrDefault();
            }
            diskPerformance = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "% Disk Time", instanceName ?? "_Total");

            Refresher = new Thread(Refresh);
            Refresher.Start();
        }

        public static DiskPerformanceWatcher Get(char logicalDisk)
        {
            lock (Instances)
            {
                if (!Instances.TryGetValue(logicalDisk, out DiskPerformanceWatcher watcher))
                    Instances.Add(logicalDisk, watcher = new DiskPerformanceWatcher(logicalDisk));
                return watcher;
            }
        }

        private bool disposedValue;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Refresher.Abort();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
