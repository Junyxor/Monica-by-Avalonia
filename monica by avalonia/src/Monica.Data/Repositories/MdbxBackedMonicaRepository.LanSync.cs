using Monica.Core.Models;
using Monica.Data.Mdbx;

namespace Monica.Data.Repositories;

public sealed partial class MdbxBackedMonicaRepository : IMdbxLanSyncMutationObserver
{
    public async Task OnLanSyncAppliedAsync(LocalMdbxDatabase database)
    {
        try
        {
            // Use the same metadata gate and fresh validators as ordinary native writes.
            await MarkRemoteWorkingCopyPendingAsync(database);
        }
        finally
        {
            ClearCategoryReadCache();
            ClearPasswordReadSnapshot();
            ClearSecureItemReadSnapshot();
        }
    }
}
