using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Reports;

public sealed record MiaoVisionInstallationStatus(bool IsInstalled, string Version, string ExecutablePath, string Message);

public sealed class MiaoVisionInstallerService
{
    public const string SupportedVersion = "0.6.2";
    public const long DownloadSizeBytes = 91_258_880;
    private const string Sha256 = "455341c462fab0e548628bfb224004a2d8faf9353e447b90c5ba871ec44bc6d6";
    private const string DownloadUrl = "https://github.com/miaoshou-dev/miao-vision/releases/download/skill-v0.6.2/miao-viz-windows-x64.exe";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true })
        { Timeout = TimeSpan.FromMinutes(10) };

    public static string InstallDirectory => Path.Combine(ApplicationData.Current.LocalFolder.Path, "ReportEngine", "MiaoVision", SupportedVersion);
    public static string ExecutablePath => Path.Combine(InstallDirectory, "miao-viz.exe");

    public async Task<MiaoVisionInstallationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ExecutablePath))
            return new(false, SupportedVersion, ExecutablePath, "Miao Vision no está instalado.");
        try
        {
            if (!await HasExpectedHashAsync(ExecutablePath, cancellationToken))
                return new(false, SupportedVersion, ExecutablePath, "La instalación existe, pero no coincide con la versión segura esperada.");
            var version = await ReadVersionAsync(cancellationToken);
            return new(true, SupportedVersion, ExecutablePath,
                string.IsNullOrWhiteSpace(version) ? $"Miao Vision {SupportedVersion} instalado y verificado." : $"Miao Vision instalado y verificado · {version}");
        }
        catch (Exception ex)
        {
            return new(false, SupportedVersion, ExecutablePath, "Miao Vision no pudo validarse: " + ex.Message);
        }
    }

    public async Task<MiaoVisionInstallationStatus> InstallAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(InstallDirectory);
        var temporaryPath = ExecutablePath + ".download";
        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            using var response = await Client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value != DownloadSizeBytes)
                throw new InvalidOperationException("El tamaño publicado del motor cambió; se canceló la instalación por seguridad.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > DownloadSizeBytes) throw new InvalidOperationException("La descarga excedió el tamaño esperado.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(Math.Clamp(total * 100d / DownloadSizeBytes, 0, 100));
                }
                if (total != DownloadSizeBytes) throw new InvalidOperationException("La descarga quedó incompleta.");
            }
            if (!await HasExpectedHashAsync(temporaryPath, cancellationToken))
                throw new InvalidOperationException("La firma SHA-256 no coincide. El archivo fue descartado.");
            File.Move(temporaryPath, ExecutablePath, true);
            var status = await GetStatusAsync(cancellationToken);
            if (!status.IsInstalled) throw new InvalidOperationException(status.Message);
            progress?.Report(100);
            return status;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(bytes), Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadVersionAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ExecutablePath, "--version")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = InstallDirectory
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows no pudo iniciar el motor.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var text = (await output).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
        return text;
    }
}
