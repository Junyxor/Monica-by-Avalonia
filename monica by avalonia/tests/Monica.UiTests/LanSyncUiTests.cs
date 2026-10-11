using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Monica.App.Features.Sync;
using Monica.App.Services;
using Monica.App.ViewModels;
using Monica.Data.Mdbx;
using QRCoder;

namespace Monica.UiTests;

[Collection(AvaloniaUiTestCollection.Name)]
[Trait("Category", "lan-sync")]
public sealed class LanSyncUiTests
{
    public LanSyncUiTests() => AvaloniaUiThreadTestContext.VerifyAccess();

    [Fact]
    public void Request_failures_show_operation_and_byte_count_without_pairing_secrets()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var secret = new string('A', 43);
        vm.ApplyDeviceSyncState(new DeviceSyncState(true, 0, [], Phase: "failed", Code: secret,
            Error: "invalid_request", FailureStage: "preparing", FailureKind: "LanSyncException",
            FailureOperation: "replica_scan_batch", FailureRequestBytes: 55851));
        Assert.Contains("replica_scan_batch", vm.LanDiagnosticText);
        Assert.Contains("55851", vm.LanDiagnosticText);
        Assert.DoesNotContain(secret, vm.LanDiagnosticText);
        vm.ApplyDeviceSyncState(new DeviceSyncState(true, 0, [], Phase: "failed",
            Error: "request_too_large", FailureStage: "preparing"));
        Assert.Contains("单次处理上限", vm.LanErrorText);
        main.Close();
    }

    [Fact]
    public void Multi_interface_qr_keeps_all_addresses_and_autofills_the_pairing_code()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var peer = new LanPeerDisplay("Computer", "192.168.1.5", 53318);
        vm.LanPeers.Add(peer);
        var code = new string('A', 43);
        var link = $"monica-lan:2:10.30.250.111,192.168.1.5:53318:{code}";
        using var qrData = QRCodeGenerator.GenerateQrCode(link, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(qrData);
        vm.LanInvitation = TotpQrCodeDecoder.TryDecode(qr.GetGraphic(6))!;
        Assert.Equal(code, vm.LanPairingCode);
        Assert.Equal(link, vm.LanInvitation);
        Assert.Same(peer, vm.SelectedLanPeer);
        Assert.True(vm.LanPairingAutoFilled);
        main.Close();
    }

    [Fact]
    public void A_connection_failure_has_actionable_text_and_redacted_copyable_diagnostics()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var secret = new string('A', 43);
        vm.ApplyDeviceSyncState(new DeviceSyncState(true, 0, [], Phase: "failed", Code: secret,
            Error: "connection_failed", FailureStage: "connecting", ConnectionAttempts: 2, FailureKind: "LanSyncException"));
        Assert.Contains("无法连接", vm.LanErrorText);
        Assert.Contains("connection_failed", vm.LanDiagnosticText);
        Assert.Contains("connecting", vm.LanDiagnosticText);
        Assert.DoesNotContain(secret, vm.LanDiagnosticText);
        vm.ApplyDeviceSyncState(new DeviceSyncState(true, 0, [], Phase: "failed", Error: "device_sync_failed", FailureStage: "applying"));
        Assert.Contains("连接已建立", vm.LanErrorText);
        main.Close();
    }

    [Fact]
    public void Pairing_link_from_a_QR_code_fills_the_code_and_replaces_the_old_device()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var oldPeer = new LanPeerDisplay("Old device", "192.168.1.2", 2000);
        viewModel.LanPeers.Add(oldPeer);
        viewModel.SelectedLanPeer = oldPeer;
        viewModel.LanPairingCode = "old-code";
        var code = new string('A', 43);
        var link = $"monica-lan:2:192.168.1.5:53318:{code}";

        var view = new LanSyncView { DataContext = viewModel };
        view.FindControl<Expander>("LanPairingLinkExpander")!.IsExpanded = true;
        var host = new Window { Width = 700, Height = 900, Content = new ScrollViewer { Content = view } };
        host.Show();
        try
        {
            using var qrData = QRCodeGenerator.GenerateQrCode(link, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(qrData);
            var scanned = TotpQrCodeDecoder.TryDecode(qr.GetGraphic(6));
            Assert.Equal(link, scanned);
            // The image reader and pasted-link field both assign this property.
            viewModel.LanInvitation = scanned!;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(code, viewModel.LanPairingCode);
            Assert.Equal(link, viewModel.LanInvitation);
            Assert.Equal("192.168.1.5", viewModel.SelectedLanPeer?.Address);
            Assert.Equal(53318, viewModel.SelectedLanPeer?.Port);
            Assert.Null(view.FindControl<TextBox>("LanPairingCodeBox"));
            Assert.Equal(link, view.FindControl<TextBox>("LanInvitationBox")!.Text);
            Assert.True(view.FindControl<TextBlock>("LanPairingReadyText")!.IsVisible);
        }
        finally { host.Close(); main.Close(); }
    }

    [Fact]
    public async Task Legacy_invitations_request_an_update_without_database_or_password_prompts()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        services.GetRequiredService<ILocalizationService>().SetLanguage("zh-CN");
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var code = new string('A', 43);
        viewModel.LanInvitation = $"monica-lan:1:192.168.1.5:53318:{code}";

        await viewModel.ConnectLanSyncCommand.ExecuteAsync(null);

        Assert.Equal("请将另一台设备的 Monica 更新后，使用“附近设备同步”生成的配对二维码或链接。", viewModel.LanErrorText);
        Assert.Equal(code, viewModel.LanPairingCode);
        Assert.True(viewModel.LanPairingAutoFilled);
        Assert.False(viewModel.IsLanBusy);
        main.Close();
    }

    [Fact]
    public void Switching_devices_clears_scanned_fields_and_restores_manual_entry()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.LanInvitation = $"monica-lan:1:192.168.1.5:53318:{new string('A', 43)}";
        viewModel.SelectedLanPeer = new LanPeerDisplay("Other device", "10.0.0.2", 5000);
        Assert.Empty(viewModel.LanInvitation);
        Assert.Empty(viewModel.LanPairingCode);
        Assert.False(viewModel.LanPairingAutoFilled);
        main.Close();
    }

    [Fact]
    public void Invalid_pasted_links_remove_previously_derived_pairing_fields()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.LanInvitation = $"monica-lan:1:192.168.1.5:53318:{new string('A', 43)}";
        viewModel.LanInvitation = "monica-lan:1:192.168.1.5:53318:invalid-code";
        Assert.Empty(viewModel.LanPairingCode);
        Assert.Null(viewModel.SelectedLanPeer);
        Assert.False(viewModel.LanPairingAutoFilled);
        main.Close();
    }

    [Fact]
    public void Nearby_sync_binds_device_actions_frequency_and_progress_without_database_controls()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var localization = services.GetRequiredService<ILocalizationService>();
        Assert.Same(services.GetRequiredService<MdbxVaultStore>(), services.GetRequiredService<IMdbxLanSyncStore>());
        localization.SetLanguage("zh-CN");
        viewModel.OpenSyncWorkspacePageCommand.Execute("Devices");
        Assert.True(viewModel.IsSyncDevicesSelected);
        Assert.False(viewModel.ShowsSyncHealthOverview);
        var pageHost = new SyncPageHostView { DataContext = viewModel };
        Assert.IsType<LanSyncView>(pageHost.FindControl<ContentControl>("SyncPageContent")!.Content);
        var view = new LanSyncView { DataContext = viewModel };
        var title = view.FindControl<TextBlock>("LanSyncTitle")!;
        view.FindControl<Expander>("LanPairingLinkExpander")!.IsExpanded = true;
        var host = new Window { Width = 700, Height = 900, Content = new ScrollViewer { Content = view } };
        host.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("附近设备同步", title.Text);
            Assert.Null(view.FindControl<TextBox>("LanVaultPasswordBox"));
            Assert.True(view.FindControl<ToggleSwitch>("DeviceSyncEnabledToggle")!.IsChecked);
            Assert.Equal(new[] { 0, 60, 300, 900, 1800, -1 }, viewModel.DeviceSyncFrequencies.Select(item => item.Seconds));
            Assert.Same(viewModel.SyncDevicesNowCommand, view.FindControl<Button>("DeviceSyncNowButton")!.Command);
            Assert.Same(viewModel.HostLanSyncCommand, view.FindControl<Button>("LanHostButton")!.Command);
            Assert.Same(viewModel.ConnectLanSyncCommand, view.FindControl<Button>("LanConnectButton")!.Command);
            viewModel.IsLanBusy = true;
            viewModel.LanProgressIndeterminate = false;
            viewModel.LanProgress = 45;
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<Button>("LanConnectButton")!.IsEnabled);
            Assert.True(view.FindControl<Button>("LanCancelButton")!.IsVisible);
            Assert.Equal(45, view.FindControl<ProgressBar>("LanSyncProgress")!.Value);
            localization.SetLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Nearby device sync", title.Text);
        }
        finally { viewModel.IsLanBusy = false; host.Close(); main.Close(); }
    }

    [Fact]
    public void Locking_clears_entered_pairing_credentials_and_the_qr_image()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.IsUnlocked = true;
        viewModel.LanPairingCode = "synthetic-pairing-code";
        viewModel.LanInvitation = "synthetic-invitation";
        viewModel.LanHostCode = "synthetic-host-code";
        viewModel.IsUnlocked = false;
        Assert.Empty(viewModel.LanPairingCode);
        Assert.Empty(viewModel.LanInvitation);
        Assert.Empty(viewModel.LanHostCode);
        Assert.Null(viewModel.LanQrCode);
        main.Close();
    }

    [Fact]
    public void Background_status_updates_reuse_device_rows_and_show_custom_frequency()
    {
        var main = new Monica.App.MainWindow();
        using var services = Monica.App.App.ConfigureServices(main);
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.ApplyDeviceSyncState(new DeviceSyncState(true, 120, [new("phone", "Phone", "offline", null)]));
        var row = Assert.Single(viewModel.SyncedDevices);
        Assert.True(viewModel.IsCustomDeviceSyncInterval);
        Assert.Equal("2", viewModel.DeviceSyncCustomMinutes);
        viewModel.DeviceSyncCustomMinutes = "17";
        viewModel.ApplyDeviceSyncState(new DeviceSyncState(true, 120, [new("phone", "Phone", "online", DateTimeOffset.UtcNow)]));
        Assert.Same(row, Assert.Single(viewModel.SyncedDevices));
        Assert.NotEmpty(row.LastSynced);
        Assert.Equal("17", viewModel.DeviceSyncCustomMinutes);
        main.Close();
    }
}
