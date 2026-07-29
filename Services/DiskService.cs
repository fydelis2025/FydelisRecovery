using FydelisRecovery.Models;
using System.Management;
using System.Runtime.InteropServices;

namespace FydelisRecovery.Services;

public class DiskService
{
    public List<DiskInfo> GetPhysicalDisks()
    {
        var disks = new List<DiskInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_DiskDrive");

        foreach (ManagementObject d in searcher.Get())
        {
            var disk = new DiskInfo
            {
                Index = Convert.ToInt32(d["Index"] ?? 0),
                Model = d["Model"]?.ToString() ?? "Desconhecido",
                InterfaceType = d["InterfaceType"]?.ToString() ?? "",
                SizeBytes = Convert.ToInt64(d["Size"] ?? 0),
                SerialNumber = d["SerialNumber"]?.ToString()?.Trim() ?? ""
            };
            disk.Partitions = GetPartitions(disk.Index);
            disks.Add(disk);
        }

        return disks.OrderBy(x => x.Index).ToList();
    }

    public List<PartitionInfo> GetPartitions(int diskIndex)
    {
        var list = new List<PartitionInfo>();

        try
        {
            string query = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{diskIndex}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
            using var partSearcher = new ManagementObjectSearcher(query);

            foreach (ManagementObject part in partSearcher.Get())
            {
                try
                {
                    var p = new PartitionInfo
                    {
                        DiskIndex = diskIndex,
                        DeviceId = part["DeviceID"]?.ToString() ?? "",
                        PartitionIndex = ParsePartitionIndex(part["DeviceID"]?.ToString()),
                        Bootable = part["Bootable"] != null && Convert.ToBoolean(part["Bootable"]),
                        SizeBytes = part["Size"] != null ? Convert.ToInt64(part["Size"]) : 0,
                        Type = part["Type"]?.ToString() ?? "",
                        OffsetBytes = part["StartingOffset"] != null ? Convert.ToInt64(part["StartingOffset"]) : 0
                    };

                    string partDeviceId = part["DeviceID"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(partDeviceId))
                    {
                        string safeDeviceId = partDeviceId.Replace("\\", "\\\\");
                        using var logSearcher = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{safeDeviceId}'}} " +
                            "WHERE AssocClass=Win32_LogicalDiskToPartition");

                        foreach (ManagementObject log in logSearcher.Get())
                        {
                            p.DriveLetter = (log["DeviceID"]?.ToString() ?? "") + "\\";
                            p.FileSystem = log["FileSystem"]?.ToString() ?? "";
                            p.Label = log["VolumeName"]?.ToString() ?? "";
                            p.FreeBytes = log["FreeSpace"] != null ? Convert.ToInt64(log["FreeSpace"]) : 0;
                            if (log["Size"] != null)
                            {
                                p.SizeBytes = Convert.ToInt64(log["Size"]);
                            }
                        }
                    }

                    list.Add(p);
                }
                catch
                {
                    // Ignora partições individuais corrompidas ou inacessíveis pelo WMI
                }
            }
        }
        catch
        {
            // Retorna a lista vazia ou parcial caso o disco inteiro falhe no WMI
        }

