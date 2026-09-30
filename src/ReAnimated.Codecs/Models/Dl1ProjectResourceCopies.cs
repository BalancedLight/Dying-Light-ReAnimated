using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1ProjectArchiveScanLimits
{
    public int MaximumFiles { get; init; } = 100_000;
    public int MaximumDirectories { get; init; } = 10_000;
    public int MaximumArchives { get; init; } = 256;
    public int MaximumMatches { get; init; } = 4096;
    public int MaximumDiagnostics { get; init; } = 256;
    public long MaximumArchiveBytes { get; init; } = 2L*1024*1024*1024;
    public long MaximumTotalHashBytes { get; init; } = 4L*1024*1024*1024;
    public int MaximumTableBytes { get; init; } = 32*1024*1024;
    public long MaximumTotalTableBytes { get; init; } = 128L*1024*1024;
}

public sealed record Dl1ProjectResourceCopy(string RelativePath, short ResourceType, string ResourceName,
    int ResourceIndex, long ContainerBytes, string? ContainerSha256, Dl1DeploymentArtifactRole? ReceiptRole)
{
    public bool RecordedByReceipt => ReceiptRole is not null;
    public string Summary => $"{ResourceName} (type {ResourceType}) in {RelativePath}" +
        (ReceiptRole is { } role ? $" — receipt role: {role}" : " — additional project copy");
}

public sealed record Dl1ProjectArchiveScan(bool Complete, int FilesVisited, int DirectoriesVisited,
    int ArchivesInspected, long BytesHashed, long TableBytesRead,
    ImmutableArray<Dl1ProjectResourceCopy> Copies, ImmutableArray<string> Diagnostics)
{
    public string Scope { get; } = "Project-root RPack files, data, assets_pc, out and .dl-reanimated/animation-refresh/packages. Backup/deployment-history folders are outside this scan.";
    public bool ActiveProviderVerified { get; }
    public string HashDomain { get; } = "Container file bytes only; resource payload equivalence is not inferred.";
}

/// <summary>Bounded read-only RPack name/type inventory, not a native mount resolver.</summary>
public static class Dl1ProjectResourceCopies
{
    private static readonly string[] Subdirectories = ["data","assets_pc","out",".dl-reanimated/animation-refresh/packages"];

