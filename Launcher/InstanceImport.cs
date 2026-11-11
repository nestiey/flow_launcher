using System.IO;

namespace Flow.Launcher;

internal static class InstanceImport
{
    private static readonly HashSet<string> GeneratedFolders = new(
        [".bobby", ".mixin.out", "logs", "crash-reports", "controllable_natives"],
        StringComparer.OrdinalIgnoreCase);

    public static string Copy(string selectedFolder, string installRoot)
    {
        var source = Path.GetFullPath(selectedFolder);
        if (Directory.Exists(Path.Combine(source, "instance", "mods")))
            source = Path.Combine(source, "instance");

        if (!Directory.Exists(Path.Combine(source, "mods")))
            throw new IOException("mods 폴더가 있는 Sunlit Valley 인스턴스를 선택하세요.");

        var destination = Path.Combine(installRoot, "instances", "sunlit-valley");
        var parent = Path.GetDirectoryName(destination)!;
        if (Directory.Exists(destination))
            throw new IOException("이미 가져온 인스턴스가 있습니다. 기존 파일은 보존했습니다.");

        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".import-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(new DirectoryInfo(source), staging, root: true);
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void CopyDirectory(DirectoryInfo source, string destination, bool root)
    {
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (root && GeneratedFolders.Contains(entry.Name))
                continue;

            RejectLink(entry);
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo child)
                CopyDirectory(child, target, root: false);
            else
                File.Copy(entry.FullName, target);
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"링크가 포함된 경로를 복사할 수 없습니다. {entry.FullName}");
    }
}
