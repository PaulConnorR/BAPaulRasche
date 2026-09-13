using BA.Resources.Styles;
using BA.ViewModels;
using CommunityToolkit.Maui.Markup;

namespace BA.Views;

public class SecondPage : ContentPage
{
    public SecondPage(SecondViewModel viewModel)
    {
        BindingContext = viewModel;
        Title = viewModel.Title;

        Content = new VerticalStackLayout
        {
            Padding = new Thickness(30, 0),
            Spacing = 25,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label()
                    .Text("Das ist die zweite Seite.")
                    .Style(AppStyles.Headline),

                new Button()
                    .Text("Zurück")
                    .BindCommand(static (SecondViewModel vm) => vm.GoBackCommand)
                    .FillHorizontal()
            }
        };
    }
}
