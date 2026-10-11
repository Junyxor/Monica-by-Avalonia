using Monica.Core.Models;

namespace Monica.Data.Mdbx;

public sealed record MdbxLanBundle(string VaultId, uint Commits, ulong Bytes, string Revision = "");
public sealed record MdbxLanMerge(uint Applied, uint Skipped, uint Conflicts);

public interface IMdbxNativeLanSync
{
    Task<MdbxLanBundle> ExportLanBundleAsync(string destination, CancellationToken cancellationToken);
    Task<MdbxLanMerge> ApplyLanBundleAsync(string source, CancellationToken cancellationToken);
    Task<string> GetLanRevisionAsync(CancellationToken cancellationToken);
}

public interface IMdbxLanSyncStore
{
    Task<MdbxLanBundle> PrepareLanArtifactsAsync(LocalMdbxDatabase database, string directory, bool includeBootstrap, CancellationToken cancellationToken);
    Task<MdbxLanMerge> MergeLanBundleAsync(LocalMdbxDatabase database, string bundle, CancellationToken cancellationToken);
    Task<string> GetLanRevisionAsync(LocalMdbxDatabase database, CancellationToken cancellationToken);
}

public interface IMdbxLanSyncMutationObserver
{
    Task OnLanSyncAppliedAsync(LocalMdbxDatabase database);
}

public sealed partial class MdbxVaultStore : IMdbxLanSyncStore
{
    public async Task<string> GetLanRevisionAsync(LocalMdbxDatabase database, CancellationToken cancellationToken)
    {
        using var lease = await OpenAsync(database, cancellationToken);
        return lease is IMdbxNativeLanSync sync
            ? await sync.GetLanRevisionAsync(cancellationToken)
            : throw new NotSupportedException("device_sync_unavailable");
    }
    public async Task<MdbxLanBundle> PrepareLanArtifactsAsync(LocalMdbxDatabase database, string directory, bool includeBootstrap, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        using var lease = await OpenAsync(database, cancellationToken);
        if (lease.IsReadOnly || lease is not IMdbxNativeLanSync sync)
            throw new NotSupportedException("LAN synchronization is unavailable for this vault.");
        var bundle = await sync.ExportLanBundleAsync(Path.Combine(directory, "changes.mdbxsync"), cancellationToken);
        if (includeBootstrap)
        {
            if (_nativeBridge is not IMdbxNativeSnapshotBridge snapshots)
                throw new NotSupportedException("Native vault snapshots are unavailable.");
            var snapshot = await snapshots.CreatePortableBackupAsync(database.WorkingCopyPath ?? database.FilePath,
                Path.Combine(directory, "vault.mdbx"), cancellationToken);
            if (snapshot.VaultId != bundle.VaultId) throw new InvalidOperationException("Vault identity changed.");
        }
        return bundle;
    }

    public async Task<MdbxLanMerge> MergeLanBundleAsync(LocalMdbxDatabase database, string bundle, CancellationToken cancellationToken)
    {
        using var lease = await OpenAsync(database, cancellationToken);
        if (lease.IsReadOnly || lease is not IMdbxNativeLanSync sync)
            throw new NotSupportedException("LAN synchronization is unavailable for this vault.");
        return await sync.ApplyLanBundleAsync(bundle, cancellationToken);
    }
}
