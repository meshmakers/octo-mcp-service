using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Hands a new file transfer to the calling MCP client: registers it for the caller and issues its one-time transfer
///     key. Shared by <c>prepare_file_upload</c> and every download-producing tool, so all of them refuse a
///     caller that cannot own a transfer (no principal, or a client-credentials token without <c>tenant_id</c>) the same
///     way, and none of them hands out a transfer without a key.
/// </summary>
internal static class FileTransferHandout
{
    /// <summary>Error of a tool whose caller cannot own a file transfer. Names no id or key.</summary>
    internal const string OwnerRequiredError =
        "File transfers need an identifiable caller with a tenant: the token must carry a 'tenant_id' claim " +
        "(a client-credentials token without one is refused).";

    /// <summary>A registered transfer and its key.</summary>
    /// <param name="TransferId">Transfer id for the <c>X-Transfer-Id</c> header.</param>
    /// <param name="Key">One-time key for the <c>X-Transfer-Key</c> header.</param>
    /// <param name="TargetPath">For an upload, the path the HTTP endpoint writes to; null for a download.</param>
    internal sealed record Handout(string TransferId, IssuedTransferKey Key, string? TargetPath);

    /// <summary>True when the current caller may own a file transfer.</summary>
    /// <param name="server">MCP server instance — provides DI access to the request context.</param>
    internal static bool CallerCanOwnTransfer(McpServer server) =>
        McpSessionContext.GetFileTransferOwner(server).CanOwnTransfer;

    /// <summary>Reserves an upload slot for the caller and issues its key; null when the caller cannot own one.</summary>
    internal static Handout? ReserveUpload(McpServer server, IFileTransferStore store, string fileName)
    {
        var owner = McpSessionContext.GetFileTransferOwner(server);
        if (!owner.CanOwnTransfer)
        {
            return null;
        }

        var (transferId, targetPath) = store.ReserveUpload(owner, fileName);
        var key = store.IssueTransferKey(transferId, FileTransferDirection.Upload, owner);
        if (key == null)
        {
            store.DeleteUpload(transferId);
            return null;
        }

        return new Handout(transferId, key, targetPath);
    }

    /// <summary>
    ///     Registers <paramref name="tempPath" /> as a download of the caller and issues its key. When the caller cannot
    ///     own a transfer the file is deleted and null is returned.
    /// </summary>
    internal static Handout? RegisterDownload(McpServer server, IFileTransferStore store, string tempPath,
        string fileName)
    {
        var owner = McpSessionContext.GetFileTransferOwner(server);
        if (!owner.CanOwnTransfer)
        {
            TryDeleteFile(tempPath);
            return null;
        }

        var transferId = store.RegisterDownload(owner, tempPath, fileName);
        var key = store.IssueTransferKey(transferId, FileTransferDirection.Download, owner);
        if (key == null)
        {
            store.DeleteDownload(transferId);
            return null;
        }

        return new Handout(transferId, key, null);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The temp folder is cleaned by the OS; nothing else to do.
        }
    }
}
