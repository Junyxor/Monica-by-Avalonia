using Monica.Core.Models;

namespace Monica.Tests;

public sealed partial class PasswordManagementTests
{
    [Fact]
    public async Task Bulk_delete_coalesces_password_count_refreshes()
    {
        var harness = CreateHarness();
        for (var index = 0; index < 40; index++)
        {
            await harness.Repository.SavePasswordAsync(new PasswordEntry
            {
                Title = $"Synthetic account {index}",
                Password = "synthetic secret"
            });
        }
        await harness.ViewModel.LoadAsync();
        foreach (var entry in harness.ViewModel.Passwords)
        {
            entry.IsSelected = true;
        }

        var refreshes = 0;
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(harness.ViewModel.PasswordCountText)) refreshes++;
        };

        await harness.ViewModel.DeleteSelectedPasswordsCommand.ExecuteAsync(null);

        Assert.Empty(harness.ViewModel.Passwords);
        Assert.Equal(40, harness.ViewModel.DeletedPasswords.Count);
        Assert.Equal(40, harness.ViewModel.PasswordDeleteTotal);
        Assert.Equal(40, harness.ViewModel.PasswordDeleteProcessed);
        Assert.False(harness.ViewModel.IsDeletingPasswords);
        Assert.True(refreshes <= 2, $"One batch triggered {refreshes} password count refreshes.");
    }
}
