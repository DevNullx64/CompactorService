#define _LOG
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
        protected const int OnStopEventId = 0;
        protected const int OnFileEventId = 1;
        protected static AutoResetEvent OnFileEvent = new AutoResetEvent(false);
        protected static ManualResetEvent OnStopEvent = new ManualResetEvent(false);
        protected static EventWaitHandle[] OnEvents = { OnStopEvent, OnFileEvent };
        protected static bool Sleep(int millisecondsTimeout) => OnStopEvent.WaitOne(millisecondsTimeout);
        protected static bool IsStopping => OnStopEvent.WaitOne(0);

        #region Log
#if LOG
        private static readonly object _logLocker = new object();
        protected static void WriteLog(string fncName, string message)
        {
            lock (_logLocker)
            {
                message = DateTime.Now.ToString() + " " + fncName + "(): " + message;
#if DEBUG
                Debug.WriteLine(message);
#else
                Trace.WriteLine(message);
#endif
            }
        }

        protected static void WriteLog(string fncName, string message, Exception exception)
        {
            WriteLog(fncName, message + " => " + exception.Message);
        }
#endif
#endregion

        private class FileToTreadInfo
        {
            public string Filename;
            public readonly CompressionAlgorithm Algorithm;
            public long Ticks;

            public FileToTreadInfo(string filename, CompressionAlgorithm algorithm)
            {
                Filename = filename;
                Algorithm = algorithm;
                Ticks = DateTime.Now.Ticks;
            }
        }

        private static readonly Dictionary<string, FileToTreadInfo> _ToTreats = new Dictionary<string, FileToTreadInfo>();

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
                        ? Compactor.PInvok.Kernel32.NtfsCompression.LZNT1
                        : Compactor.PInvok.Kernel32.NtfsCompression.NONE);
                    return true;
                }
                else
                    return Compact(new FileInfo(path), algorithm);
            }
            catch (Exception e)
            {
#if LOG
                WriteLog(nameof(Compact), path, e);
#endif
                return false;
            }
        }

        private class Watcher : IDisposable
        {
            private Thread InitialisationThread = null;
            public string PathName => watcher.Path;
            public bool SubFolder => watcher.IncludeSubdirectories;
            public readonly CompressionAlgorithm Algorithm;
            public bool IsInitializing => InitialisationThread is object;

            private void Initialise(string directory)
            {
                if (Directory.Exists(directory))
                {
                    try
                    {
#if LOG
                        WriteLog(nameof(Initialise), directory + " intializing...");
#endif
                        foreach (string subDirectory in Directory.EnumerateDirectories(directory))
                            if (IsStopping)
                                return;
                            else
                                Initialise(subDirectory);
                        foreach (string filename in Directory.EnumerateFiles(directory))
                            if (IsStopping)
                                return;
                            else
                                Compact(filename, Algorithm);
#if LOG
                        WriteLog(nameof(Initialise), directory + " intialized");
#endif
                    }
                    catch (Exception e)
                    {
#if LOG
                        WriteLog(nameof(Initialise), directory + " Error\n" + e.Message);
#endif
                    }
                }
            }

            private bool Enabled_ = false;
            public bool Enabled
            {
                get => Enabled_;
                set
                {
                    if (Enabled_ != value && (Enabled_ = value))
                    {
                        InitialisationThread = new Thread(() =>
                        {
#if LOG
                            try
                            {
                                WriteLog(nameof(InitialisationThread), watcher.Path + " intializing...");
#endif
                                Initialise(watcher.Path);
                                watcher.EnableRaisingEvents = Enabled_;
                                InitialisationThread = null;
#if LOG
                                WriteLog(nameof(InitialisationThread), watcher.Path + " intialized");
                            }
                            catch (Exception e)
                            {
                                WriteLog(nameof(InitialisationThread), watcher.Path + " Error\n" + e.Message);
                            }
#endif
                        })
                        { Priority = ThreadPriority.Lowest };
                        InitialisationThread.Start();
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
                watcher.Created += OnCreatedOrChanged;
                watcher.Changed += OnCreatedOrChanged;
                watcher.Renamed += OnRenamed;
                watcher.Deleted += OnDelete;
            }

            private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
            {
#if LOG
                try
                {
                    WriteLog(nameof(OnCreatedOrChanged), e.FullPath);
#endif
                    if (!(Directory.Exists(e.FullPath) || IsStopping))
                    {
                        lock (_ToTreats)
                            if (_ToTreats.TryGetValue(e.FullPath, out FileToTreadInfo info))
                                info.Ticks = DateTime.Now.Ticks;
                            else
                                _ToTreats.Add(e.FullPath, new FileToTreadInfo(e.FullPath, Algorithm));
                        OnFileEvent.Set();
                    }
#if LOG
                }
                catch (Exception ex)
                {
                    WriteLog(nameof(OnCreatedOrChanged), e.FullPath, ex);
                    throw ex;
                }
#endif
            }
            private void OnRenamed(object sender, RenamedEventArgs e)
            {
#if LOG
                try
                {
                    WriteLog(nameof(OnRenamed), e.OldFullPath + " => " + e.FullPath);
#endif
                    if (!(Directory.Exists(e.FullPath) || IsStopping))
                        lock (_ToTreats)
                        {
                            if (_ToTreats.TryGetValue(e.OldFullPath, out FileToTreadInfo info))
                            {
                                info.Filename = e.FullPath;
                                info.Ticks = DateTime.Now.Ticks;
                                _ToTreats.Remove(e.OldFullPath);
                                _ToTreats.Add(e.FullPath, info);
                            }
                            else
                            {
                                _ToTreats.Add(e.FullPath, new FileToTreadInfo(e.FullPath, Algorithm));
                                OnFileEvent.Set();
                            }
                        }
#if LOG
                }
                catch (Exception ex)
                {
                    WriteLog(nameof(OnRenamed), e.FullPath, ex);
                    throw ex;
                }
#endif
            }
            private void OnDelete(object sender, FileSystemEventArgs e)
            {
#if LOG
                try
                {
                    WriteLog(nameof(OnDelete), e.FullPath);
#endif
                    if (!IsStopping)
                        lock (_ToTreats)
                            _ToTreats.Remove(e.FullPath);
#if LOG
                }
                catch (Exception ex)
                {
                    WriteLog(nameof(OnDelete), e.FullPath, ex);
                    throw ex;
                }
#endif
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
#if LOG
            TextWriterTraceListener listener = new TextWriterTraceListener("C:\\log.txt");
#if DEBUG
            Debug.Listeners.Add(listener);
            Debug.AutoFlush = true;
#else
            Trace.Listeners.Add(listener);
            Trace.AutoFlush = true;
#endif
#endif
            InitializeComponent();
        }

        private const int us_Ticks = 10;
        private const int ms_Ticks = 1000 * us_Ticks;
        private const int s_Ticks = 1000 * ms_Ticks;

        private static FileToTreadInfo NextToTreat(long ticks)
        {
            lock (_ToTreats)
            {
                IEnumerator<FileToTreadInfo> iterator = _ToTreats.Values.GetEnumerator();
                if (iterator.MoveNext())
                {
                    FileToTreadInfo result = iterator.Current;
                    while (iterator.MoveNext())
                        if (result.Ticks > iterator.Current.Ticks)
                            result = iterator.Current;
                    return (result.Ticks > ticks)
                        ? result
                        : null;
                }
                else
                    return null;
            }
        }

        private readonly Thread ProcessingThread = new Thread(() =>
        {
#if LOG
            try
            {
#endif
                FileToTreadInfo toTreat;
                while (WaitHandle.WaitAny(OnEvents, 60000) != OnStopEventId)
                    while ((toTreat = NextToTreat(DateTime.Now.Ticks)) is object && !IsStopping)
                        if (Compact(toTreat.Filename, toTreat.Algorithm))
                            lock (_ToTreats)
                                _ToTreats.Remove(toTreat.Filename);
                        else
                            toTreat.Ticks += 60 * s_Ticks;
#if LOG
            }
            catch (Exception e)
            {
                WriteLog(nameof(ProcessingThread), "", e);
            }
#endif
        })
        { Priority = ThreadPriority.BelowNormal };

        internal void Process()
        {
#if LOG
            try
            {
#endif
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

                ProcessingThread.Start();
#if LOG
            }
            catch (Exception e)
            {
                WriteLog(nameof(Process), "", e);
                throw e;
            }
#endif
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
            try
            {
#endif
                OnStopEvent.Set();
                foreach (Watcher watcher in watchers)
                    watcher.Enabled = false;

                foreach (Watcher watcher in watchers)
                    watcher.Dispose();

                Thread process = ProcessingThread;
                if (process is object && process.IsAlive)
                    ProcessingThread.Join();
#if LOG
                WriteLog(nameof(OnStop), "Stoped");
            }
            catch (Exception e)
            {
                WriteLog(nameof(OnStop), "", e);
                throw e;
            }
#endif
        }
    }
}
