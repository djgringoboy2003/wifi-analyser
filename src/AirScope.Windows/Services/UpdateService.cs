using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed record StagedUpdate(string Version, string Executable, bool PublisherVerified, string Detail);
public sealed class UpdateService : IDisposable
{
    private const string Prefix = "https://github.com/djgringoboy2003/wifi-analyser/releases/download/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    public UpdateService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("AirScope/" + Product.Version);
    public async Task<ReleaseManifest?> CheckAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync("https://api.github.com/repos/djgringoboy2003/wifi-analyser/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string? url = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("name").GetString() == "AirScope-update.json").GetPropertyOrNull("browser_download_url");
        if (url is null || !url.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        using var manifestResponse = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); manifestResponse.EnsureSuccessStatusCode();
        if (manifestResponse.Content.Headers.ContentLength > 65536) throw new InvalidDataException("Release manifest exceeds the supported size.");
        await using var body = await manifestResponse.Content.ReadAsStreamAsync(token); using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true) { int n = await body.ReadAsync(buffer, token); if (n == 0) break; if (memory.Length + n > 65536) throw new InvalidDataException("Oversized release manifest."); await memory.WriteAsync(buffer.AsMemory(0, n), token); }
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(memory.ToArray(), JsonStore.Options) ?? throw new InvalidDataException("Invalid release manifest.");
        Validate(manifest);
        return Version.Parse(manifest.Version.TrimStart('v')) > Version.Parse(Product.Version) ? manifest : null;
    }
    public static void Validate(ReleaseManifest manifest)
    {
        if (!Version.TryParse(manifest.Version.TrimStart('v'), out _) || !manifest.Url.StartsWith(Prefix, StringComparison.Ordinal)
            || !Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsoluteUri.StartsWith(Prefix, StringComparison.Ordinal) || Uri.UnescapeDataString(uri.Segments.Last()) != manifest.FileName
            || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit) || Path.GetFileName(manifest.FileName) != manifest.FileName
            || !manifest.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release source, name, version or SHA-256 is invalid.");
    }
    public async Task<StagedUpdate> DownloadAsync(ReleaseManifest manifest, string? pinnedPublisher, IProgress<string> progress, CancellationToken token)
    {
        Validate(manifest);
        string directory = Path.Combine(JsonStore.Root, "updates", Guid.NewGuid().ToString("D")); Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "update.zip");
        using var response = await _http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 500_000_000) throw new IOException("The update exceeds the 500 MB limit.");
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[131072]; long total = 0;
            while (true)
            {
                int n = await input.ReadAsync(buffer, token); if (n == 0) break; total += n;
                if (total > 500_000_000) throw new IOException("The update exceeds the 500 MB limit.");
                await output.WriteAsync(buffer.AsMemory(0, n), token); progress.Report($"Downloading update: {total / 1_000_000d:F1} MB");
            }
        }
        await using (var stream = File.OpenRead(archive))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update checksum does not match its release manifest.");
        string executable = Path.Combine(directory, "AirScope.exe");
        using (var zip = ZipFile.OpenRead(archive))
        {
            var entries = zip.Entries.Where(e => e.FullName == "AirScope.exe").ToArray();
            if (entries.Length != 1 || entries[0].Length > 500_000_000) throw new InvalidDataException("The update does not contain one bounded AirScope executable.");
            entries[0].ExtractToFile(executable);
        }
        bool verified = false;
        if (!string.IsNullOrWhiteSpace(pinnedPublisher))
        {
            string json = await PowerShellRunner.RunAsync("$s=Get-AuthenticodeSignature -LiteralPath " + PowerShellRunner.Quote(executable)
                + "; [pscustomobject]@{Status=[string]$s.Status;Thumbprint=[string]$s.SignerCertificate.Thumbprint} | ConvertTo-Json -Compress", token: token);
            using var signature = JsonDocument.Parse(json);
            verified = signature.RootElement.GetProperty("Status").GetString() == "Valid"
                && string.Equals(signature.RootElement.GetProperty("Thumbprint").GetString(), pinnedPublisher, StringComparison.OrdinalIgnoreCase)
                && string.Equals(manifest.PublisherThumbprint, pinnedPublisher, StringComparison.OrdinalIgnoreCase);
        }
        return new(manifest.Version, executable, verified, verified
            ? "SHA-256 and the pinned trusted Authenticode publisher were verified. Ready to apply after closing the app."
            : "SHA-256 matched the GitHub release manifest. Trusted publisher verification is unavailable or failed; automatic application is blocked.");
    }
    public static async Task<string> PrepareApplyScriptAsync(StagedUpdate update, string pinnedPublisher)
    {
        if (!update.PublisherVerified || string.IsNullOrWhiteSpace(pinnedPublisher)) throw new InvalidOperationException("A verified pinned publisher is required to apply an update.");
        string target = Environment.ProcessPath ?? throw new IOException("Application path unavailable.");
        if (!Path.GetFileName(target).Equals("AirScope.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Start the app executable directly before updating.");
        string script = Path.Combine(Path.GetDirectoryName(update.Executable)!, "Apply.ps1");
        string text = $$"""
            $ErrorActionPreference='Stop'
            $source={{PowerShellRunner.Quote(update.Executable)}}
            $target={{PowerShellRunner.Quote(target)}}
            $pin={{PowerShellRunner.Quote(pinnedPublisher)}}
            Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            $signature=Get-AuthenticodeSignature -LiteralPath $source
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $pin) { throw 'Publisher verification failed.' }
            Copy-Item -LiteralPath $source -Destination ($target+'.new') -Force
            Move-Item -LiteralPath $target -Destination ($target+'.previous') -Force
            try { Move-Item -LiteralPath ($target+'.new') -Destination $target; Start-Process -FilePath $target -WindowStyle Hidden }
            catch { Move-Item -LiteralPath ($target+'.previous') -Destination $target -Force; throw }
            """;
        await File.WriteAllTextAsync(script, text); return script;
    }
    public void Dispose() => _http.Dispose();
}
internal static class JsonExtensions
{
    public static string? GetPropertyOrNull(this JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) ? found.GetString() : null;
}
