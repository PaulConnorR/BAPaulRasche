using BA.Views;

namespace BA;

public class AppShell : Shell
{
    public AppShell()
    {
        Title = "BA";

        // Dauerhaft sichtbare linke Navigationsspalte (statt Hamburger-Menü).
        FlyoutBehavior = FlyoutBehavior.Locked;
        FlyoutWidth = 200;

        Items.Add(new ShellContent
        {
            Title = "Senden",
            Route = "MainPage",
            ContentTemplate = new DataTemplate(typeof(MainPage)),
        });

        Items.Add(new ShellContent
        {
            Title = "Empfangen",
            Route = "ReceivePage",
            ContentTemplate = new DataTemplate(typeof(ReceivePage)),
        });
    }
}
