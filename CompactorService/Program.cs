using System;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace CompactorService
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
#if (DEBUG)
            Service myServ = new Service();
            myServ.Process();
#else
            if (Environment.UserInteractive)
                try {
                    switch (string.Concat(args).ToLower())
                    {
                        case "--i":
                        case "-i":
                        case "/i":
                            ManagedInstallerClass.InstallHelper(new string[] { Assembly.GetExecutingAssembly().Location });
                            break;
                        case "--u":
                        case "-u":
                        case "/u":
                            ManagedInstallerClass.InstallHelper(new string[] { "/u", Assembly.GetExecutingAssembly().Location });
                            break;
                    }
                    Console.Out.WriteLine("Work donne.");
                }
                catch(Exception e)
                {
                    Console.Error.WriteLine(e.Message);
                }
            else
                ServiceBase.Run(new Service());
#endif
        }
    }
}