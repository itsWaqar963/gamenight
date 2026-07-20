// Agent self-update (Phase 5): poll GitHub Releases on itsWaqar963/gamenight
// (primary), fall back to GET /api/v1/agent/latest on the linked GameNight
// server, download the asset, verify SHA-256, then two-process binary-swap.
// Windows locks the running exe, so a child (--apply-update) waits for our
// PID, replaces the file, and relaunches.
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameNight.Agent;

public record LatestRelease(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256);

public enum UpdateOutcome
{
    UpToDate,
    Updated,       // swap scheduled; caller must exit
    NotConfigured, // no usable release metadata
    Failed,
}

public sealed record UpdateResult(UpdateOutcome Outcome, string Message, string? NewVersion = null);

public static class Updater
{
    public const string ApplyUpdateFlag = "--apply-update";

    /// <summary>Canonical upstream repo for agent builds (Waqar).</summary>
    public const string GitHubOwner = "itsWaqar963";
    public const string GitHubRepo = "gamenight";

    private static readonly string UpdateDir =
        Path.Combine(AgentConfig.DataDir, "update");

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _swapScheduled; // 1 after swap is scheduled; hold gate until exit

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameNightAgent/{AgentInfo.Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static int ApplyUpdate(string pendingPath, string targetPath, int parentPid)
    {
        try
        {
            WaitForProcessExit(parentPid, TimeSpan.FromSeconds(60));
            Thread.Sleep(500); // let Windows release the exe lock

            if (!ReplaceWithRetry(pendingPath, targetPath, attempts: 20, delayMs: 500))
            {
                Log($"replace failed: {pendingPath} → {targetPath}");
                return 1;
            }

            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            Log($"apply-update failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<UpdateResult> CheckAndApplyAsync(
        string serverUrl, bool silentIfCurrent = true, Action<string>? onProgress = null)
    {
        if (Volatile.Read(ref _swapScheduled) == 1)
            return new(UpdateOutcome.Updated, "Update already in progress…");

        if (!await Gate.WaitAsync(0))
            return new(UpdateOutcome.UpToDate, "An update check is already in progress.");

        try
        {
            UpdateResult result = await CheckAndApplyCoreAsync(serverUrl, silentIfCurrent, onProgress);
            if (result.Outcome == UpdateOutcome.Updated)
            {
                Volatile.Write(ref _swapScheduled, 1);
                return result; // keep gate held until process exit
            }
            return result;
        }
        finally
        {
            if (Volatile.Read(ref _swapScheduled) == 0)
                Gate.Release();
        }
    }

    private static async Task<UpdateResult> CheckAndApplyCoreAsync(
        string serverUrl, bool silentIfCurrent, Action<string>? onProgress)
    {
        void Progress(string msg)
        {
            try { onProgress?.Invoke(msg); } catch { /* UI must never kill update */ }
        }

        try
        {
            Progress("Checking GitHub…");
            LatestRelease? latest = await FetchLatestAsync(serverUrl);
            if (latest is null ||
                string.IsNullOrWhiteSpace(latest.Version) ||
                string.IsNullOrWhiteSpace(latest.Url) ||
                string.IsNullOrWhiteSpace(latest.Sha256))
            {
                return new(UpdateOutcome.NotConfigured,
                    "Could not read the latest agent release from GitHub yet.");
            }

            if (CompareSemVer(latest.Version, AgentInfo.Version) <= 0)
            {
                return new(UpdateOutcome.UpToDate,
                    silentIfCurrent
                        ? $"Running v{AgentInfo.Version}"
                        : $"You're on the latest version (v{AgentInfo.Version}).");
            }

            string pending = Path.Combine(UpdateDir, "GameNightAgent.pending.exe");
            Directory.CreateDirectory(UpdateDir);

            string verLabel = FormatVersionLabel(latest.Version);
            Log($"downloading v{latest.Version} from {latest.Url}");
            await DownloadAsync(latest.Url, pending, Progress, verLabel);

            Progress("Verifying…");
            string actual = ComputeSha256Hex(pending);
            string expected = NormalizeSha256(latest.Sha256);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(pending);
                return new(UpdateOutcome.Failed,
                    $"SHA-256 mismatch (got {actual[..Math.Min(12, actual.Length)]}…, expected {expected[..Math.Min(12, expected.Length)]}…). Update aborted.");
            }

            string? target = Environment.ProcessPath;
            if (string.IsNullOrEmpty(target))
                return new(UpdateOutcome.Failed, "Cannot locate running executable path.");

            Progress($"Installing v{verLabel}…");
            string swap = Path.Combine(UpdateDir, $"GameNightAgent.swap-{Guid.NewGuid():N}.exe");
            File.Copy(pending, swap, overwrite: true);

            int pid = Environment.ProcessId;
            var psi = new ProcessStartInfo(swap)
            {
                ArgumentList = { ApplyUpdateFlag, pending, target, pid.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            Log($"swap scheduled → v{latest.Version}");
            return new(UpdateOutcome.Updated, $"Installing v{verLabel} — restarting…", latest.Version);
        }
        catch (Exception ex)
        {
            string detail = FormatException(ex);
            Log($"check/apply failed: {detail}");
            return new(UpdateOutcome.Failed, $"Update failed: {detail}");
        }
    }

    private static string FormatVersionLabel(string version)
    {
        string v = version.Trim();
        if (v.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
            v = v[6..];
        return v.TrimStart('v', 'V');
    }

    /// <summary>
    /// Prefer GitHub Releases on itsWaqar963/gamenight; fall back to the linked server.
    /// </summary>
    public static async Task<LatestRelease?> FetchLatestAsync(string? serverUrl)
    {
        try
        {
            LatestRelease? fromGh = await FetchLatestFromGitHubAsync();
            if (fromGh is not null &&
                !string.IsNullOrWhiteSpace(fromGh.Version) &&
                !string.IsNullOrWhiteSpace(fromGh.Url))
            {
                return fromGh;
            }
        }
        catch (Exception ex)
        {
            Log($"github latest failed: {FormatException(ex)}");
        }

        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            try
            {
                return await FetchLatestFromServerAsync(serverUrl);
            }
            catch (Exception ex)
            {
                Log($"server latest failed: {FormatException(ex)}");
            }
        }

        return null;
    }

    private static async Task<LatestRelease?> FetchLatestFromGitHubAsync()
    {
        string url =
            $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page=30";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var releases = await JsonSerializer.DeserializeAsync<List<GhRelease>>(stream, JsonOpts);
        if (releases is null || releases.Count == 0) return null;

        return PickBestAgentRelease(releases);
    }

    private static async Task<LatestRelease?> FetchLatestFromServerAsync(string serverUrl)
    {
        var baseUri = serverUrl.TrimEnd('/') + "/";
        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(baseUri), "api/v1/agent/latest"));
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<LatestRelease>(JsonOpts);
    }

    internal static LatestRelease? PickBestAgentRelease(IReadOnlyList<GhRelease> releases)
    {
        var withExe = new List<(string Tag, string Version, string Url, string? Sha, bool IsAgentTag)>();
        foreach (GhRelease r in releases)
        {
            if (r.Draft || r.Prerelease || string.IsNullOrWhiteSpace(r.TagName) || r.Assets is null)
                continue;
            GhAsset? exe = PickExeAsset(r.Assets);
            if (exe is null || string.IsNullOrWhiteSpace(exe.BrowserDownloadUrl))
                continue;

            string version = FormatVersionLabel(r.TagName);
            string? sha = ExtractSha(exe, r.Body);
            bool isAgent = r.TagName.StartsWith("agent-v", StringComparison.OrdinalIgnoreCase)
                || r.TagName.StartsWith("agent-", StringComparison.OrdinalIgnoreCase);
            withExe.Add((r.TagName, version, exe.BrowserDownloadUrl, sha, isAgent));
        }

        if (withExe.Count == 0) return null;

        IEnumerable<(string Tag, string Version, string Url, string? Sha, bool IsAgentTag)> pool =
            withExe.Exists(x => x.IsAgentTag) ? withExe.Where(x => x.IsAgentTag) : withExe;

        var best = pool
            .OrderByDescending(x => x.Version, Comparer<string>.Create(CompareSemVer))
            .First();

        return new LatestRelease(best.Version, best.Url, best.Sha);
    }

    private static GhAsset? PickExeAsset(IReadOnlyList<GhAsset> assets)
    {
        foreach (GhAsset a in assets)
        {
            if (a.Name is not null &&
                a.Name.StartsWith("GameNightAgent", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        foreach (GhAsset a in assets)
        {
            if (a.Name is not null &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return null;
    }

    private static string? ExtractSha(GhAsset asset, string? body)
    {
        if (!string.IsNullOrWhiteSpace(asset.Digest))
            return NormalizeSha256(asset.Digest);
        if (string.IsNullOrEmpty(body)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            body, @"AGENT_SHA256=([a-fA-F0-9]{64})");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static int CompareSemVer(string a, string b)
    {
        static int[] Parts(string raw)
        {
            string v = FormatVersionLabel(raw);
            return v.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0)
                .ToArray();
        }

        int[] pa = Parts(a), pb = Parts(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static string NormalizeSha256(string sha)
    {
        string s = sha.Trim();
        const string prefix = "sha256:";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..].Trim();
        return s.ToLowerInvariant();
    }

    private static async Task DownloadAsync(
        string url, string destPath, Action<string> onProgress, string verLabel)
    {
        const int maxAttempts = 3;
        string tmp = destPath + ".partial";
        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                onProgress(attempt > 1
                    ? $"Retrying download ({attempt}/{maxAttempts})…"
                    : $"Downloading v{verLabel}…");

                TryDelete(tmp);
                TryDelete(destPath);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;

                {
                    await using var input = await resp.Content.ReadAsStreamAsync();
                    await using var output = new FileStream(
                        tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[256 * 1024];
                    long copied = 0;
                    int lastPct = -1;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        copied += read;
                        if (total is not > 0) continue;
                        int pct = (int)Math.Min(100, copied * 100 / total.Value);
                        if (pct != lastPct && (pct == 100 || pct - lastPct >= 2 || lastPct < 0))
                        {
                            lastPct = pct;
                            onProgress($"Downloading v{verLabel}… {pct}%");
                        }
                    }
                    await output.FlushAsync();
                }

                onProgress($"Downloading v{verLabel}… 100%");
                await ReplaceFileWithRetryAsync(tmp, destPath);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientNetwork(ex))
            {
                last = ex;
                Log($"download attempt {attempt}/{maxAttempts} failed (will retry): {FormatException(ex)}");
                TryDelete(tmp);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
        throw last ?? new InvalidOperationException("download failed");
    }

    private static async Task ReplaceFileWithRetryAsync(string source, string dest)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                TryDelete(dest);
                File.Move(source, dest);
                return;
            }
            catch (IOException) when (i < 9)
            {
                await Task.Delay(200 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 9)
            {
                await Task.Delay(200 * (i + 1));
            }
        }
        File.Copy(source, dest, overwrite: true);
        TryDelete(source);
    }

    private static bool IsTransientNetwork(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException or System.Net.Sockets.SocketException)
                return true;
            if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FormatException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            parts.Add(e.Message);
        string joined = string.Join(" → ", parts);
        return joined.Length <= 180 ? joined : joined[..177] + "…";
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.WaitForExit(timeout))
                throw new TimeoutException($"parent PID {pid} did not exit in time");
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static bool ReplaceWithRetry(string source, string dest, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(dest))
                    File.Replace(source, dest, destinationBackupFileName: null);
                else
                    File.Move(source, dest);
                return true;
            }
            catch (IOException) when (i + 1 < attempts)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i + 1 < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static void Log(string msg)
    {
        AgentLog.Write("update.log", msg);
        Debug.WriteLine($"[Updater] {msg}");
    }

    internal sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
    }

    internal sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
