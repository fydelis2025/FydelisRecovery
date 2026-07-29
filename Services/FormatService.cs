using FydelisRecovery.Models;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace FydelisRecovery.Services;

public class FormatService
{
    public event Action<string>? Log;

    /// <summary>
    /// Formata um volume lógico (ex: E:\) via WMI Win32_Volume.Format
    /// </summary>
    public async Task FormatVolumeAsync(
        PartitionInfo partition,
        string fileSystem = "NTFS",
        string label = "",
        bool quick = true,
        int clusterSize = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partition.DriveLetter))
            throw new InvalidOperationException("Partição sem letra de unidade. Atribua uma letra antes de formatar.");

        string drive = partition.DriveLetter.TrimEnd('\\', ':') + ":";

        Log?.Invoke($"Formatando {drive} como {fileSystem} (rápido={quick})...");

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_Volume WHERE DriveLetter = '{drive}'");

            var volumes = searcher.Get().Cast<ManagementObject>().ToList();
            if (volumes.Count == 0)
                throw new InvalidOperationException($"Volume {drive} não encontrado via WMI.");

            using var volume = volumes[0];

            // Format(FileSystem, QuickFormat, ClusterSize, Label, EnableCompression)
            var inParams = volume.GetMethodParameters("Format");
            inParams["FileSystem"] = fileSystem.ToUpperInvariant();
            inParams["QuickFormat"] = quick;
            inParams["Label"] = label ?? "";
            if (clusterSize > 0)
                inParams["ClusterSize"] = (uint)clusterSize;
            inParams["EnableCompression"] = false;

            var outParams = volume.InvokeMethod("Format", inParams, null);
            uint result = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1);

            if (result != 0)
                throw new InvalidOperationException($"Format falhou. Código WMI: {result} ({WmiFormatError(result)})");

        }, ct);

        Log?.Invoke($"Formatação de {drive} concluída.");
    }

    /// <summary>
    /// Formata usando format.com (fallback / discos stubborn)
    /// </summary>
    public async Task FormatWithFormatComAsync(
        string driveLetter,
        string fileSystem = "NTFS",
        string label = "",
        bool quick = true,
        CancellationToken ct = default)
    {
        string drive = driveLetter.TrimEnd('\\', ':');
        var args = new StringBuilder();
        args.Append($"{drive}: /FS:{fileSystem.ToUpperInvariant()} /Y");
        if (quick) args.Append(" /Q");
        if (!string.IsNullOrWhiteSpace(label))
            args.Append($" /V:{label}");

        Log?.Invoke($"Executando: format.com {args}");

        var psi = new ProcessStartInfo
        {
            FileName = "format.com",
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        var tcs = new TaskCompletionSource<int>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Log?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Log?.Invoke("[ERR] " + e.Data);
        };
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);
        proc.EnableRaisingEvents = true;

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // confirmação automática (alguns cenários pedem ENTER)
        try { await proc.StandardInput.WriteLineAsync(); } catch { /* ignore */ }

        using (ct.Register(() => { try { proc.Kill(true); } catch { } tcs.TrySetCanceled(ct); }))
        {
            int code = await tcs.Task;
            if (code != 0)
                throw new InvalidOperationException($"format.com saiu com código {code}");
        }

        Log?.Invoke("format.com concluído.");
    }

    /// <summary>
    /// Limpa a tabela de partições e cria uma partição primária única (diskpart).
    /// CUIDADO: destrutivo.
    /// </summary>
    public async Task CleanAndCreatePrimaryPartitionAsync(
        int diskIndex,
        string fileSystem = "NTFS",
        string label = "NOVO",
        string? assignLetter = null,
        CancellationToken ct = default)
    {
        string letterLine = string.IsNullOrEmpty(assignLetter)
            ? "assign"
            : $"assign letter={assignLetter.TrimEnd(':', '\\')}";

        string script = $@"
select disk {diskIndex}
clean
create partition primary
format fs={fileSystem.ToLowerInvariant()} label=""{label}"" quick
{letterLine}
exit
";
        await RunDiskpartAsync(script, ct);
    }

    public async Task RunDiskpartAsync(string script, CancellationToken ct = default)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dp_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, script, ct);

        try
        {
            Log?.Invoke("Executando diskpart...");
            Log?.Invoke(script);

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{tmp}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Não foi possível iniciar diskpart.");

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (!string.IsNullOrWhiteSpace(stdout)) Log?.Invoke(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Log?.Invoke("[ERR] " + stderr);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"diskpart falhou (código {proc.ExitCode})");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static string WmiFormatError(uint code) => code switch
    {
        1 => "Acesso negado / parâmetros inválidos",
        2 => "Volume em uso",
        3 => "Sistema de arquivos não suportado",
        4 => "Cluster size inválido",
        5 => "Volume read-only",
        _ => "Erro desconhecido"
    };
}