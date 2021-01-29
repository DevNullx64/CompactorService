using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Compactor
{
    public static class CompactorExtender
    {
        #region Process
        [Flags]
        private enum ThreadAccess : int
        {
            TERMINATE = (0x0001),
            SUSPEND_RESUME = (0x0002),
            GET_CONTEXT = (0x0008),
            SET_CONTEXT = (0x0010),
            SET_INFORMATION = (0x0020),
            QUERY_INFORMATION = (0x0040),
            SET_THREAD_TOKEN = (0x0080),
            IMPERSONATE = (0x0100),
            DIRECT_IMPERSONATION = (0x0200)
        }

        [DllImport("kernel32.dll")]
        private static extern SafeHandle OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, int dwThreadId);
        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(SafeHandle hThread);
        [DllImport("kernel32.dll")]
        private static extern int ResumeThread(SafeHandle hThread);

        public static void Suspend(this ProcessThread processThread)
        {
            using (SafeHandle thread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, processThread.Id))
            {
                if (thread.IsInvalid)
                    throw new Win32Exception();
                SuspendThread(thread);
                thread.Close();
            }
        }
        public static void Resume(this ProcessThread processThread)
        {
            using (SafeHandle thread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, processThread.Id))
            {
                if (thread.IsInvalid)
                    throw new Win32Exception();
                while (ResumeThread(thread) > 0) ;
                thread.Close();
            }
        }

        public static void Suspend(this Process process)
        {
            foreach (ProcessThread processThread in process.Threads)
                processThread.Suspend();
        }
        public static void Resume(this Process process)
        {
            foreach (ProcessThread processThread in process.Threads)
                processThread.Resume();
        }
        #endregion Process
    }
}