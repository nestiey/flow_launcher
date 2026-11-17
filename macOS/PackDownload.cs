using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flow.Launcher;

internal static class PackDownload
{
    private const string Repo = "nestiey/sunlit-valley-releases";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ManagedRoots = new(
        ["mods", "config", "defaultconfigs", "configureddefaults", "kubejs",
         "resourcepacks", "shaderpacks", "patchouli_books", "moonlight-global-datapacks",
         "quests_structures"], StringComparer.OrdinalIgnoreCase);

    public static async Task InstallAsync(string destination, IProgress<string> progress,
        CancellationToken cancellationToken)
    {

        const string trustedKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMTNnBNh1PMTXnrOBpRf3aiS/5+3xKth/urWrUStFabO7jVmHuh4yB4Zs3GSuZeVAsbGhukbc/D8deKhAzY0Iog==";
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trustedKey), out _);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd("FLOW-Launcher/0.1");
        using var release = await Http.SendAsync(request, cancellationToken);
        if (release.StatusCode == HttpStatusCode.NotFound)
            throw new IOException("GitHub에 공개된 Sunlit Valley 릴리스가 아직 없습니다.");
        release.EnsureSuccessStatusCode();
        var metadata = await release.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var tag = metadata.GetProperty("tag_name").GetString() ?? "";
        ValidateTag(tag);
        var bytes = await ReadAssetAsync(tag, "manifest.json", 16 * 1024 * 1024, cancellationToken);
        var signature = await ReadAssetAsync(tag, "manifest.sig", 1024, cancellationToken);
        if (!verifier.VerifyData(bytes, Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signature)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("모드팩 매니페스트 서명이 올바르지 않습니다.");

        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(bytes, Json);
        if (manifest is null || manifest.SchemaVersion != 2 || manifest.PackId != "sunlit-valley" ||
            manifest.Sequence <= 0 || tag != $"sunlit-valley-{manifest.Sequence}" ||
            manifest.Game is not { MinecraftVersion: "1.20.1", Loader: "forge", LoaderVersion: "47.4.0", JavaMajor: 17 } ||
            !Version.TryParse(manifest.MinimumLauncherVersion, out var minimum) ||
            minimum > new Version(0, 1, 0) ||
            manifest.Archive is not { Asset: "minecraft.zip", Size: > 0 and < 2147483648L,
                ExpandedSize: >= 0 and <= 8589934592L, FileCount: > 0 and <= 100000 } archive ||
            !Regex.IsMatch(archive.Sha256 ?? "", @"\A[0-9a-f]{64}\z"))
            throw new InvalidDataException("지원하지 않는 서버팩 배포 정보입니다.");

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        using var installLock = File.Open(Path.Combine(parent, ".sunlit-valley.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var backup = destination + ".flow-backup";
        if (Directory.Exists(backup) && !Directory.Exists(destination))
            Directory.Move(backup, destination);

        var marker = Path.Combine(destination, ".flow-build");
        if (File.Exists(marker) && long.TryParse(await File.ReadAllTextAsync(marker, cancellationToken), out var local))
        {
            if (local > manifest.Sequence)
                throw new InvalidDataException("GitHub 배포 번호가 설치된 버전보다 낮습니다.");
            if (local == manifest.Sequence)
                return;
        }

        var work = Directory.CreateDirectory(Path.Combine(parent, $".download-{Guid.NewGuid():N}"));
        try
        {
            var zip = Path.Combine(work.FullName, archive.Asset);
            var stage = Directory.CreateDirectory(Path.Combine(work.FullName, "instance"));
            progress.Report($"Sunlit Valley {manifest.Sequence} · minecraft.zip 다운로드 중…");
            var blob = new PackFile(archive.Asset, archive.Sha256, archive.Size, new BlobLocation(tag, archive.Asset));
            await DownloadFileAsync(blob, zip, cancellationToken);
            progress.Report("검증 완료 · 압축 해제 중…");
            await ExtractAsync(zip, stage.FullName, archive, cancellationToken);

            if (Directory.Exists(destination))
            {
                var existing = new DirectoryInfo(destination);
                if (existing.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("연결된 인스턴스는 업데이트할 수 없습니다.");
                foreach (var entry in existing.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ManagedRoots.Contains(entry.Name) && entry.Name != ".flow-build")
                        CopyLocalEntry(entry, stage.FullName);
                }
            }

            await File.WriteAllTextAsync(Path.Combine(stage.FullName, ".flow-build"),
                manifest.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
            if (Directory.Exists(destination))
                Directory.Move(destination, backup);
            try
            {
                Directory.Move(stage.FullName, destination);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(destination))
                    Directory.Move(backup, destination);
                throw;
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private sealed record ArchiveManifest(int SchemaVersion, string PackId, long Sequence,
        string MinimumLauncherVersion, GameProfile Game, ArchiveSpec Archive);

    private sealed record ArchiveSpec(string Asset, string Sha256, long Size, long ExpandedSize, int FileCount);

    private static async Task ExtractAsync(string zip, string stage, ArchiveSpec spec, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count != spec.FileCount)
            throw new InvalidDataException("압축 파일의 항목 수가 일치하지 않습니다.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            ValidatePath(entry.FullName);
            if (!paths.Add(entry.FullName) || entry.Length > spec.ExpandedSize - expanded)
                throw new InvalidDataException("중복 경로 또는 압축 해제 크기 오류입니다.");
            var target = Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = File.Open(target, FileMode.CreateNew);
            var buffer = new byte[65536];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                written += count;
                if (written > entry.Length)
                    throw new InvalidDataException("압축 항목 크기가 일치하지 않습니다.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (written != entry.Length)
                throw new InvalidDataException("압축 항목 크기가 일치하지 않습니다.");
            expanded += written;
        }
        if (expanded != spec.ExpandedSize || !Directory.Exists(Path.Combine(stage, "mods")))
            throw new InvalidDataException("압축 파일의 서버팩 구성이 올바르지 않습니다.");
    }

    private static void CopyLocalEntry(FileSystemInfo entry, string destination)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("mods 폴더에 링크가 포함되어 있습니다.");

        var target = Path.Combine(destination, entry.Name);
        if (entry is DirectoryInfo directory)
        {
            Directory.CreateDirectory(target);
            foreach (var child in directory.EnumerateFileSystemInfos())
                CopyLocalEntry(child, target);
        }
        else
        {
            File.Copy(entry.FullName, target);
        }
    }

    private static void ValidateTag(string tag)
    {
        if (!Regex.IsMatch(tag, @"\Asunlit-valley-[1-9][0-9]{0,18}\z"))
            throw new InvalidDataException("릴리스 태그 형식이 올바르지 않습니다.");
    }

    private static void ValidatePath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 2 || !ManagedRoots.Contains(parts[0]) ||
            parts.Any(part => part.Length == 0 || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("배포할 수 없는 파일 경로가 포함되어 있습니다.");

    }

    private static string AssetUrl(string tag, string name) =>
        $"https://github.com/{Repo}/releases/download/{tag}/{name}";

    private static async Task<byte[]> ReadAssetAsync(string tag, string name, int limit, CancellationToken ct)
    {
        using var response = await Http.GetAsync(AssetUrl(tag, name), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("매니페스트 파일이 너무 큽니다.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task DownloadFileAsync(PackFile file, string target, CancellationToken ct)
    {
        using var response = await Http.GetAsync(AssetUrl(file.Blob.Tag, file.Blob.Asset),
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Open(target, FileMode.CreateNew);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            length += count;
            if (length > file.Size)
                throw new InvalidDataException($"파일 크기 검증 실패 ({file.Path})");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (length != file.Size || !hash.GetHashAndReset().AsSpan().SequenceEqual(Convert.FromHexString(file.Sha256)))
            throw new InvalidDataException($"파일 무결성 검증 실패 ({file.Path})");
    }
}





