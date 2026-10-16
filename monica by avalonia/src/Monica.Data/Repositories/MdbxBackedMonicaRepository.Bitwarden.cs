using Monica.Core.Bitwarden;

namespace Monica.Data.Repositories;

public sealed partial class MdbxBackedMonicaRepository
{
    public async Task<bool> TryAcknowledgeBitwardenMutationAsync(
        BitwardenMutationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        var database = await RequireDefaultMdbxDatabaseAsync(cancellationToken);
        if (database.Id != acknowledgement.MdbxDatabaseId) return false;
        var updated = false;
        try
        {
            await ExecuteMdbxMutationAsync(database, async () =>
                updated = await mdbxVaultStore.TryAcknowledgeBitwardenMutationAsync(database, acknowledgement, cancellationToken));
            return updated;
        }
        finally
        {
            ClearPasswordReadSnapshot();
            ClearSecureItemReadSnapshot();
        }
    }
}
