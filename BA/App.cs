using BA.Resources.Styles;

namespace BA;

public class App : Application
{
    public App()
    {
        Resources.Add(AppStyles.PageStyle.MauiStyle);
        Resources.Add(AppStyles.LabelStyle.MauiStyle);
        Resources.Add(AppStyles.ButtonStyle.MauiStyle);
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new AppShell());
}
