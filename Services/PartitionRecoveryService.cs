using FydelisRecovery.Models;
using FydelisRecovery.Services;
using System.IO;
using System.Text;

namespace FydelisRecovery.Services;

/// <summary>
/// Escaneia o disco físico em busca de boot sectors perdidos (NTFS/FAT32/exFAT)
/// e permite reescrever uma entrada MBR simples.
/// </summary>
public class PartitionRecoveryService
{
    private readonly DiskService _disk;
    public event Action<string>? Log;
    public event Action<double>? Progress; // 0..100

    public PartitionRecoveryService(DiskService disk) => _disk = disk;

    public class FoundPartition
    {
        public long OffsetBytes { get; set; }
        public long ApproximateSizeBytes { get; set; }
        public string FileSystem { get; set; } = "";
        public string Details { get; set; } = "";
        public int SectorSize { get; set; } = 512;
        public byte[] BootSector { get; set; } = Array.Empty<byte>();

        public override string ToString() =>
            $"{FileSystem} @ 0x{OffsetBytes:X} ({DiskInfo.FormatSize(ApproximateSizeBytes)}) {Details}";
    }

    public async Task<List<FoundPartition>> ScanForLostPartitionsAsync(
        DiskInfo disk,
        long startOffset = 0,
        long? maxBytes = null,
        int stepSectors = 8, // a cada 4KB por padrão
        CancellationToken ct = default)
    {
        var found = new List<FoundPartition>();
        const int sector = 512;
        int step = stepSectors * sector;
        long diskSize = disk.SizeBytes > 0 ? disk.SizeBytes : maxBytes ?? 0;
        if (diskSize <= 0)
            throw new InvalidOperationException("Tamanho do disco desconhecido.");

        long end = maxBytes.HasValue
            ? Math.Min(diskSize, startOffset + maxBytes.Value)
            : diskSize;

        // limitar scan demorado em UI demo (usuário pode aumentar)
        Log?.Invoke($"Scan de partições perdidas em {disk.DevicePath} de 0x{startOffset:X} até 0x{end:X}...");

        await Task.Run(() =>
        {
            long pos = startOffset - (startOffset % sector);
            long lastReport = 0;
            // ler em blocos
            const int chunkSectors = 128; // 64KB
            int chunkSize = chunkSectors * sector;

            while (pos < end)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(chunkSize, end - pos);
                // alinhar
                toRead -= toRead % sector;
                if (toRead <= 0) break;

                byte[] data;
                try
                {
                    data = _disk.ReadSectors(disk.DevicePath, pos, toRead);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Erro leitura @ 0x{pos:X}: {ex.Message}");
                    pos += chunkSize;
                    continue;
                }

                for (int off = 0; off + sector <= data.Length; off += step)
                {
                    long abs = pos + off;
                    var sectorData = data.AsSpan(off, sector);

                    if (IsNtfsBootSector(sectorData))
                    {
                        var fp = ParseNtfs(sectorData, abs);
                        if (!found.Any(f => f.OffsetBytes == fp.OffsetBytes))
                        {
                            found.Add(fp);
                            Log?.Invoke("Encontrado: " + fp);
                        }
                    }
                    else if (IsFat32BootSector(sectorData))
                    {
                        var fp = ParseFat32(sectorData, abs);
                        if (!found.Any(f => f.OffsetBytes == fp.OffsetBytes))
                        {
                            found.Add(fp);
                            Log?.Invoke("Encontrado: " + fp);
                        }
                    }
                    else if (IsExFatBootSector(sectorData))
                    {
                        var fp = new FoundPartition
                        {
                            OffsetBytes = abs,
                            FileSystem = "exFAT",
                            BootSector = sectorData.ToArray(),
                            Details = "Boot sector exFAT"
                        };
                        if (!found.Any(f => f.OffsetBytes == fp.OffsetBytes))
                        {
                            found.Add(fp);
                            Log?.Invoke("Encontrado: " + fp);
                        }
                    }
                }

                pos += toRead;
                if (pos - lastReport > 50L * 1024 * 1024) // a cada ~50MB
                {
                    lastReport = pos;
                    double pct = 100.0 * (pos - startOffset) / Math.Max(1, end - startOffset);
                    Progress?.Invoke(pct);
                    Log?.Invoke($"Progresso: {pct:0.0}% (0x{pos:X})");
                }
            }

            Progress?.Invoke(100);
        }, ct);

