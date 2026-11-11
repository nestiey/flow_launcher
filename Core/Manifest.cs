namespace Flow;

public sealed record PackManifest(
    int SchemaVersion, string PackId, long Sequence, string PackVersion,
    string MinimumLauncherVersion, GameProfile Game, IReadOnlyList<PackFile> Files);

public sealed record GameProfile(string MinecraftVersion, string Loader,
    string LoaderVersion, int JavaMajor, string DataEpoch);

public sealed record PackFile(string Path, string Sha256, long Size, BlobLocation Blob);
public sealed record BlobLocation(string Tag, string Asset);
public sealed record UpdatePlan(PackFile[] Download, PackFile[] Reuse, string[] Removed);

public static class UpdatePlanner
{
    // Pass only validated manifests and a locally verified content inventory.
    public static UpdatePlan Compare(PackManifest? current, PackManifest target,
        IReadOnlySet<(string Sha256, long Size)> verifiedContent)
    {
        var targetPaths = target.Files.Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var download = new List<PackFile>();
        var reuse = new List<PackFile>();
        foreach (var file in target.Files)
        {
            var destination = verifiedContent.Contains((file.Sha256, file.Size))
                ? reuse : download;
            destination.Add(file);
        }
        var removed = current?.Files.Select(file => file.Path)
            .Where(path => !targetPaths.Contains(path)).ToArray() ?? [];
        return new UpdatePlan(download.ToArray(), reuse.ToArray(), removed);
    }
}
