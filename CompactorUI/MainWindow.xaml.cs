using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Compactor;

namespace CompactorUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Compactor.Compactor.SetCompression(new DirectoryInfo(@"D:\Games\Epic Games\"), CompressionAlgorithm.LZX, true, 0.95);
        }

        private void Window_Activated(object sender, EventArgs e)
        {
        }
    }
}
