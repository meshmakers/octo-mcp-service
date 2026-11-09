using System.IO.Compression;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8568: decides whether an uploaded runtime model (YAML, JSON, or a zip of them) writes System.Ai knowledge pages,
///     versions or their links, by the decoded ids instead of the raw text, so JSON or YAML escapes, quoting, aliases and
///     other casing cannot hide a reference.
///     <list type="bullet">
///         <item>
///             JSON is read with the importer's own streaming reader (<see cref="IRtSerializer.DeserializeStreamAsync" />,
///             bulks of entities), so the check sees exactly the entities the asset repository imports.
///         </item>
///         <item>
///             YAML is read with the YamlDotNet parser the importer's deserializer is built on, event by event: every scalar
///             is decoded by the parser (escapes, quotes, folded and block scalars, tags) and aliases are resolved, without
///             loading the document. The importer's YAML reader itself buffers the whole file, so it is not used here.
///         </item>
///     </list>
///     Memory stays bounded for any upload size; a zip entry is streamed and capped at <see cref="MaxEntryBytes" />.
///     The format follows the upload's file extension, case-insensitive: <c>.zip</c>, <c>.json</c>, and <c>.yaml</c> or
///     <c>.yml</c> (both read as YAML). Any other extension is refused with the generic failure message, fail closed,
///     whatever the SDK or the import job would do with it (the import job reads a file of unknown content type as
///     YAML). In a zip every <c>.json</c>, <c>.yaml</c> and <c>.yml</c> entry is checked; the import job picks its
///     entry by the extension <c>.json</c> or <c>.yaml</c> (case-insensitive) and never reads any other entry, so other
///     entries (a README, images) are left alone and do not refuse the zip.
/// </summary>
internal sealed class KnowledgeImportCheck(
    IRtJsonSerializer jsonSerializer,
    ICkCacheService ckCache,
    string tenantId)
{
    /// <summary>Decompressed bytes read from one zip entry at most; above it the import is refused (zip bombs).</summary>
    internal const long MaxEntryBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>Anchored YAML scalars kept for alias resolution at most; above it the file is refused.</summary>
    internal const int MaxYamlAnchors = 10_000;

    /// <summary>Nested YAML mappings and sequences at most; deeper files are refused (DoS).</summary>
    internal const int MaxYamlDepth = 64;

    /// <summary>
    ///     Memory (UTF-16 bytes) of the anchored YAML scalar values kept for alias resolution at most, over all anchors of
    ///     one document stream; above it the file is refused (DoS). A redefined anchor releases its previous value.
    /// </summary>
    internal const long MaxYamlAnchorBytes = 16L * 1024 * 1024;

    private const string ProtectedModel = "System.Ai";

    private static readonly string[] ProtectedTypeNames = ["KnowledgePage", "KnowledgePageVersion"];

    private static readonly string[] ProtectedRoleNames = ["LinksTo", "GeneratedFrom", "VersionOf"];

    private static readonly RtCkId<CkTypeId> KnowledgePage = new("System.Ai/KnowledgePage");

    private long _entryBytesLimit = MaxEntryBytes;

    private long _yamlAnchorBytesLimit = MaxYamlAnchorBytes;

    /// <summary>Lowers the per-entry cap; tests only.</summary>
    internal KnowledgeImportCheck WithEntryBytesLimit(long limit)
    {
        _entryBytesLimit = limit;
        return this;
    }

    /// <summary>Lowers the budget of retained anchored YAML scalars; tests only.</summary>
    internal KnowledgeImportCheck WithYamlAnchorBytesLimit(long limit)
    {
        _yamlAnchorBytesLimit = limit;
        return this;
    }

    /// <summary>
    ///     The refusal message, or <c>null</c> when the file references no knowledge type or role. A file with an
    ///     extension other than <c>.zip</c>, <c>.json</c>, <c>.yaml</c> or <c>.yml</c> gets
    ///     <see cref="KnowledgeWriteRefusal.ImportCheckFailedMessage" />. Throws when the file cannot be read; the caller
    ///     refuses the import then (fail closed).
    /// </summary>
    public async Task<string?> FindRefusalAsync(string filePath, CancellationToken cancellationToken = default)
    {
        switch (Path.GetExtension(filePath).ToLowerInvariant())
        {
            case ".zip":
                return await CheckZipAsync(filePath, cancellationToken);
            case ".json":
            {
                await using var stream = File.OpenRead(filePath);
                return await CheckJsonAsync(stream, cancellationToken);
            }
            case ".yaml" or ".yml":
            {
                await using var stream = File.OpenRead(filePath);
                return CheckYaml(stream, cancellationToken);
            }
            default:
                // Fail closed: a format this check cannot read is never passed on unchecked.
                return KnowledgeWriteRefusal.ImportCheckFailedMessage;
        }
    }

    private async Task<string?> CheckZipAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var archive = await ZipFile.OpenReadAsync(filePath, cancellationToken);
        foreach (var entry in archive.Entries)
        {
            var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
            if (extension is not (".json" or ".yaml" or ".yml"))
            {
                continue; // the import job reads only a .json or .yaml entry; .yml is checked as well
            }

            await using var entryStream = new CappedReadStream(await entry.OpenAsync(cancellationToken), _entryBytesLimit);
            var refusal = extension == ".json"
                ? await CheckJsonAsync(entryStream, cancellationToken)
                : CheckYaml(entryStream, cancellationToken);
            if (refusal != null)
            {
                return refusal;
            }
        }

        return null;
    }

    private async Task<string?> CheckJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var deserializeStream = await jsonSerializer.DeserializeStreamAsync(stream, cancellationToken);
        await foreach (var bulk in deserializeStream.ReadBulksAsync(cancellationToken))
        {
            foreach (var entity in bulk)
            {
                var refusal = CheckEntity(entity);
                if (refusal != null)
                {
                    return refusal;
                }
            }
        }

        return null;
    }

    private string? CheckEntity(RtEntityTcDto entity)
    {
        var refusal = CheckTypeId(entity.CkTypeId?.ToString());
        if (refusal != null)
        {
            return refusal;
        }

        foreach (var association in entity.Associations ?? [])
        {
            refusal = CheckRoleId(association.RoleId?.ToString()) ?? CheckTypeId(association.TargetCkTypeId?.ToString());
            if (refusal != null)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>
    ///     Streams the YAML events. A scalar under a key <c>ckTypeId</c> or <c>targetCkTypeId</c> gets the full type check
    ///     (protected, derived via the CK type hierarchy, unknown in a tenant with knowledge types); a scalar under
    ///     <c>roleId</c> the role check; every other scalar is refused when it is a protected type or role id itself.
    ///     Keys are compared case-insensitively, a superset of the deserializer. Bounded against hostile input: more than
    ///     <see cref="MaxYamlDepth" /> nested containers, more than <see cref="MaxYamlAnchors" /> anchored scalars or more
    ///     than <see cref="MaxYamlAnchorBytes" /> of retained anchored values throw, and the caller refuses the import.
    /// </summary>
    private string? CheckYaml(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var parser = new Parser(reader);
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
        long anchorBytes = 0;
        var frames = new Stack<YamlFrame>();

        while (parser.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (parser.Current)
            {
                case Scalar scalar:
                    if (!scalar.Anchor.IsEmpty)
                    {
                        var previous = anchors.TryGetValue(scalar.Anchor.Value, out var replaced) ? replaced : null;
                        if (previous == null && anchors.Count >= MaxYamlAnchors)
                        {
                            throw new InvalidDataException("Too many YAML anchors.");
                        }

                        anchorBytes += ((long)scalar.Value.Length - (previous?.Length ?? 0)) * sizeof(char);
                        if (anchorBytes > _yamlAnchorBytesLimit)
                        {
                            throw new InvalidDataException("The anchored YAML values are larger than the check keeps.");
                        }

                        anchors[scalar.Anchor.Value] = scalar.Value;
                    }

                    var scalarRefusal = OnYamlValue(frames, scalar.Value);
                    if (scalarRefusal != null)
                    {
                        return scalarRefusal;
                    }

                    break;
                case AnchorAlias alias:
                    // An alias of a mapping or sequence was checked where it was defined; an alias of a scalar is checked
                    // again in the position it is used in (it can be a key, or the value of ckTypeId).
                    var aliasRefusal = OnYamlValue(frames,
                        anchors.TryGetValue(alias.Value.Value, out var aliased) ? aliased : null);
                    if (aliasRefusal != null)
                    {
                        return aliasRefusal;
                    }

                    break;
                case MappingStart:
                    PushFrame(frames, true);
                    break;
                case SequenceStart:
                    PushFrame(frames, false);
                    break;
                case MappingEnd or SequenceEnd:
                    frames.Pop();
                    // The finished container is a key or a value of its parent mapping.
                    AdvanceParent(frames, null);
                    break;
            }
        }

        return null;
    }

    private static void PushFrame(Stack<YamlFrame> frames, bool isMapping)
    {
        if (frames.Count >= MaxYamlDepth)
        {
            throw new InvalidDataException("The YAML is nested deeper than the check reads.");
        }

        frames.Push(new YamlFrame(isMapping));
    }

    private string? OnYamlValue(Stack<YamlFrame> frames, string? value)
    {
        if (value != null)
        {
            var parent = frames.Count > 0 ? frames.Peek() : null;
            var key = parent is { IsMapping: true, ExpectKey: false } ? parent.Key : null;
            var refusal = key switch
            {
                _ when key != null && (key.Equals("ckTypeId", StringComparison.OrdinalIgnoreCase) ||
                                       key.Equals("targetCkTypeId", StringComparison.OrdinalIgnoreCase))
                    => CheckTypeId(value),
                _ when key != null && key.Equals("roleId", StringComparison.OrdinalIgnoreCase) => CheckRoleId(value),
                _ => CheckAnyScalar(value)
            };
            if (refusal != null)
            {
                return refusal;
            }
        }

        AdvanceParent(frames, value);
        return null;
    }

    private static void AdvanceParent(Stack<YamlFrame> frames, string? value)
    {
        if (frames.Count == 0 || !frames.Peek().IsMapping)
        {
            return;
        }

        var frame = frames.Peek();
        if (frame.ExpectKey)
        {
            frame.Key = value;
            frame.ExpectKey = false;
        }
        else
        {
            frame.Key = null;
            frame.ExpectKey = true;
        }
    }

    private sealed class YamlFrame(bool isMapping)
    {
        public bool IsMapping { get; } = isMapping;
        public bool ExpectKey { get; set; } = true;
        public string? Key { get; set; }
    }

    /// <summary>A protected type (or a type derived from one, or an unknown type in a knowledge tenant), else <c>null</c>.</summary>
    private string? CheckTypeId(string? ckTypeId)
    {
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return null;
        }

        if (IsProtectedName(ckTypeId, ProtectedTypeNames, out var protectedId))
        {
            return KnowledgeWriteRefusal.ImportMessage(protectedId);
        }

        var runtimeId = AttributeAccessGuard.ToRuntimeId(ckTypeId.Trim());
        if (runtimeId != null && ckCache.TryGetRtCkType(tenantId, runtimeId, out var graph) && graph != null)
        {
            return KnowledgeWriteRefusal.IsKnowledgeType(ckCache, tenantId, runtimeId)
                ? KnowledgeWriteRefusal.ImportMessage(runtimeId.ToString())
                : null;
        }

        // A type this tenant's CK cache does not know cannot be ruled out as a type derived from KnowledgePage; in a
        // tenant that has the knowledge types it is refused (the import of an unknown type fails anyway).
        return ckCache.TryGetRtCkType(tenantId, KnowledgePage, out var page) && page != null
            ? KnowledgeWriteRefusal.UnknownTypeImportMessage(ckTypeId)
            : null;
    }

    private static string? CheckRoleId(string? roleId) =>
        !string.IsNullOrWhiteSpace(roleId) && IsProtectedName(roleId, ProtectedRoleNames, out var protectedId)
            ? KnowledgeWriteRefusal.ImportMessage(protectedId)
            : null;

    private static string? CheckAnyScalar(string value) =>
        value.Contains('/') &&
        (IsProtectedName(value, ProtectedTypeNames, out var protectedId) ||
         IsProtectedName(value, ProtectedRoleNames, out protectedId))
            ? KnowledgeWriteRefusal.ImportMessage(protectedId)
            : null;

    /// <summary>
    ///     <c>System.Ai/Name</c> as a runtime id (<c>System.Ai/Name</c>) or full id (<c>System.Ai-4.7.0/Name-1</c>), any
    ///     casing, surrounding white space ignored.
    /// </summary>
    internal static bool IsProtectedName(string id, IReadOnlyList<string> names, out string protectedId)
    {
        protectedId = string.Empty;
        var trimmed = id.Trim();
        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash == trimmed.Length - 1)
        {
            return false;
        }

        var model = StripVersion(trimmed[..slash]);
        var element = StripVersion(trimmed[(slash + 1)..]);
        if (!model.Equals(ProtectedModel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var name in names)
        {
            if (element.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                protectedId = $"{ProtectedModel}/{name}";
                return true;
            }
        }

        return false;
    }

    // Model-1.2.3 -> Model, Type-1 -> Type (a dash followed by a digit starts the version).
    private static string StripVersion(string part)
    {
        var dash = part.LastIndexOf('-');
        while (dash > 0)
        {
            if (dash < part.Length - 1 && char.IsDigit(part[dash + 1]))
            {
                return part[..dash];
            }

            dash = part.LastIndexOf('-', dash - 1);
        }

        return part;
    }

    /// <summary>Read-only stream that throws once more than the limit was read (decompression bombs).</summary>
    private sealed class CappedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        private int Count(int read)
        {
            _read += read;
            if (_read > limit)
            {
                throw new InvalidDataException("A zip entry is larger than the check reads.");
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
