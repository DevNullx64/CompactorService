using System;
using System.IO;

namespace Compactor
{
    class Program
    {
        static void Main(string[] args)
        {
            //Console.CursorVisible = false;
            Compactor.SetCompression(new DirectoryInfo(@"D:\CSC\"), CompressionAlgorithm.LZX, true, 0.95, parameters =>
            {
                Console.SetCursorPosition(0, 0);
                Console.Write(string.Format("{0} done of {1} total ({2:P3})",
                    Compactor.FileLengthToString(parameters.LengthDone),
                    Compactor.FileLengthToString(parameters.LengthToDo),
                    parameters.Progress).PadRight(Console.WindowWidth));
                Console.Write(parameters.CurrentFile.FullName.PadRight(3 * Console.WindowWidth));
                return true;
            });
            Console.CursorVisible = true;
        }
    }
}