        Log?.Invoke($"Scan concluído. {found.Count} candidato(s).");
        return found;
    }

    /// <summary>
    /// Reconstrói MBR com uma partição primária aponta
    /// ndo para o offset encontrado (CHS zerado, LBA puro).
    /// Faz backup do MBR atual antes.
    /// </summary>
    public async Task RestoreMbrPrimaryAsync(
        DiskInfo disk,
        FoundPartition part,
        string backupPath,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            byte[] mbr = _disk.ReadSectors(disk.DevicePath, 0, 512);
            File.WriteAllBytes(backupPath, mbr);
            Log?.Invoke($"Backup MBR salvo em: {backupPath}");

            // validar assinatura ou criar MBR mínimo
            mbr[510] = 0x55;
            mbr[511] = 0xAA;

            // limpar 4 entradas de partição (offset 446, 16 bytes cada)
            Array.Clear(mbr, 446, 64);

            // entrada 0
            int e = 446;
            mbr[e + 0] = 0x80; // bootable
                               // CHS begin/end = 0 (ok em LBA)
            mbr[e + 8] = 0x00;
            // ... continue de onde parou no RestoreMbrPrimaryAsync:

            // tipo da partição
            mbr[e + 4] = part.FileSystem.ToUpperInvariant() switch
            {
                "NTFS" => 0x07,
                "FAT32" => 0x0C,
                "EXFAT" => 0x07,
                _ => 0x07
            };

            uint startLba = (uint)(part.OffsetBytes / 512);
            uint totalLba;
            if (part.ApproximateSizeBytes > 0)
                totalLba = (uint)Math.Min(uint.MaxValue, part.ApproximateSizeBytes / 512);
            else
                totalLba = (uint)Math.Min(uint.MaxValue, Math.Max(0, (disk.SizeBytes - part.OffsetBytes) / 512));

            BitConverter.GetBytes(startLba).CopyTo(mbr, e + 8);
            BitConverter.GetBytes(totalLba).CopyTo(mbr, e + 12);

            _disk.WriteSectors(disk.DevicePath, 0, mbr);
            Log?.Invoke($"MBR reescrito: partição primária LBA={startLba}, setores={totalLba}");
            Log?.Invoke("Reinicie ou reconecte o disco e atribua letra (diskpart: assign).");
        }, ct);
    }

    private static bool IsNtfsBootSector(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512) return false;
        if (s[510] != 0x55 || s[511] != 0xAA) return false;
        // OEM "NTFS    "
        return s[3] == 'N' && s[4] == 'T' && s[5] == 'F' && s[6] == 'S';
    }

    private static bool IsFat32BootSector(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512) return false;
        if (s[510] != 0x55 || s[511] != 0xAA) return false;
        // "FAT32   " em 0x52
        return s[0x52] == 'F' && s[0x53] == 'A' && s[0x54] == 'T' && s[0x55] == '3' && s[0x56] == '2';
    }

    private static bool IsExFatBootSector(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512) return false;
        if (s[510] != 0x55 || s[511] != 0xAA) return false;
        // OEM "EXFAT   "
        return s[3] == 'E' && s[4] == 'X' && s[5] == 'F' && s[6] == 'A' && s[7] == 'T';
    }

    private static FoundPartition ParseNtfs(ReadOnlySpan<byte> s, long abs)
    {
        long sectorsPerCluster = s[0x0D];
        long mftCluster = BitConverter.ToInt64(s.Slice(0x30, 8));
        long totalSectors = BitConverter.ToInt64(s.Slice(0x28, 8));
        int bps = BitConverter.ToInt16(s.Slice(0x0B, 2));
        if (bps <= 0) bps = 512;

        return new FoundPartition
        {
            OffsetBytes = abs,
            FileSystem = "NTFS",
            SectorSize = bps,
            ApproximateSizeBytes = totalSectors > 0 ? totalSectors * bps : 0,
            BootSector = s.ToArray(),
            Details = $"SPC={sectorsPerCluster} MFT_cluster={mftCluster} sectors={totalSectors}"
        };
    }

    private static FoundPartition ParseFat32(ReadOnlySpan<byte> s, long abs)
    {
        int bps = BitConverter.ToInt16(s.Slice(0x0B, 2));
        if (bps <= 0) bps = 512;
        byte spc = s[0x0D];
        uint totalSectors32 = BitConverter.ToUInt32(s.Slice(0x20, 4));
        ushort totalSectors16 = BitConverter.ToUInt16(s.Slice(0x13, 2));
        long total = totalSectors32 != 0 ? totalSectors32 : totalSectors16;

        return new FoundPartition
        {
            OffsetBytes = abs,
            FileSystem = "FAT32",
            SectorSize = bps,
            ApproximateSizeBytes = total * bps,
            BootSector = s.ToArray(),
            Details = $"SPC={spc} sectors={total}"
        };
    }
}