    public static async Task<Dl1ProjectArchiveScan> InspectAsync(string projectRoot,
        Dl1DeveloperToolsDeploymentReceipt receipt, Dl1ProjectArchiveScanLimits? limits=null,
        CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);ArgumentNullException.ThrowIfNull(receipt);
        limits??=new();ValidateLimits(limits);
        string root=Path.GetFullPath(projectRoot);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("The selected project does not exist.");
        RejectReparse(root);
        string prefix=Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar;
        var targets=new HashSet<(short Type,string Name)>();
        Add(Rp6lResourceTypes.Mesh,receipt.ModelResourceName);
        Add(Rp6lResourceTypes.AnimationScript,receipt.AnimationLibraryName);
        var owned=new Dictionary<string,Dl1DeploymentArtifactRole>(StringComparer.OrdinalIgnoreCase);
        foreach(var artifact in receipt.Artifacts)
        {
            string relative=artifact.RelativePath.Replace('\\','/');
            if(!owned.TryAdd(relative,artifact.Role))throw new InvalidDataException("Receipt artifact paths are duplicated.");
            string extension=Path.GetExtension(relative).ToLowerInvariant();
            if(extension is ".anm2" or ".anm2_obj")Add(Rp6lResourceTypes.Animation,Path.GetFileNameWithoutExtension(relative));
            if(extension is ".dds" or ".dds_obj")Add(Rp6lResourceTypes.Texture,Path.GetFileNameWithoutExtension(relative));
        }
        int files=0,directories=0,archives=0;long hashed=0,tables=0;
        bool complete=true,stop=false;
        var copies=ImmutableArray.CreateBuilder<Dl1ProjectResourceCopy>();
        var diagnostics=ImmutableArray.CreateBuilder<string>();
        var pending=new Stack<(string Path,bool Recurse)>();
        pending.Push((root,false));
        foreach(string subdirectory in Subdirectories.Reverse())
        {
            string path=Path.Combine(root,subdirectory.Replace('/',Path.DirectorySeparatorChar));
            if(!Directory.Exists(path))continue;
            string check=root;
            foreach(string segment in subdirectory.Split('/')){check=Path.Combine(check,segment);RejectReparse(check);}
            pending.Push((path,true));
        }
        var archiveLimits=new Rp6lLimits {MaximumTableBytes=limits.MaximumTableBytes,MaximumNameBlobBytes=limits.MaximumTableBytes,
            MaximumTableCount=Math.Min(2_000_000,limits.MaximumTableBytes/4)};
        while(pending.TryPop(out var directory)&&!stop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(++directories>limits.MaximumDirectories){Incomplete("Directory scan limit reached.");break;}
            try
            {
                RejectReparse(directory.Path);
                foreach(string path in Directory.EnumerateFiles(directory.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if(++files>limits.MaximumFiles){Incomplete("File scan limit reached.");stop=true;break;}
                    if(!Path.GetExtension(path).Equals(".rpack",StringComparison.OrdinalIgnoreCase))continue;
                    string relative=Relative(path);
                    if(archives>=limits.MaximumArchives){Incomplete("Archive scan limit reached.");stop=true;break;}
                    archives++;
                    try
                    {
                        RejectReparse(path);
                        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
                        long length=stream.Length;
                        if(length>limits.MaximumArchiveBytes){Incomplete(relative+": archive size exceeds the scan limit.");continue;}
                        // Bound aggregate table allocation before opening the archive parser.
                        byte[] header=new byte[36];await stream.ReadExactlyAsync(header,cancellationToken).ConfigureAwait(false);
                        long tableBytes=ReadTableBytes(header);
                        if(tableBytes>limits.MaximumTableBytes||tableBytes>limits.MaximumTotalTableBytes-tables)
                        {Incomplete(relative+": archive table budget exceeded.");continue;}
                        tables+=tableBytes;
                        var archive=await Rp6lArchive.OpenAsync(path,archiveLimits,cancellationToken).ConfigureAwait(false);
                        var matches=archive.Resources.Where(r=>targets.Contains((r.ResourceType,r.Name.ToLowerInvariant()))).ToArray();
                        if(matches.Length==0)continue;
                        string? hash=null;
                        if(length<=limits.MaximumTotalHashBytes-hashed)
                        {
                            stream.Position=0;hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,cancellationToken).ConfigureAwait(false));hashed+=length;
                        }
                        else Incomplete(relative+": matching resource names found, but the container hash budget was exhausted.");
                        foreach(var match in matches)
                        {
                            if(copies.Count>=limits.MaximumMatches){Incomplete("Resource match limit reached.");stop=true;break;}
                            copies.Add(new(relative,match.ResourceType,match.Name,match.Index,length,hash,
                                owned.TryGetValue(relative,out var role)?role:null));
                        }
                    }
                    catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or NotSupportedException)
                    {Incomplete(relative+": "+error.Message);}
                    if(stop)break;
                }
                if(directory.Recurse&&!stop)
                    foreach(string child in Directory.EnumerateDirectories(directory.Path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if(pending.Count+directories>=limits.MaximumDirectories){Incomplete("Directory scan limit reached.");stop=true;break;}
                        pending.Push((child,true));
                    }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidDataException)
            {Incomplete(Relative(directory.Path)+": "+error.Message);}
        }
        return new(complete,files,directories,archives,hashed,tables,
            copies.OrderBy(static c=>c.RelativePath,StringComparer.OrdinalIgnoreCase).ThenBy(static c=>c.ResourceIndex).ToImmutableArray(),diagnostics.ToImmutable());

        void Add(short type,string name)
        {ArgumentException.ThrowIfNullOrWhiteSpace(name);targets.Add((type,name.ToLowerInvariant()));}
        void Incomplete(string message)
        {
            complete=false;
            if(diagnostics.Count<limits.MaximumDiagnostics)diagnostics.Add(message);
            else if(diagnostics.Count==limits.MaximumDiagnostics)diagnostics.Add("Additional archive diagnostics omitted after the configured limit.");
        }
        string Relative(string path)
        {
            string full=Path.GetFullPath(path);
            if(full!=root&&!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Archive scan path escaped the selected project.");
            return Path.GetRelativePath(root,full).Replace('\\','/');
        }
    }

    private static long ReadTableBytes(byte[] bytes)
    {
        if(!bytes.AsSpan(0,4).SequenceEqual("RP6L"u8))throw new InvalidDataException("Not an RP6L archive.");
        int items=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int chunks=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        int resources=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        int blob=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        int names=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28));
        if(items<0||chunks<0||resources<0||blob<0||names<0)throw new InvalidDataException("Negative archive table size.");
        return checked(20L*chunks+16L*items+12L*resources+4L*names+blob);
    }
    private static void RejectReparse(string path)
    {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Archive inventory refuses reparse points.");}
    private static void ValidateLimits(Dl1ProjectArchiveScanLimits limits)
    {
        if(limits.MaximumFiles<=0||limits.MaximumDirectories<=0||limits.MaximumArchives<=0||limits.MaximumMatches<=0||limits.MaximumDiagnostics<=0||
            limits.MaximumArchiveBytes<=0||limits.MaximumTotalHashBytes<=0||limits.MaximumTableBytes<4||limits.MaximumTotalTableBytes<=0)
            throw new ArgumentException("Archive scan limits must be positive.",nameof(limits));
    }
}
