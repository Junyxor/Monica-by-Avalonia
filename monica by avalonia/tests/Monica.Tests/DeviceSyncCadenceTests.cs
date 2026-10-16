using Monica.App.Services;

namespace Monica.Tests;

public sealed class DeviceSyncCadenceTests
{
    [Theory]
    [InlineData(0, 1, true, false)]
    [InlineData(0, 2, true, true)]
    [InlineData(300, 299, true, false)]
    [InlineData(300, 300, true, true)]
    [InlineData(0, 100, false, false)]
    [InlineData(300, 1000, false, false)]
    public void Frequency_waits_for_its_deadline_and_does_not_resend_unchanged_passwords(int interval, int elapsed, bool changed, bool expected)
    {
        Assert.Equal(expected, DeviceSyncCadence.IsDue(interval, TimeSpan.FromSeconds(elapsed), changed));
    }

    [Fact]
    public void Reconnect_can_catch_up_without_waiting_for_the_next_interval()
    {
        Assert.True(DeviceSyncCadence.IsDue(1800, TimeSpan.Zero, false, force: true));
    }

    [Fact]
    public void A_frequency_change_propagates_in_both_directions_without_an_old_setting_winning()
    {
        var first = new DeviceSyncPreferences { DeviceId = "desktop" };
        var second = new DeviceSyncPreferences { DeviceId = "phone" };
        first.SetInterval(300);
        Assert.True(second.AcceptInterval(first.IntervalSeconds, first.SettingsVersion, first.SettingsWriter));
        second.SetInterval(0);
        Assert.True(first.AcceptInterval(second.IntervalSeconds, second.SettingsVersion, second.SettingsWriter));
        Assert.Equal(0, first.IntervalSeconds);
        Assert.False(first.AcceptInterval(300, 1, "desktop"));
    }
}
