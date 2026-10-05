using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class CharacterSupplementalSourceAuthoring
{
    public const int MaximumArchiveEntries = 100_000;
    public const int MaximumDirectoryBytes = 64 * 1024 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scr", ".def", ".phx", ".bel", ".fx", ".mat", ".chr", ".ascr", ".bscr", ".mpcloth",
    };

    public static async Task<CustomModelPackage> AttachAsync(
        CustomModelPackage package, string archivePath, string memberName, string virtualName,
        CharacterSubsystem subsystem, string expectedSha256, bool reviewed,
        string? expectedArchiveSha256 = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!reviewed) throw new InvalidOperationException("Review the selected source, then pass --reviewed.");
        if (!Enum.IsDefined(subsystem)) throw new ArgumentException("Choose a character subsystem.");
        CharacterSupplementalSourceReceipt.ValidatePortablePath(memberName);
        CharacterSupplementalSourceReceipt.ValidatePortablePath(virtualName);
        if (!string.Equals(Path.GetExtension(memberName), Path.GetExtension(virtualName), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The ZIP member and virtual resource must have matching extensions.");
        ValidateExpectedHash(expectedSha256, nameof(expectedSha256));
        if (expectedArchiveSha256 is not null)
            ValidateExpectedHash(expectedArchiveSha256, nameof(expectedArchiveSha256));
        var inventory = package.Document.CharacterResources
            ?? throw new InvalidOperationException("The model has no character resource inventory.");
        inventory.Validate();
        var matches = inventory.Resources.Where(resource =>
            string.Equals(resource.LogicalName, virtualName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Any(resource => resource.Id == inventory.RootResourceId))
            throw new InvalidOperationException("The character root cannot be replaced by a supplemental source.");
        if (matches.Any(resource => resource.EntryPath is not null ||
            resource.LogicalName != virtualName || resource.Status is not (CharacterDependencyStatus.Missing or CharacterDependencyStatus.Unsupported)))
            throw new InvalidOperationException("The virtual resource identity is already present or ambiguous.");
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long archiveLength = input.Length;
        if (archiveLength <= 0 || archiveLength > CharacterSupplementalSourceReceipt.MaximumArchiveBytes)
            throw new InvalidDataException("The ZIP exceeds the supported archive size limit.");
        await CheckDirectoryBoundsAsync(input, cancellationToken).ConfigureAwait(false);
        input.Position = 0;
        string archiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        if (expectedArchiveSha256 is not null && !string.Equals(expectedArchiveSha256, archiveHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive SHA-256 does not match --archive-sha256.");
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The ZIP has too many entries.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName.EndsWith('/') ? entry.FullName[..^1] : entry.FullName;
            CharacterSupplementalSourceReceipt.ValidatePortablePath(name);
            if (!identities.Add(name))
                throw new InvalidDataException("The ZIP contains duplicate or case-ambiguous paths.");
        }
        var selected = archive.GetEntry(memberName)
            ?? throw new InvalidDataException("The exact ZIP member was not found.");
        if (selected.FullName.EndsWith('/') || selected.Length <= 0 ||
            selected.Length > CharacterSupplementalSourceReceipt.MaximumMemberBytes ||
            selected.CompressedLength < 0 || selected.CompressedLength > archiveLength)
            throw new InvalidDataException("The selected ZIP member is empty or exceeds its size limit.");
        byte[] bytes = new byte[checked((int)selected.Length)];
        await using (var member = selected.Open())
        {
            await member.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            byte[] extra = new byte[1];
            if (await member.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException("The selected ZIP member exceeds its declared length.");
        }
        string contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(expectedSha256, contentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source SHA-256 does not match --expected-sha256.");
        ValidateSource(bytes, virtualName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Length != archiveLength)
            throw new IOException("The ZIP changed while reading.");
        var receipt = new CharacterSupplementalSourceReceipt
        {
            ArchiveSha256 = archiveHash, ArchiveByteLength = archiveLength,
            MemberName = memberName, VirtualName = virtualName,
            ContentSha256 = contentHash, ByteLength = bytes.Length, Reviewed = true,
        };
        string identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            archiveHash + "\n" + memberName + "\n" + virtualName + "\n" + contentHash)));
        string id = "supplemental:" + identityHash;
        string entryPath = "character/resources/supplemental/" + identityHash + ".bin";
        if (inventory.Resources.Any(resource => resource.Id == id) || package.CompanionPayloads.ContainsKey(entryPath))
            throw new InvalidOperationException("The supplemental source is already present.");
        var record = new CharacterResourceRecord
        {
            Id = id, LogicalName = virtualName, ProviderIdentity = "zip-sha256:" + archiveHash,
            SourceFingerprint = archiveHash, ContentSha256 = contentHash, EntryPath = entryPath,
            ByteLength = bytes.Length, Subsystem = subsystem,
            Status = CharacterDependencyStatus.Ambiguous, Required = true,
            Detail = "Supplemental source attached; review its character association.",
            SupplementalSource = receipt,
        };
        receipt.Validate(record);
        var document = package.Document with
        {
            LastBuildReceipt = null,
            CharacterResources = inventory with
            {
                Resources = inventory.Resources.Add(record),
                CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [],
            },
        };
        document.Validate();
        return package with
        {
            Document = document,
            CompanionPayloads = package.CompanionPayloads.Add(entryPath, ImmutableArray.Create(bytes)),
        };
    }

    private static void ValidateExpectedHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Provide a SHA-256 value with exactly 64 hexadecimal characters.", parameterName);
    }

    private static void ValidateSource(byte[] bytes, string virtualName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(virtualName);
        if (TextExtensions.Contains(extension))
        {
            if (bytes.Length > NativeCharacterScriptCodec.MaximumCharacters * 4L + 3)
                throw new InvalidDataException("The supplemental source script exceeds its size limit.");
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
            catch (DecoderFallbackException exception)
            { throw new InvalidDataException("The supplemental source script is not valid UTF-8.", exception); }
            if (text.Length > NativeCharacterScriptCodec.MaximumCharacters || text.Contains('\0'))
                throw new InvalidDataException("The supplemental source script is oversized or contains NUL.");
        }
        else if (extension.Equals(".fed", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new MemoryStream(bytes, writable: false);
            _ = FedReader.Read(stream, Path.GetFileNameWithoutExtension(virtualName));
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task CheckDirectoryBoundsAsync(FileStream stream, CancellationToken token)
    {
        int tailLength = checked((int)Math.Min(stream.Length, 65_557));
        byte[] tail = await ReadAtAsync(stream, stream.Length - tailLength, tailLength, token).ConfigureAwait(false);
        int end = -1;
        for (int index = tail.Length - 22; index >= 0; index--)
            if (U32(tail, index) == 0x06054b50 && index + 22 + U16(tail, index + 20) == tail.Length)
            { end = index; break; }
        if (end < 0) throw new InvalidDataException("The ZIP end record was not found.");
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 ||
            U16(tail, end + 8) != U16(tail, end + 10))
            throw new InvalidDataException("Multi-volume ZIP archives are not supported.");
        ulong entries = U16(tail, end + 10);
        ulong directorySize = U32(tail, end + 12);
        ulong directoryOffset = U32(tail, end + 16);
        long boundary = stream.Length - tailLength + end;
        if (entries == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            if (boundary < 20) throw new InvalidDataException("The ZIP64 locator is missing.");
            byte[] locator = await ReadAtAsync(stream, boundary - 20, 20, token).ConfigureAwait(false);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1)
                throw new InvalidDataException("The ZIP64 locator is invalid.");
            ulong offset = U64(locator, 8);
            if (offset > (ulong)(boundary - 20) || (ulong)(boundary - 20) - offset < 56)
                throw new InvalidDataException("The ZIP64 end record is outside the archive.");
            byte[] header = await ReadAtAsync(stream, (long)offset, 56, token).ConfigureAwait(false);
            if (U32(header, 0) != 0x06064b50 || U64(header, 4) < 44 ||
                U32(header, 16) != 0 || U32(header, 20) != 0 || U64(header, 24) != U64(header, 32))
                throw new InvalidDataException("The ZIP64 end record is invalid.");
            entries = U64(header, 32);
            directorySize = U64(header, 40);
            directoryOffset = U64(header, 48);
            boundary = (long)offset;
        }
        if (entries > MaximumArchiveEntries || directorySize > MaximumDirectoryBytes ||
            directoryOffset > (ulong)boundary || directorySize > (ulong)boundary - directoryOffset)
            throw new InvalidDataException("The ZIP directory exceeds its supported count or size limits.");
        byte[] directory = await ReadAtAsync(stream, (long)directoryOffset, (int)directorySize, token).ConfigureAwait(false);
        int cursor = 0;
        ulong actualEntries = 0;
        while (cursor < directory.Length)
        {
            token.ThrowIfCancellationRequested();
            if (directory.Length - cursor < 46 || U32(directory, cursor) != 0x02014b50)
                throw new InvalidDataException("The ZIP directory record is invalid.");
            int length = 46 + U16(directory, cursor + 28) + U16(directory, cursor + 30) + U16(directory, cursor + 32);
            if (length > directory.Length - cursor || ++actualEntries > MaximumArchiveEntries)
                throw new InvalidDataException("The ZIP directory exceeds its supported count or size limits.");
            cursor += length;
        }
        if (actualEntries != entries)
            throw new InvalidDataException("The ZIP directory entry count does not match its end record.");
    }

    private static async Task<byte[]> ReadAtAsync(FileStream stream, long offset, int length, CancellationToken token)
    {
        if (offset < 0 || offset > stream.Length || length > stream.Length - offset)
            throw new InvalidDataException("The ZIP record is outside the archive.");
        stream.Position = offset;
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
}