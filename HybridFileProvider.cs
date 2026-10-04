using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using EpicManifestParser.UE;

namespace ShopStreamExtractor;

public sealed class HybridFileProvider(VersionContainer version, string cacheDirectory)
    : AbstractVfsFileProvider(version, StringComparer.OrdinalIgnoreCase)
{
    public Task<int> RegisterManifestAsync(FBuildPatchAppManifest manifest)
    {
        // FilesById in current CUE4Parse is overwritten in registration order.
        // Register base containers first and patch (_P) containers last so hard package imports
        // resolve to the same newest container selected by path/read-order lookup.
        var containers = manifest.Files
            // Match FModel's Fortnite Live filter. Other products (notably
            // Chromium/CEF) also use the .pak extension but are not UE archives.
            .Where(file => file.FileName.Replace('\\', '/').StartsWith(
                "FortniteGame/Content/Paks/", StringComparison.OrdinalIgnoreCase))
            .Where(file => Path.GetExtension(file.FileName).Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(file.FileName).Equals(".utoc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => IsPatchContainer(file.FileName) ? 1 : 0)
            .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in containers)
        {
            var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
            if (extension is "pak" or "utoc")
            {
                RegisterVfs(file.FileName, [file.GetStream()], name =>
                    new FRandomAccessStreamArchive(name,
                        manifest.Files.First(x => x.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)).GetStream(), Versions));
            }
        }

        return Task.FromResult(containers.Length);
    }

    private static bool IsPatchContainer(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_P_", StringComparison.OrdinalIgnoreCase);
    }

    public async Task RegisterOnDemandAsync(byte[] tocBytes, string tocName, string sourceName)
    {
        // Keep identically named Live and Studio TOCs separate on disk.
        var sourceDirectory = Path.Combine(cacheDirectory, sourceName);
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, Path.GetFileName(tocName));
        await File.WriteAllBytesAsync(path, tocBytes);
        await RegisterVfsAsync(new IoChunkToc(new FByteArchive(path, tocBytes, Versions)));
    }

    public override void Initialize() { }
}
