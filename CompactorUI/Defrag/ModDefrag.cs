using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Linq;
using Compactor.PInvok;

namespace CompactorUI.Defrag
{
    class ModDefrag
    {
        public const int OPEN_EXISTING = 3;
        public const int FILE_SHARE_READ = 0x1;
        public const int FILE_SHARE_WRITE = 0x2;
        public const int DELETE = 0x10000;
        public const int FILE_READ_ACCESS = 0x1;

        //indique que le cluster ne peut pas être déplacé à l'endroit spécifié
        public const int ERROR_INVALID_PARAMETER = 87;
        public const int ERROR_ACCESS_DENIED = 5;



        //ouvre un fichier
        [DllImport("kernel32.dll", EntryPoint = "CreateFile", CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string lpFileName, int dwDesiredAccess, int dwShareMode, int lpSecurityAttributes, int dwCreationDisposition, int dwFlagsAndAttributes, int hTemplateFile);

        //permet de stopper le traitement en cours
        public static bool bStop;

        //contient une partie de la carte du volume
        public class VolumeFreeClusters
        {
            public long startLCN;
            public long ClusterCount;
        }

        //contient des informations sur les clusters d'un fichier
        public class fileClusters
        {
            public string File;
            public bool Moveable;
            public List<kernel32.Extent> Extents = new List<kernel32.Extent>();
        }

        //récupère la liste des clusters du fichier File
        //==============================================
        //File : fichier dont on veut la liste des fragments
        //renvoie une structure
        public static fileClusters GetFileBitmap(string File)
        {
            fileClusters result = new fileClusters();

            //ouvre le fichier avec les droits de la déplacer juste pour voir si on pourrait le déplacer
            SafeFileHandle hFile = CreateFile(File, FILE_READ_ACCESS | DELETE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            //copie le nom du fichier
            result.File = File;
            //si on ne peut pas l'ouvrir pour déplacement
            if (hFile.IsInvalid)
            {
                //pas déplacable
                result.Moveable = false;
                //on essaie de l'ouvrir en lecture
                hFile = kernel32.CreateFile(File, FileAccess.Read, FileShare.ReadWrite, FileMode.Open, kernel32.CreateFileFlag.NONE);
                //si pas possible : fichier système vital
                if (hFile.IsInvalid)
                    return result;
            }
            else
                //déplacable
                result.Moveable = true;

            kernel32.RETRIEVAL_POINTERS_BUFFER FileBitmap = kernel32.GetRetrievalPointers(hFile);
            result.Extents = new List<kernel32.Extent>(FileBitmap.Extents);

            return result;
        }

        //récupère la liste des fragments par fichier (peut être très long retrouver)
        //===========================================================================
        //Volume : nom du volume dont on veut la liste des clusters par fichier (forme c:\)
        //Progress : progressbar pour afficher la progression de l'opération
        //renvoie un tableau de clusters par fichier pour chaque fichier du volume
        public static fileClusters[] GetVolumeFilesBitmap(string Volume, ref double Progress)
        {
            List<fileClusters> result = new List<fileClusters>();

            Progress = 0;

            DirectoryInfo volume = new DirectoryInfo(Volume);
            foreach (string filename in volume.EnumerateFiles("**.*", SearchOption.AllDirectories).Select(e => e.FullName))
            {
                if (bStop)
                    break;
                else
                    result.Add(GetFileBitmap(filename));
            }

            return result.ToArray();
        }

        //défragmente un fichier pour rassembler tout ses extents
        //=======================================================
        //szFileName : nom du fichier à défragmenter
        //renvoie 0 si succès, un code d'erreur si le déplacement de clusters échoue
        //(ACCESS_DENIED indique qu'une écriture a eu lieu à l'emplacement où l'on voulait mettre le fichier entier
        //(donc qu'on a essayé de déplacer vers un cluster déjà alloué)
        // ce qui fait que la carte du lecteur que la fonction possède est obsolète
        //et qu'il faudra donc réessayer)
        public static int DefragmentFile(string szFileName)
        {
            //compteurs
            //nouvel emplacement des extents déplacés, VCN du dernier extent déplacé,nombre de clusters de l'extent en cours à déplacer
            long newLCN, fPrevClusterVCN;
            //handle du fichier à déplacer, handle du volume contenant le fichier à déplacer

            try
            {

                //ouvre le volume
                SafeFileHandle volHandle = OpenVolume(szFileName.Substring(0, Math.Min(2, szFileName.Length)));
                if (volHandle.IsInvalid)
                    return Marshal.GetLastWin32Error();

                //récupère la liste des extents libres du volume
                VolumeFreeClusters[] volFreeClusters = GetVolumeBitmap(szFileName.Substring(0, Math.Min(2, szFileName.Length)));
                //récupère la liste des extents du fichier
                fileClusters fClusters = GetFileBitmap(szFileName);

                //si le fichier est fragmenté
                if (fClusters.Extents.Count > 1)
                {
                    int result = 0;
                    //on ouvre le fichier
                    SafeFileHandle fHandle = kernel32.CreateFile(szFileName, FileAccess.Read, FileShare.ReadWrite, FileMode.Open, kernel32.CreateFileFlag.NONE);
                    if (fHandle.IsInvalid)
                    {
                        result = Marshal.GetLastWin32Error();
                        return result;
                    }

                    //pour chaque extents libre du volume
                    for (int x = 0; x < volFreeClusters.Length; x++)
                    {
                        var volFreeCluster = volFreeClusters[x];
                        //on regarde si le fichier tient dedans en entier
                        //TOCHECK : HighDWORD
                        if (volFreeCluster.ClusterCount >= fClusters.Extents[^1].NextVcn)
                        {
                            long fClusterCount;
                            //on commence au début de cet extent libre
                            newLCN = volFreeCluster.startLCN;
                            //de l'offset 0 dans le fichier
                            fPrevClusterVCN = 0;
                            //pour chaque extent du fichier
                            for (int y = 0; y < fClusters.Extents.Count; y++)
                            {
                                //on calcule le nombre de cluster de l'extent en cours (avec les offsets de l'extent précédent et suivant)
                                //TOCHECK : HighDWORD
                                fClusterCount = fClusters.Extents[y].NextVcn - fPrevClusterVCN;
                                //essaie de déplacer l'extent
                                kernel32.MoveFile(volHandle, fHandle, fPrevClusterVCN, (uint)fClusterCount, newLCN);
                                //offset de l'extent que l'on vient de déplacer
                                fPrevClusterVCN = fClusters.Extents[y].NextVcn;
                                //avance le LCN pour placer l'extent suivant juste après celui que l'on vient de déplacer
                                //TOCHECK : HighDWORD
                                newLCN += fClusterCount;
                            }
                            break;
                        }
                    }
                }

                return 0;

            }
            catch
            {
                return -1;
            }
        }

        //obtient un handle du volume spécifié
        //====================================
        //Volume : lettre du volume suivi de ":" (par ex : "C:")
        public static SafeFileHandle OpenVolume(string Volume) 
            => kernel32.CreateFile("\\\\.\\" + Volume, FileAccess.Read, FileShare.ReadWrite, FileMode.Open, kernel32.CreateFileFlag.NONE);

        private static IList<VolumeFreeClusters>  GetExtents(IList<ulong> bitmap, int index, ref VolumeFreeClusters lastExtents)
        {
            List<VolumeFreeClusters> buffer = new List<VolumeFreeClusters>();
            ulong value = bitmap[index];
            if (value == 0 && lastExtents == null)
                buffer.Add(
                    lastExtents = new VolumeFreeClusters()
                    {
                        startLCN = index * sizeof(ulong)
                    });
            else if (value == ulong.MaxValue && lastExtents != null)
            {
                lastExtents.ClusterCount = lastExtents.startLCN - (index * sizeof(ulong));
                lastExtents = null;
            }
            else
            {

            }
            return buffer;
        }

        //traite une partie de carte de clusters pour récupérer les extents libres
        private static VolumeFreeClusters[] ProcessBitmap(kernel32.VOLUME_BITMAP_BUFFER Map)
        {
            List<VolumeFreeClusters> ret = new List<VolumeFreeClusters>();

            //init les puissances de 2
            int nClustersFree = 0;
            VolumeFreeClusters lastExtent = null;

            //pour chaque cluster de la partie de carte
            for (int x = 0; x <= Map.Bitmap.Length - 1; x++)
            {
                if (Map.Bitmap[x] == 0)
                {
                    if (lastExtent!=null)
                        lastExtent = new VolumeFreeClusters()
                        {
                            startLCN = (x * sizeof(ulong))
                        };
                }
                else if ((Map.Bitmap[x] != ulong.MaxValue) || (nClustersFree != 0))
                    for (int x2 = 0; x2 < sizeof(ulong); x2++)
                        if ((Map.Bitmap[x] & (1ul << x2)) != 0)
                        {
                            //le cluster est occupé

                            //si on sort d'une série de clusters libres : un extent
                            if (nClustersFree != 0)
                            {
                                //on l'ajoute à la liste des extents libres
                                VolumeFreeClusters u = new VolumeFreeClusters
                                {
                                    //nombre de clusters
                                    ClusterCount = nClustersFree,
                                    //position sur le volume
                                };
                                //repart de 0 cluster libre pour le prochain extent
                                nClustersFree = 0;
                                ret.Add(u);
                            }
                            //sinon le cluster est libre
                        }
                        else
                            //on ajoute 1 au compteur de cluster libre de l'extent en cours
                            nClustersFree += 1;
            }

            //et qu'il y a un extent libre à la fin du volume
            if (nClustersFree != 0)
                //on ne l'oublie pas
                ret.Add(new VolumeFreeClusters
                {
                    //nombre de clusters libres
                    ClusterCount = nClustersFree,
                    //position sur le volume
                    startLCN = nClustersFree
                });

            return ret.ToArray();
        }

        //récupère la liste des extents (ensemble continue de clusters) libres du volume Volume
        //======================================================
        //Volume : nom du volume dont on veut la liste des clusters libres
        //VolumeSize : taille du volume en clusters
        //renvoie la liste des clusters libres
        public static VolumeFreeClusters[] GetVolumeBitmap(string Volume)
        {
            //ouvre le volume en brute
            SafeFileHandle hVol = OpenVolume(Volume);
            //si erreur : surement pas administrateur
            if (hVol.IsInvalid)
                return Array.Empty<VolumeFreeClusters>();

            kernel32.VOLUME_BITMAP_BUFFER VolumeBitmap = kernel32.GetVolumeBitmap(hVol, 0);
            //renvoie les clusters libres
            return ProcessBitmap(VolumeBitmap);
        }
    }
}