        return list;
    }

    private static int ParsePartitionIndex(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return 0;
        var idx = deviceId.LastIndexOf('#');
        if (idx >= 0 && int.TryParse(deviceId[(idx + 1)..], out var n))
            return n;
        return 0;
    }

    public byte[] ReadSectors(string devicePath, long offset, int length)
    {
        const int sector = 512;
        long alignedOffset = offset - (offset % sector);
        int skip = (int)(offset - alignedOffset);
        int alignedLen = ((length + skip + sector - 1) / sector) * sector;

        var h = OpenDisk(devicePath, readOnly: true);
        if (h == (IntPtr)(-1) || h == IntPtr.Zero)
            throw new InvalidOperationException($"Falha ao abrir {devicePath}: {MarshalError()}");

        try
        {
            // Usando SetFilePointerEx conforme declarado no seu NativeMethods
            FydelisRecovery.Native.NativeMethods.SetFilePointerEx(h, alignedOffset, out _, 0);

            byte[] buf = new byte[alignedLen];
            if (!FydelisRecovery.Native.NativeMethods.ReadFile(h, buf, (uint)alignedLen, out uint read, IntPtr.Zero))
                throw new InvalidOperationException("Read falhou: " + MarshalError());

            byte[] result = new byte[Math.Min(length, Math.Max(0, (int)read - skip))];
            if (result.Length > 0)
                Buffer.BlockCopy(buf, skip, result, 0, result.Length);
            return result;
        }
        finally { FydelisRecovery.Native.NativeMethods.CloseHandle(h); }
    }

    public void WriteSectors(string devicePath, long offset, byte[] data)
    {
        const int sector = 512;
        if (offset % sector != 0)
            throw new ArgumentException("Offset deve ser múltiplo de 512.");
        if (data.Length % sector != 0)
            throw new ArgumentException("Tamanho dos dados deve ser múltiplo de 512.");

        var h = OpenDisk(devicePath, readOnly: false);
        if (h == (IntPtr)(-1) || h == IntPtr.Zero)
            throw new InvalidOperationException($"Falha ao abrir {devicePath}: {MarshalError()}");

        try
        {
            FydelisRecovery.Native.NativeMethods.SetFilePointerEx(h, offset, out _, 0);

            if (!FydelisRecovery.Native.NativeMethods.WriteFile(h, data, (uint)data.Length, out uint written, IntPtr.Zero) || written != data.Length)
                throw new InvalidOperationException("Write falhou: " + MarshalError());
        }
        finally { FydelisRecovery.Native.NativeMethods.CloseHandle(h); }
    }

    public static IntPtr OpenDisk(string path, bool readOnly)
    {
        uint access = FydelisRecovery.Native.NativeMethods.GENERIC_READ;
        if (!readOnly) access |= FydelisRecovery.Native.NativeMethods.GENERIC_WRITE;

        return FydelisRecovery.Native.NativeMethods.CreateFile(
            path,
            access,
            FydelisRecovery.Native.NativeMethods.FILE_SHARE_READ | FydelisRecovery.Native.NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            FydelisRecovery.Native.NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);
    }

    public static string MarshalError() =>
        new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

    public async Task CloneDiskAsync(
    string sourceDevicePath,
    string destDevicePath,
    long totalBytes,
    IProgress<int>? progress = null,
    CancellationToken cancellationToken = default)
    {
        const int bufferSize = 1024 * 1024; // Blocos de 1 MB para máxima velocidade
        byte[] buffer = new byte[bufferSize];

        IntPtr hSource = OpenDisk(sourceDevicePath, readOnly: true);
        IntPtr hDest = OpenDisk(destDevicePath, readOnly: false);

        if (hSource == (IntPtr)(-1) || hSource == IntPtr.Zero ||
            hDest == (IntPtr)(-1) || hDest == IntPtr.Zero)
        {
            throw new UnauthorizedAccessException("Erro ao abrir os discos de origem ou destino. Verifique privilégios de Administrador.");
        }

        long totalBytesCopied = 0;

        try
        {
            await Task.Run(() =>
            {
                while (totalBytesCopied < totalBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    uint bytesToRead = (uint)Math.Min(bufferSize, totalBytes - totalBytesCopied);

                    // Leitura do disco de origem
                    if (!FydelisRecovery.Native.NativeMethods.ReadFile(hSource, buffer, bytesToRead, out uint bytesRead, IntPtr.Zero) || bytesRead == 0)
                    {
                        break;
                    }

                    // Escrita no disco de destino
                    if (!FydelisRecovery.Native.NativeMethods.WriteFile(hDest, buffer, bytesRead, out uint bytesWritten, IntPtr.Zero) || bytesWritten != bytesRead)
                    {
                        throw new InvalidOperationException("Falha crítica de gravação no disco de destino durante a clonagem.");
                    }

                    totalBytesCopied += bytesWritten;

                    int percent = (int)(totalBytesCopied * 100 / totalBytes);
                    progress?.Report(Math.Min(percent, 100));
                }
            }, cancellationToken);
        }
        finally
        {
            FydelisRecovery.Native.NativeMethods.CloseHandle(hSource);
            FydelisRecovery.Native.NativeMethods.CloseHandle(hDest);
        }
    }
}