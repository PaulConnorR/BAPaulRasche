using CommunityToolkit.Mvvm.Input;

namespace BA.ViewModels;

public partial class SecondViewModel : BaseViewModel
{
    public SecondViewModel()
    {
        Title = "Zweite Seite";
    }

    [RelayCommand]
    private static async Task GoBack()
        => await Shell.Current.GoToAsync("..");
}
