//#define LOG
using Compactor;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CompactorService
{
    public partial class Service : ServiceBase
    {
#if LOG
        private static readonly StreamWriter log = new StreamWriter(new FileStream("C:\\log.txt", FileMode.Create, FileAccess.Write, FileShare.Read));

        protected static void WriteLog(string fncName, string message)
        {
            lock (log)
            {
                log.WriteLine(DateTime.Now.ToString() + " " + fncName + "(): " + message);
                log.Flush();
            }
        }

        protected static void WriteLog(string fncName, Exception exception)
        {
            WriteLog(fncName, exception.Message);
        }
#endif

        private class FileToTreadInfo
        {
            public readonly CompressionAlgorithm Algorithm;
            public long Ticks;

            public FileToTreadInfo(CompressionAlgorithm algorithm)
            {
                Algorithm = algorithm;
                Ticks = DateTime.Now.Ticks;
            }
        }

        private static readonly Dictionary<string, FileToTreadInfo> FileToTread = new Dictionary<string, FileToTreadInfo>();

        private static bool Compact(DirectoryInfo directoryInfo, CompressionAlgorithm algorithm)
        {
            Compactor.Compactor.SetCompression(directoryInfo, algorithm);
#if LOG
            WriteLog(nameof(Compact), directoryInfo.FullName + " => " + algorithm.ToString());
#endif
            return true;
        }

        private static bool Compact(FileInfo fileInfo, CompressionAlgorithm algorithm)
        {
            if (!fileInfo.Exists || !fileInfo.Attributes.HasFlag(FileAttributes.Archive))
                return true;
            DiskPerformanceWatcher locker = DiskPerformanceWatcher.Get(fileInfo.FullName[0]);
            lock (locker)
            {
                if (!fileInfo.Attributes.HasFlag(FileAttributes.Archive))
                    return true;
                else
                {
                    locker.WaitIdle(75, 95);
                    if (Compactor.Compactor.SetCompression(fileInfo, algorithm))
                    {
                        fileInfo.Attributes &= ~FileAttributes.Archive;
#if LOG
                        WriteLog(nameof(Compact), fileInfo.FullName + " => " + algorithm.ToString());
#endif
                        return true;
                    }
                    else
                    {
#if LOG
                        WriteLog(nameof(Compact), fileInfo.FullName + " => Error");
#endif
                        return false;
                    }
                }
            }
        }
        private static bool Compact(string path, CompressionAlgorithm algorithm)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Compactor.Compactor.SetCompression(new DirectoryInfo(path),
                        (algorithm == CompressionAlgorithm.LZNT1)
                        ? CompressionAlgorithm.LZNT1
                        : CompressionAlgorithm.NONE);
                    return true;
                }
                else
                    return Compact(new FileInfo(path), algorithm);
            }
            catch (Exception e)
            {
#if LOG
                WriteLog(nameof(Compact), e);
#endif
                return false;
            }
        }

        private class Watcher : IDisposable
        {
            private Thread InitialisationThread = null;
            private bool Enabled_ = false;
            public string PathName => watcher.Path;
            public bool SubFolder => watcher.IncludeSubdirectories;
            public readonly CompressionAlgorithm Algorithm;
            public bool IsInitializing => InitialisationThread is object;

            private void Initialise(string directory)
            {
                if (Directory.Exists(directory))
                {
                    foreach (string subDirectory in Directory.EnumerateDirectories(directory))
                        if (Enabled_)
                            Initialise(subDirectory);
                        else return;
                    foreach (string filename in Directory.EnumerateFiles(directory))
                        if (Enabled_)
                            Compact(filename, Algorithm);
                        else return;
                }
            }

            public bool Enabled
            {
                get => Enabled_;
                set
                {
                    if (Enabled_ != value)
                    {
                        Enabled_ = value;
                        if (Enabled_)
                        {
                            InitialisationThread = new Thread(() =>
                            {
                                try
                                {
#if LOG
                                    WriteLog(nameof(InitialisationThread), watcher.Path + " intializing...");
#endif
                                    Initialise(watcher.Path);
                                    watcher.EnableRaisingEvents = Enabled_;
                                    InitialisationThread = null;
#if LOG
                                    WriteLog(nameof(InitialisationThread), watcher.Path + " intialized");
#endif
                                }
                                catch (Exception e)
                                {
#if LOG
                                    WriteLog(nameof(InitialisationThread), watcher.Path + " Error\n" + e.Message);
#endif
                                }
                            })
                            { Priority = ThreadPriority.Lowest };
                            InitialisationThread.Start();
                        }
                    }
                }
            }

            private readonly FileSystemWatcher watcher;

            public Watcher(string pathName, bool subFolder, CompressionAlgorithm algorithm)
            {
                Algorithm = algorithm;

                watcher = new FileSystemWatcher
                {
                    Path = pathName,
                    Filter = "*.*",
                    IncludeSubdirectories = subFolder,
                    NotifyFilter =
                        NotifyFilters.Attributes |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.FileName |
                        NotifyFilters.DirectoryName,
                    EnableRaisingEvents = Enabled_
                };
                watcher.Created += OnCreatedOdChanged;
                watcher.Changed += OnCreatedOdChanged;
                watcher.Renamed += OnRenamed;
                watcher.Deleted += OnDelete;
            }

            private void OnCreatedOdChanged(object sender, FileSystemEventArgs e)
            {
                try
                {
#if LOG
                    WriteLog(nameof(OnCreatedOdChanged), e.FullPath);
#endif
                    lock (FileToTread)
                        if (FileToTread.TryGetValue(e.FullPath, out FileToTreadInfo info))
                            info.Ticks = DateTime.Now.Ticks;
                        else
                            FileToTread.Add(e.FullPath, new FileToTreadInfo(Algorithm));
                }
                catch (Exception ex)
                {
#if LOG
                    WriteLog(nameof(OnCreatedOdChanged), ex);
#endif
                    throw ex;
                }
            }
            private void OnRenamed(object sender, RenamedEventArgs e)
            {
                try
                {
#if LOG
                    WriteLog(nameof(OnRenamed), e.OldFullPath + " => " + e.FullPath);
#endif
                    lock (FileToTread)
                    {
                        if (FileToTread.TryGetValue(e.OldFullPath, out FileToTreadInfo info))
                        {
                            FileToTread.Remove(e.OldFullPath);
                            info.Ticks = DateTime.Now.Ticks;
                            FileToTread.Add(e.FullPath, info);
                        }
                        else
                            FileToTread.Add(e.FullPath, new FileToTreadInfo(Algorithm));
                    }
                }
                catch (Exception ex)
                {
#if LOG
                    WriteLog(nameof(OnRenamed), ex);
#endif
                    throw ex;
                }
            }
            private void OnDelete(object sender, FileSystemEventArgs e)
            {
                try
                {
#if LOG
                    WriteLog(nameof(OnDelete), e.FullPath);
#endif
                    lock (FileToTread)
                        if (FileToTread.TryGetValue(e.FullPath, out FileToTreadInfo info))
                            FileToTread.Remove(e.FullPath);
                }
                catch (Exception ex)
                {
#if LOG
                    WriteLog(nameof(OnDelete), ex);
#endif
                    throw ex;
                }
            }

            public void Set(RegistryKey key, int i)
            {
                string watcher = "Watcher" + i.ToString();
                key.SetValue($"{watcher}.PathName", PathName);
                key.SetValue($"{watcher}.SubFolder", SubFolder);
                key.SetValue($"{watcher}.Algorithm", Algorithm);
            }

            public static Watcher Get(RegistryKey key, int i)
            {
                string watcher = "Watcher" + i.ToString();
                string pathName = (string)key.GetValue($"{watcher}.PathName");
                if (pathName is object)
                {
                    if (Enum.TryParse((string)key.GetValue($"{watcher}.Algorithm"), out CompressionAlgorithm algorithm))
                        return new Watcher(pathName, bool.TryParse((string)key.GetValue($"{watcher}.SubFolder"), out bool subfolder) && subfolder, algorithm);
                }
                return null;
            }

            private bool disposedValue;
            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        Enabled = false;
                        Thread init = InitialisationThread;
                        if (init is object && init.IsAlive)
                            init.Join();
                        watcher.Dispose();
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

        private readonly List<Watcher> watchers = new List<Watcher>();

        public Service()
        {
            InitializeComponent();
        }

        private Thread ProcessingThread = null;

        private const int us_Ticks = 10;
        private const int ms_Ticks = 1000 * us_Ticks;
        private const int s_Ticks = 1000 * ms_Ticks;
        bool Continue = true;
        internal void Process()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName))
                {
                    Watcher watcher;

                    int i = 0;
                    while ((watcher = Watcher.Get(key, ++i)) is object)
                    {
                        watchers.Add(watcher);
                        watcher.Enabled = true;
                    }

                    key.Close();

                    if (i == 1)
                        return;
                }

                ProcessingThread = new Thread(() =>
                {
                    try
                    {
                        while (Continue)
                        {
                            List<KeyValuePair<string, FileToTreadInfo>> toTreats;
                            long ticks = DateTime.Now.Ticks;
                            lock (FileToTread)
                                toTreats = FileToTread.Where(e => (ticks - e.Value.Ticks) > 60 * s_Ticks).ToList();

                            if (toTreats.Count == 0)
                                Thread.Sleep(1000);
                            else
                            {
                                foreach (var toTreat in toTreats)
                                    if (Compact(toTreat.Key, toTreat.Value.Algorithm))
                                        toTreat.Value.Ticks = long.MaxValue;

                                lock (FileToTread)
                                    foreach (string key in toTreats.Where(e => e.Value.Ticks == long.MaxValue).Select(e => e.Key))
                                        FileToTread.Remove(key);
                            }
                        }
                        ProcessingThread = null;
                    }
                    catch (Exception e)
                    {
#if LOG
                        WriteLog(nameof(ProcessingThread), e);
#endif
                    }
                })
                { Priority = ThreadPriority.BelowNormal };
                ProcessingThread.Start();
            }
            catch (Exception e)
            {
#if LOG
                WriteLog(nameof(Process), e);
#endif
                throw e;
            }
        }

        protected override void OnStart(string[] args)
        {
#if LOG
            WriteLog(nameof(OnStart), "Start service");
#endif
            Process();
        }

        protected override void OnStop()
        {
#if LOG
            WriteLog(nameof(OnStop), "Stoping service...");
#endif
            try
            {
                Continue = false;
                foreach (Watcher watcher in watchers)
                    watcher.Enabled = false;

                foreach (Watcher watcher in watchers)
                    watcher.Dispose();

                Thread process = ProcessingThread;
                if (process is object && process.IsAlive)
                    ProcessingThread.Join();
#if LOG
                WriteLog(nameof(OnStop), "Stoped");
                log.Close();
#endif
            }
            catch (Exception e)
            {
#if LOG
                WriteLog(nameof(OnStop), e);
#endif
                throw e;
            }
        }
    }
}
