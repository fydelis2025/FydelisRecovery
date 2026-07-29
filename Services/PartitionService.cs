using FydelisRecovery.Models;
using System.Runtime.InteropServices;
using System.Text;

namespace FydelisRecovery.Services;

public class PartitionService
{
    /// <summary>
    /// Lista todos os discos físicos disponíveis.
    /// </summary>
    public List<DiskDriveInfo> EnumeratePhysicalDrives()
    {
        var drives = new List<DiskDriveInfo>();

        for (int i = 0; i < 32; i++)
        {
            string path = $@"\\.\PHYSICALDRIVE{i}";
            IntPtr h = NativeMethods.CreateFile(
                path,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (h.ToInt64() == -1) continue;

            try
            {
                var geo = new DISK_GEOMETRY();
                int size = Marshal.SizeOf(geo);
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(geo, buf, false);
                    bool ok = NativeMethods.DeviceIoControl(
                        h,
                        NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                        IntPtr.Zero,
                        0,
                        buf,
                        (uint)size,
                        out uint ret,
                        IntPtr.Zero);

                    if (ok)
                    {
                        geo = Marshal.PtrToStructure<DISK_GEOMETRY>(buf);
                        long totalSize = geo.Cylinders * geo.TracksPerCylinder *
                                         geo.SectorsPerTrack * geo.BytesPerSector;

                        drives.Add(new DiskDriveInfo
                        {
                            Index = i,
                            Model = $"PhysicalDrive{i}",
                            Size = totalSize
                        });
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        return drives;
    }

    /// <summary>
    /// Retorna partições de um disco lendo o MBR/GPT.
    /// </summary>
    public List<PartitionInfo> GetPartitions(int diskIndex)
    {
        var partitions = new List<PartitionInfo>();
        string path = $@"\\.\PHYSICALDRIVE{diskIndex}";

        IntPtr h = NativeMethods.CreateFile(
            path,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (h.ToInt64() == -1)
            throw new UnauthorizedAccessException("Execute como Administrador.");

        try
        {
            byte[] mbr = new byte[512];
            uint bytesRead;
            NativeMethods.ReadFile(h, mbr, 512, out bytesRead, IntPtr.Zero);

            if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            {
                return partitions;
            }

            for (int i = 0; i < 4; i++)
            {
                int offset = 0x1BE + i * 16;
                byte systemId = mbr[offset + 4];

                if (systemId == 0) continue;

                uint relativeSectors = BitConverter.ToUInt32(mbr, offset + 8);
                uint totalSectors = BitConverter.ToUInt32(mbr, offset + 12);

                // CORRIGIDO: Usando OffsetBytes e SizeBytes conforme a modelagem correta
                var part = new PartitionInfo
                {
                    DiskIndex = diskIndex,
                    PartitionIndex = i + 1,
                    OffsetBytes = relativeSectors * 512L,
                    SizeBytes = totalSectors * 512L,
                    Label = GetPartitionTypeLabel(systemId),
                };

                var letter = GetDriveLetterForPartition(diskIndex, part.OffsetBytes);
                if (letter.HasValue)
                {
                    part.DriveLetter = $"{letter.Value}:\\";
                }

                partitions.Add(part);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }

        return partitions;
    }

    /// <summary>
    /// Formata uma partição (Win32 FormatEx).
    /// </summary>
    public async Task<bool> FormatPartitionAsync(
        char driveLetter,
        string fileSystem = "NTFS",
        string? label = null,
        bool quickFormat = true)
    {
        return await Task.Run(() =>
        {
            string root = $@"{driveLetter}:\";
            string volPath = $@"\\.\{driveLetter}:";
            IntPtr hVol = NativeMethods.CreateFile(
                volPath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hVol.ToInt64() != -1)
            {
                uint ret;
                NativeMethods.DeviceIoControl(hVol, NativeMethods.FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                NativeMethods.DeviceIoControl(hVol, NativeMethods.FSCTL_DISMOUNT_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                NativeMethods.CloseHandle(hVol);
            }

            bool result = NativeMethods.FormatEx(
                root,
                0,
                fileSystem,
                label,
                quickFormat,
                0,
                IntPtr.Zero);

            return result;
        });
    }

    #region Helpers

    private static string GetPartitionTypeLabel(byte systemId) => systemId switch
    {
        0x01 => "FAT12",
        0x04 => "FAT16 (<32MB)",
        0x06 => "FAT16 (>32MB)",
        0x07 => "NTFS / HPFS",
        0x0B => "FAT32 (CHS)",
        0x0C => "FAT32 (LBA)",
        0x0E => "FAT16 (LBA)",
        0x0F => "Extended (LBA)",
        0x11 => "Hidden FAT12",
        0x14 => "Hidden FAT16",
        0x17 => "Hidden NTFS",
        0x1B => "Hidden FAT32",
        0x27 => "Windows RE",
        0x42 => "Dynamic Disk (LDM)",
        0x82 => "Linux Swap",
        0x83 => "Linux Native",
        0x8E => "Linux LVM",
        0xA0 => "Laptop Hibernate",
        0xAB => "Apple Boot",
        0xAF => "Apple HFS/HFS+",
        0xEE => "GPT Protective",
        0xEF => "EFI System",
        _ => $"0x{systemId:X2} Unknown"
    };

    private static char? GetDriveLetterForPartition(int diskIndex, long partitionOffset)
    {
        for (char letter = 'C'; letter <= 'Z'; letter++)
        {
            string dosName = $"{letter}:";
            StringBuilder target = new StringBuilder(256);
            uint len = NativeMethods.QueryDosDevice(dosName, target, 256);

            if (len > 0)
            {
                string devicePath = target.ToString();
                if (devicePath.Contains($"PhysicalDrive{diskIndex}") ||
                    devicePath.Contains($"Harddisk{diskIndex}"))
                {
                    return letter;
                }
            }
        }
        return null;
    }

    #endregion
}