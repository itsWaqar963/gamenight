// Agent self-update (Phase 5): poll GET /api/v1/agent/latest, download the
// GitHub Release asset, verify SHA-256, then two-process binary-swap.
//
// Why two processes: Windows locks the running exe. We spawn the *new* binary
// with --apply-update, exit (releasing the lock + single-instance mutex), and
// the child waits for our PID, replaces us in place, and relaunches.
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
    NotConfigured, // server returned null metadata
    Failed,
}

public sealed record UpdateResult(UpdateOutcome Outcome, string Message, string? NewVersion = null);

public static class Updater
{
    public const string ApplyUpdateFlag = "--apply-update";

    private static readonly string UpdateDir =
        Path.Combine(AgentConfig.DataDir, "update");

    private static readonly HttpClient Http = CreateHttp();

    // Manual "Check for updates" can overlap the background timer; serialize.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // After a swap is scheduled we are exiting — ignore further checks so a
    // racing poll cannot toast a misleading SSL/network failure over success.
    private static int _swapScheduled;

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub (and some CDNs) are happier with an explicit UA on large downloads.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameNightAgent/{AgentInfo.Version}");
        return http;
    }

    /// <summary>
    /// Child-process entry: wait for <paramref name="parentPid"/> to exit,
    /// move <paramref name="pendingPath"/> over <paramref name="targetPath"/>,
    /// relaunch the agent. Must run BEFORE the single-instance mutex.
    /// </summary>
    public static int ApplyUpdate(string pendingPath, string targetPath, int parentPid)
    {
        try
        {
            WaitForProcessExit(parentPid, TimeSpan.FromSeconds(60));
            // Extra beat for Windows to release the file lock after process exit.
            Thread.Sleep(500);

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

    /// <summary>
    /// Check the server for a newer agent, download + verify, and schedule the
    /// binary swap. On <see cref="UpdateOutcome.Updated"/> the caller must exit
    /// the process so the child can replace the exe.
    /// <paramref name="onProgress"/> receives UI stage text (Checking / Downloading / Installing).
    /// </summary>
    public static async Task<UpdateResult> CheckAndApplyAsync(
        string serverUrl, bool silentIfCurrent = true, Action<string>? onProgress = null)
    {
        if (Interlocked.CompareExchange(ref _swapScheduled, 0, 0) == 1)
            return new(UpdateOutcome.Updated, "Update already in progress…");

        if (!await Gate.WaitAsync(0))
            return new(UpdateOutcome.UpToDate, "An update check is already in progress.");

        try
        {
            UpdateResult result = await CheckAndApplyCoreAsync(serverUrl, silentIfCurrent, onProgress);
            if (result.Outcome == UpdateOutcome.Updated)
            {
                Interlocked.Exchange(ref _swapScheduled, 1);
                // Keep the gate held until process exit so nothing else can start.
                return result;
            }
            return result;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _swapScheduled, 0, 0) == 0)
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
            Progress("Checking…");
            LatestRelease? latest = await FetchLatestAsync(serverUrl);
            if (latest is null ||
                string.IsNullOrWhiteSpace(latest.Version) ||
                string.IsNullOrWhiteSpace(latest.Url) ||
                string.IsNullOrWhiteSpace(latest.Sha256))
            {
                return new(UpdateOutcome.NotConfigured,
                    "Update metadata not configured on the server yet.");
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
            Progress($"Downloading v{verLabel}…");
            Log($"downloading v{latest.Version} from {latest.Url}");
            await DownloadAsync(latest.Url, pending, pct =>
                Progress(pct is null
                    ? $"Downloading v{verLabel}…"
                    : $"Downloading v{verLabel}… {pct}%"));

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
            // Unique swap name so a leftover locked GameNightAgent.swap.exe from a
            // previous attempt cannot block File.Copy.
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

    public static async Task<LatestRelease?> FetchLatestAsync(string serverUrl)
    {
        var baseUri = serverUrl.TrimEnd('/') + "/";
        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(baseUri), "api/v1/agent/latest"));
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<LatestRelease>();
    }

    /// <summary>Compare dotted semver-ish strings. Returns &gt;0 if a &gt; b.</summary>
    public static int CompareSemVer(string a, string b)
    {
        static int[] Parts(string raw)
        {
            string v = raw.Trim();
            // Accept "0.6.1", "v0.6.1", or "agent-v0.6.1" (how some releases were tagged).
            if (v.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
                v = v[6..];
            v = v.TrimStart('v', 'V');
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

    /// <summary>Accept bare hex or GitHub-style "sha256:abc…".</summary>
    public static string NormalizeSha256(string sha)
    {
        string s = sha.Trim();
        const string prefix = "sha256:";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..].Trim();
        return s.ToLowerInvariant();
    }

    private static async Task DownloadAsync(string url, string destPath, Action<int?>? onPercent = null)
    {
        const int maxAttempts = 3;
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string tmp = destPath + $".partial-{Environment.ProcessId}-{Guid.NewGuid():N}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;

                // Dispose streams BEFORE rename — Windows will not Move a file that
                // still has an open write handle (caused the download-loop bug).
                {
                    await using var input = await resp.Content.ReadAsStreamAsync();
                    await using var output = new FileStream(
                        tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long copied = 0;
                    int lastPct = -1;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        copied += read;
                        if (total is > 0)
                        {
                            int pct = (int)(copied * 100 / total.Value);
                            if (pct != lastPct && (pct == 100 || pct - lastPct >= 5))
                            {
                                lastPct = pct;
                                try { onPercent?.Invoke(pct); } catch { }
                            }
                        }
                        else if (lastPct < 0)
                        {
                            lastPct = 0;
                            try { onPercent?.Invoke(null); } catch { }
                        }
                    }
                    await output.FlushAsync();
                }

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

    /// <summary>Move/replace with short retries for AV scanners; does not re-download.</summary>
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
        // Last resort: copy then delete source.
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
        // Balloon tips truncate; keep the toast readable.
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
            // Already gone — fine.
        }
    }

    private static bool ReplaceWithRetry(string source, string dest, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                // Prefer atomic replace when dest exists; fall back to move.
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
}
