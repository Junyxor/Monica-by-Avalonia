using CommunityToolkit.Mvvm.ComponentModel;

namespace Monica.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool _isDeletingPasswords;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordDeleteProgressText))]
    private int _passwordDeleteProcessed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordDeleteProgressText))]
    private int _passwordDeleteTotal;

    public string PasswordDeleteProgressText =>
        _localization.Format("PasswordDeleteProgressFormat", PasswordDeleteProcessed, PasswordDeleteTotal);
}
