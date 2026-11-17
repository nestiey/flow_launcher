using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Flow.Launcher;

internal static class ForgeRuntime
{
    private const string Version = "1.20.1-47.4.0";
    private const string InstallerUrl =
        "https://github.com/nestiey/flow_launcher/releases/download/forge-" +
        Version + "/forge-" + Version + "-installer.jar";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<string> PrepareAsync(string installRoot, IProgress<string> progress, CancellationToken ct)
    {
        var java = await JavaRuntime.PrepareAsync(installRoot);
        var game = Path.Combine(installRoot, "minecraft", Version);
        Directory.CreateDirectory(game);
        using var installLock = File.Open(Path.Combine(game, ".install.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var profile = Path.Combine(game, "versions", "1.20.1-forge-47.4.0",
            "1.20.1-forge-47.4.0.json");
        var marker = Path.Combine(game, ".forge-ready");
        if (File.Exists(marker) && File.Exists(profile))
            return game;

        var launcherProfiles = Path.Combine(game, "launcher_profiles.json");
        if (!File.Exists(launcherProfiles))
            await File.WriteAllTextAsync(launcherProfiles, """{"profiles":{}}""");

        var expected = Convert.FromHexString("b73203963ea70aee0c38dc58de1f6d16a766605b");
        var installer = Path.Combine(game, "forge-installer.jar");
        var bundled = Path.Combine(AppContext.BaseDirectory, "forge-1.20.1-47.4.0-installer.jar");
        if (!IsVerified(installer) && IsVerified(bundled))
        {
            progress.Report("동봉된 Forge 설치 파일을 확인했습니다.");
            File.Copy(bundled, installer, overwrite: true);
        }
        if (!IsVerified(installer))
        {
            var partial = installer + ".partial";
            try
            {
                using var response = await Http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                await using var input = await response.Content.ReadAsStreamAsync(idle.Token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = new byte[65536];
                long received = 0;
                var progressClock = Stopwatch.StartNew();
                await using (var output = File.Create(partial))
                {
                    while (true)
                    {
                        int count;
                        try { count = await input.ReadAsync(buffer, idle.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException("Forge 다운로드가 60초 동안 진행되지 않았습니다. 다시 시도하세요.");
                        }
                        if (count == 0)
                            break;
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        received += count;
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        if (progressClock.ElapsedMilliseconds >= 500)
                        {
                            var downloaded = $"{received / 1048576d:F1} MB";
                            var size = total is > 0 ? $" / {total.Value / 1048576d:F1} MB" : "";
                            progress.Report($"Forge 다운로드 중… {downloaded}{size}");
                            progressClock.Restart();
                        }
                    }
                }
                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("Forge 설치 파일의 체크섬이 일치하지 않습니다.");
                File.Move(partial, installer, overwrite: true);
            }
            finally { File.Delete(partial); }
        }

        var start = new ProcessStartInfo(java)
        {
            WorkingDirectory = game,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-jar", installer, "--installClient", game })
            start.ArgumentList.Add(argument);

        progress.Report("Forge 설치 중… 첫 실행에는 시간이 걸릴 수 있습니다.");
        using var process = Process.Start(start) ?? throw new IOException("Forge 설치를 시작할 수 없습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(12));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            throw new IOException("Forge 설치 시간이 초과됐습니다. 연결 상태를 확인한 뒤 다시 시도하세요.");
        }
        var log = Path.Combine(game, "forge-install.log");
        await File.WriteAllTextAsync(log, (await stdout) + Environment.NewLine + (await stderr));
        if (process.ExitCode != 0 || !File.Exists(profile))
            throw new IOException($"Forge 설치에 실패했습니다. 설치 로그: {log}");

        await File.WriteAllTextAsync(marker, Version);
        return game;

        bool IsVerified(string path) => File.Exists(path) &&
            SHA1.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(expected);
    }
}




