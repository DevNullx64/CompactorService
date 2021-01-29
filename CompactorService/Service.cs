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
        private static readonly Dictionary<char, object> Lockers = new Dictionary<char, object>();
        private static readonly ConcurrentDictionary<string, CompressionAlgorithm> FileToTread = new ConcurrentDictionary<string, CompressionAlgorithm>();
        private static bool Continue = true;

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
                        return true;
                    }
                    else
                        return false;
                }
            }
        }
        private static bool Compact(string fileName, CompressionAlgorithm algorithm)
            => Compact(new FileInfo(fileName), algorithm);

        private class Watcher : IDisposable
        {
            private Thread InitialisationThread;
            private bool Enabled_ = false;
            public string PathName => watcher.Path;
            public bool SubFolder => watcher.IncludeSubdirectories;
            public readonly CompressionAlgorithm Algorithm;

            private void Initialise(DirectoryInfo directory)
            {
                Compactor.Compactor.SetCompression(directory,
                    (Algorithm == CompressionAlgorithm.LZNT1)
                    ? CompressionAlgorithm.LZNT1
                    : CompressionAlgorithm.NONE);
                foreach (DirectoryInfo subDirectory in directory.EnumerateDirectories())
                    if (Continue)
                        Initialise(subDirectory);
                    else return;
                foreach (FileInfo filename in directory.EnumerateFiles())
                    if (Continue)
                        Compact(filename, Algorithm);
                    else return;
            }
            private void Initialise(string path)
                => Initialise(new DirectoryInfo(path));

            public bool Enabled
            {
                get => Enabled_;
                set
                {
                    if (Enabled_ != value)
                    {
                        Enabled_ = value;
                        watcher.EnableRaisingEvents = Enabled_;
                        if (Enabled_)
                            (InitialisationThread =
                            new Thread(() =>
                            {
                                Initialise(watcher.Path);
                                InitialisationThread = null;
                            })
                            { Priority = ThreadPriority.Lowest })
                            .Start();
                    }
                }
            }

            private readonly FileSystemWatcher watcher;
            private bool disposedValue;

            public Watcher(string pathName, bool subFolder, CompressionAlgorithm algorithm)
            {
                Algorithm = algorithm;

                watcher = new FileSystemWatcher
                {
                    Path = pathName,
                    Filter = "*.*",
                    IncludeSubdirectories = subFolder,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = Enabled_
                };
                FileSystemEventHandler onChanged = new FileSystemEventHandler(OnChanged);
                watcher.Changed += onChanged;
                watcher.Created += onChanged;

                if (!Lockers.ContainsKey(pathName[0]))
                    Lockers.Add(pathName[0], new object());
            }

            private void OnChanged(object sender, FileSystemEventArgs e)
                => FileToTread.TryAdd(e.FullPath, Algorithm);

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

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        Enabled = false;
                        Thread init = InitialisationThread;
                        if (init != null)
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

        private Thread ProcessingThread;

        internal void Process()
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName))
            {
                Watcher watcher;
#if DEBUG
                watcher = new Watcher(@"D:\Games\", true, CompressionAlgorithm.LZX);
                watcher.Set(key, 1);
#endif

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

            (ProcessingThread =
                new Thread(() =>
            {
                while (Continue)
                {
                    foreach (var toTreat in FileToTread)
                        if (Compact(toTreat.Key, toTreat.Value))
                            FileToTread.TryRemove(toTreat.Key, out _);
                    Thread.Sleep(1000);
                }
            })
                { Priority = ThreadPriority.BelowNormal })
            .Start();
        }

        protected override void OnStart(string[] args)
        {
            Process();
        }

        protected override void OnStop()
        {
            Continue = false;
            foreach (Watcher watcher in watchers)
                watcher.Enabled = false;
            
            foreach (Watcher watcher in watchers)
                watcher.Dispose();
            ProcessingThread.Join();
        }
    }
}