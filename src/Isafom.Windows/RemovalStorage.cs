using System.IO;
using Isafom.Core;

namespace Isafom.Windows;

internal static class RemovalStorage
{
    private static string Root=>Path.GetFullPath(AdminConfiguration.DirectoryPath);
    private static string[] Caches=>new[]{"driver-cache","driver-kx85-cache"}.Select(name=>Path.GetFullPath(Path.Combine(Root,name))).ToArray();
    private static string Settings=>Path.GetFullPath(Path.Combine(Root,"printer-settings.json"));
    private static string Manifest=>Path.GetFullPath(Path.Combine(Root,"removal-files.json"));

    internal static void Validate()
    {
        if(Caches.Any(cache=>!string.Equals(Path.GetDirectoryName(cache),Root,StringComparison.OrdinalIgnoreCase))||
            !string.Equals(Path.GetDirectoryName(Settings),Root,StringComparison.OrdinalIgnoreCase))throw new InstallerException(ErrorCode.Configuration);
        if(Directory.Exists(Root))AdminConfiguration.EnsureProtected(Root);
        if(Caches.Any(File.Exists))throw new InstallerException(ErrorCode.Configuration);
        int count=0;
        void Visit(string directory,int depth)
        {
            if(depth>32||++count>20000)throw new InstallerException(ErrorCode.Configuration);
            AdminConfiguration.EnsureProtected(directory);
            foreach(string item in Directory.EnumerateFileSystemEntries(directory))
            {
                if(++count>20000)throw new InstallerException(ErrorCode.Configuration);
                AdminConfiguration.EnsureProtected(item);
                if(Directory.Exists(item))Visit(item,depth+1);
            }
        }
        foreach(string cache in Caches)if(Directory.Exists(cache))Visit(cache,0);
        if(File.Exists(Settings))AdminConfiguration.EnsureProtected(Settings);
        if(File.Exists(Manifest))
        {
            AdminConfiguration.EnsureProtected(Manifest);
            if(new FileInfo(Manifest).Length>1024*1024)throw new InstallerException(ErrorCode.Configuration);
        }
        if(Directory.Exists(Manifest))throw new InstallerException(ErrorCode.Configuration);
        if(Directory.Exists(Settings))throw new InstallerException(ErrorCode.Configuration);
    }

    internal static bool Clear()
    {
        Validate(); // Revalidate canonical paths, ACLs and every link before recursion.
        foreach(string cache in Caches)if(Directory.Exists(cache))Directory.Delete(cache,recursive:true);
        if(File.Exists(Settings))File.Delete(Settings);
        if(File.Exists(Manifest))File.Delete(Manifest);
        if(Directory.Exists(Root)&&!Directory.EnumerateFileSystemEntries(Root).Any())Directory.Delete(Root,recursive:false);
        return Caches.All(cache=>!Directory.Exists(cache))&&!File.Exists(Settings)&&!File.Exists(Manifest);
    }
}
