using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flow.Launcher;

internal static class GameLaunch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private const string Vanilla = "1.20.1";
    private const string Forge = "1.20.1-forge-47.4.0";

    public static async Task<int> RunAsync(string root, MinecraftSession session, int ramGiB, IProgress<string> progress, Action gameStarted, CancellationToken forceStop, CancellationToken ct)
    {
        var instances = Path.Combine(root, "instances");
        var instance = Path.Combine(instances, "sunlit-valley");
        if (!Directory.Exists(Path.Combine(instance, "mods")))
            throw new IOException("서버팩을 먼저 설치하세요.");

        using var packLock = File.Open(Path.Combine(instances, ".sunlit-valley.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        progress.Report("Java 17을 확인하는 중…");
        var java = await JavaRuntime.PrepareAsync(root);
        var game = await ForgeRuntime.PrepareAsync(root, progress, ct);
        progress.Report("Minecraft와 Forge 라이브러리를 준비하는 중…");
        using var forge = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(game, "versions", Forge, Forge + ".json"), ct));
        using var catalogue = JsonDocument.Parse(await Http.GetByteArrayAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct));
        var entry = catalogue.RootElement.GetProperty("versions").EnumerateArray()
            .First(item => Str(item, "id") == Vanilla);
        var versionFile = Path.Combine(game, "versions", Vanilla, Vanilla + ".json");
        await FetchAsync(Str(entry, "url"), Str(entry, "sha1"), versionFile, ct);
        using var vanilla = JsonDocument.Parse(await File.ReadAllBytesAsync(versionFile, ct));
        var client = Path.Combine(game, "versions", Vanilla, Vanilla + ".jar");
        await FetchAsync(vanilla.RootElement.GetProperty("downloads").GetProperty("client"), client, ct);

        var libraries = Path.Combine(game, "libraries");
        var natives = Path.Combine(game, "flow-natives");
        Directory.CreateDirectory(natives);
        var classpath = new Dictionary<string, string>();
        foreach (var profile in new[] { vanilla.RootElement, forge.RootElement })
        {
            foreach (var lib in profile.GetProperty("libraries").EnumerateArray().Where(Allowed))
            {
                var name = Str(lib, "name").Split(':');
                if (!lib.TryGetProperty("downloads", out var downloads))
                    continue;
                if (downloads.TryGetProperty("artifact", out var artifact))
                {
                    var path = Path.Combine(libraries, Str(artifact, "path"));
                    if (Str(artifact, "url").Length > 0)
                        await FetchAsync(artifact, path, ct);
                    else if (!File.Exists(path))
                        throw new IOException($"Forge 라이브러리가 없습니다 ({name[1]}).");

                    classpath[string.Join(':', name.Take(2).Concat(name.Skip(3)))] = path;
                }
                if (lib.TryGetProperty("natives", out var platforms) &&
                    platforms.TryGetProperty("osx", out var native))
                {
                    var classifier = native.GetString()!.Replace("${arch}", "64");
                    var spec = downloads.GetProperty("classifiers").GetProperty(classifier);
                    var path = Path.Combine(libraries, Str(spec, "path"));
                    await FetchAsync(spec, path, ct);
                    System.IO.Compression.ZipFile.ExtractToDirectory(path, natives, overwriteFiles: true);
                }
            }
        }

        progress.Report("Minecraft 게임 리소스를 준비하는 중…");
        var assets = Path.Combine(game, "assets");
        var index = vanilla.RootElement.GetProperty("assetIndex");
        var indexName = Str(index, "id");
        var indexPath = Path.Combine(assets, "indexes", indexName + ".json");
        await FetchAsync(index, indexPath, ct);
        using var indexJson = JsonDocument.Parse(await File.ReadAllBytesAsync(indexPath, ct));
        var hashes = indexJson.RootElement.GetProperty("objects").EnumerateObject()
            .Select(item => Str(item.Value, "hash")).Distinct();
        await Parallel.ForEachAsync(hashes, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = 12
        }, async (hash, token) =>
        {
            var relative = $"{hash[..2]}/{hash}";
            await FetchAsync($"https://resources.download.minecraft.net/{relative}", hash,
                Path.Combine(assets, "objects", relative), token);
        });

        var variables = new Dictionary<string, string>
        {
            ["auth_player_name"] = session.Name, ["auth_uuid"] = session.Id,
            ["auth_access_token"] = session.AccessToken, ["auth_xuid"] = "",
            ["clientid"] = LoginSetup.GetClientId(), ["user_type"] = "msa",
            ["version_name"] = Forge, ["version_type"] = "release",
            ["game_directory"] = instance, ["assets_root"] = assets, ["assets_index_name"] = indexName,
            ["natives_directory"] = natives, ["launcher_name"] = "FLOW", ["launcher_version"] = "0.1.0",
            ["classpath"] = string.Join(Path.PathSeparator, classpath.Values),
            ["library_directory"] = libraries, ["classpath_separator"] = Path.PathSeparator.ToString()
        };
        var start = new ProcessStartInfo(java)
        {
            WorkingDirectory = instance, UseShellExecute = false, RedirectStandardError = true
        };
        start.ArgumentList.Add($"-Xmx{ramGiB}G");
        foreach (var kind in new[] { "jvm", "game" })
        {
            if (kind == "game")
                start.ArgumentList.Add(Str(forge.RootElement, "mainClass"));
            foreach (var profile in new[] { vanilla.RootElement, forge.RootElement })
            foreach (var arg in profile.GetProperty("arguments").GetProperty(kind).EnumerateArray())
            {
                if (arg.ValueKind == JsonValueKind.String)
                    Add(arg.GetString()!);
                else if (Allowed(arg))
                {
                    var value = arg.GetProperty("value");
                    if (value.ValueKind == JsonValueKind.String)
                        Add(value.GetString()!);
                    else
                        foreach (var item in value.EnumerateArray())
                            Add(item.GetString()!);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("게임을 시작할 수 없습니다.");
        progress.Report("Minecraft가 실행 중입니다.");
        var stderr = process.StandardError.ReadToEndAsync();
        gameStarted();
        try
        {
            await process.WaitForExitAsync(forceStop);
        }
        catch (OperationCanceledException) when (forceStop.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync();
            throw;
        }
        var errors = await stderr;
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(errors))
        {
            try
            {
                var logs = Directory.CreateDirectory(Path.Combine(instance, "logs"));
                await File.WriteAllTextAsync(Path.Combine(logs.FullName, "flow-launch-error.log"),
                    errors.Replace(session.AccessToken, "[redacted]", StringComparison.Ordinal));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return process.ExitCode;

        void Add(string argument) => start.ArgumentList.Add(Regex.Replace(argument, @"\$\{([^}]+)\}",
            match => variables.TryGetValue(match.Groups[1].Value, out var value) ? value :
                throw new InvalidDataException($"지원하지 않는 게임 인자 ({match.Groups[1].Value})")));
    }

    private static bool Allowed(JsonElement item)
    {
        if (!item.TryGetProperty("rules", out var rules))
            return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("features", out var features) &&
                features.EnumerateObject().Any(feature => feature.Value.GetBoolean()))
                continue;
            if (rule.TryGetProperty("os", out var os) &&
                ((Str(os, "name") is var name && name.Length > 0 && name != "osx") ||
                (Str(os, "arch") is var arch && arch.Length > 0 && !(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? arch is "arm64" or "aarch64" : arch is "x86_64" or "amd64")) ||
                (Str(os, "version") is var version && version.Length > 0 &&
                    !Regex.IsMatch(Environment.OSVersion.Version.ToString(), version))))
                continue;
            allowed = Str(rule, "action") == "allow";
        }
        return allowed;
    }

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static Task FetchAsync(JsonElement spec, string path, CancellationToken ct) =>
        FetchAsync(Str(spec, "url"), Str(spec, "sha1"), path, ct);

    private static async Task FetchAsync(string url, string sha, string path, CancellationToken ct)
    {
        var expected = Convert.FromHexString(sha);
        if (File.Exists(path))
        {
            await using var local = File.OpenRead(path);
            if ((await SHA1.HashDataAsync(local, ct)).AsSpan().SequenceEqual(expected))
                return;
        }
        var bytes = await Http.GetByteArrayAsync(url, ct);
        if (!SHA1.HashData(bytes).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"게임 파일 검증 실패 ({Path.GetFileName(path)})");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}






