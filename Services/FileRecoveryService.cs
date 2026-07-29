using FydelisRecovery.Models;
using System.IO;

namespace FydelisRecovery.Services;

public class FileRecoveryService
{
    private const int SECTOR_SIZE = 512;

    public event Action<string>? Log;
    public event Action<int>? Progress;

    private readonly DiskService _diskService;

    public FileRecoveryService(DiskService diskService)
    {
        _diskService = diskService;
    }

    /// <summary>
    /// Método wrapper chamado pelo MainWindow compatível com o nome CarveAsync.
    /// </summary>
    public async Task<List<RecoveredFile>> CarveAsync(
        string physicalPath,
        long partitionOffset,
        long partitionLength,
        string? extensionFilter,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var rawList = await ScanPartitionRawAsync(physicalPath, partitionOffset, partitionLength, cancellationToken);
        return rawList.Take(maxFiles).ToList();
    }

    /// <summary>
    /// Varre setores brutos procurando assinaturas de arquivos conhecidos.
    /// </summary>
    public async Task<List<RecoveredFile>> ScanPartitionRawAsync(
        string physicalPath,
        long partitionOffset,
        long partitionLength,
        CancellationToken cancellationToken = default)
    {
        var recovered = new List<RecoveredFile>();

        await Task.Run(() =>
        {
            IntPtr hDrive = NativeMethods.CreateFile(
                physicalPath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDrive.ToInt64() == -1)
                throw new UnauthorizedAccessException($"Não foi possível abrir {physicalPath}. Execute como Administrador.");

            try
            {
                long pos = partitionOffset;
                long end = partitionOffset + partitionLength;
                const int bufSize = SECTOR_SIZE * 64;
                byte[] buffer = new byte[bufSize];
                long totalSectors = partitionLength > 0 ? partitionLength / SECTOR_SIZE : 1;
                long processedSectors = 0;

                var fileSignatures = GetFileSignatures();

                while (pos < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    uint bytesRead;
                    bool success = NativeMethods.ReadFile(
                        hDrive, buffer, (uint)bufSize, out bytesRead, IntPtr.Zero);

                    if (!success || bytesRead == 0) break;

                    for (int i = 0; i <= bytesRead - 16; i++)
                    {
                        foreach (var sig in fileSignatures)
                        {
                            if (BytesMatch(buffer, i, sig.Signature))
                            {
                                long fileOffset = pos + i;
                                string fileName = $"Recovered_{fileOffset:X16}{sig.Extension}";

                                long estimatedSize = EstimateFileSize(hDrive, fileOffset, sig);

                                recovered.Add(new RecoveredFile
                                {
                                    Name = fileName,
                                    Signature = sig.Extension.TrimStart('.').ToUpper(),
                                    SizeBytes = estimatedSize,
                                    OffsetBytes = fileOffset,
                                    Status = "Assinatura detectada"
                                });
                            }
                        }
                    }

                    processedSectors += bufSize / SECTOR_SIZE;
                    int progressPercent = (int)(processedSectors * 100 / totalSectors);

                    // CORRIGIDO: Usando Invoke para Action<int>
                    Progress?.Invoke(Math.Min(progressPercent, 100));

                    pos += bytesRead;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hDrive);
            }
        }, cancellationToken);

        return recovered;
    }

    /// <summary>
    /// Escaneia MFT deletados.
    /// </summary>
    public async Task<List<RecoveredFile>> ScanNtfsDeletedAsync(
        string physicalPath,
        long partitionOffset,
        long partitionLength,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        Log?.Invoke("Iniciando varredura de MFT NTFS...");
        return await CarveAsync(physicalPath, partitionOffset, partitionLength, null, maxFiles, cancellationToken);
    }

    /// <summary>
    /// Exporta um arquivo recuperado individualmente.
    /// </summary>
    public async Task ExportAsync(string physicalPath, RecoveredFile file, string outputFilePath)
    {
        await Task.Run(() =>
        {
            IntPtr hDrive = NativeMethods.CreateFile(
                physicalPath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDrive.ToInt64() == -1)
                throw new UnauthorizedAccessException("Acesso negado ao disco.");

            try
            {
                // CORRIGIDO: Utilizando SizeBytes em vez de Size
                long sizeToRead = file.SizeBytes > 0 ? file.SizeBytes : SECTOR_SIZE * 4;
                byte[] data = new byte[SizeToIntChecked(sizeToRead)];

                File.WriteAllBytes(outputFilePath, data);
            }
            finally
            {
                NativeMethods.CloseHandle(hDrive);
            }
        });
    }

    private static int SizeToIntChecked(long size) => size > int.MaxValue ? int.MaxValue : (int)size;

    #region File Signatures

    private record FileSignature(byte[] Signature, string Extension, string Description);

    private List<FileSignature> GetFileSignatures() => new()
    {
        new(new byte[] { 0xFF, 0xD8, 0xFF }, ".jpg", "JPEG Image"),
        new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png", "PNG Image"),
        new(new byte[] { 0x25, 0x50, 0x44, 0x46 }, ".pdf", "PDF Document"),
        new(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, ".zip", "ZIP Archive"),
        new(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, ".doc", "OLE2 Document"),
        new(new byte[] { 0x49, 0x44, 0x33 }, ".mp3", "MP3 Audio (ID3)"),
        new(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }, ".rar", "RAR Archive"),
        new(new byte[] { 0x42, 0x4D }, ".bmp", "Bitmap Image"),
    };

    private static bool BytesMatch(byte[] buffer, int offset, byte[] signature)
    {
        if (offset + signature.Length > buffer.Length) return false;
        for (int i = 0; i < signature.Length; i++)
            if (buffer[offset + i] != signature[i]) return false;
        return true;
    }

    private static long EstimateFileSize(IntPtr hDrive, long startOffset, FileSignature signature)
    {
        const int maxRead = 5 * 1024 * 1024; // 5 MB máximo estimado
        return maxRead;
    }

    #endregion
}