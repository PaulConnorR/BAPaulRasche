using CommunityToolkit.Maui.Markup;

namespace BA.Resources.Styles;

public static class AppStyles
{
    public static readonly Style<Page> PageStyle = new Style<Page>()
        .Add(Page.PaddingProperty, new Thickness(0))
        .AddAppThemeBinding(Page.BackgroundColorProperty, AppColors.White, AppColors.OffBlack);

    public static readonly Style<Label> LabelStyle = new Style<Label>()
        .AddAppThemeBinding(Label.TextColorProperty, AppColors.Black, AppColors.White)
        .Add(Label.BackgroundColorProperty, Colors.Transparent)
        .Add(Label.FontFamilyProperty, "OpenSansRegular")
        .Add(Label.FontSizeProperty, 14d);

    public static readonly Style<Button> ButtonStyle = new Style<Button>()
        .AddAppThemeBinding(Button.TextColorProperty, AppColors.White, AppColors.PrimaryDarkText)
        .AddAppThemeBinding(Button.BackgroundColorProperty, AppColors.Primary, AppColors.PrimaryDark)
        .Add(Button.FontFamilyProperty, "OpenSansRegular")
        .Add(Button.FontSizeProperty, 14d)
        .Add(Button.BorderWidthProperty, 0d)
        .Add(Button.CornerRadiusProperty, 8)
        .Add(Button.PaddingProperty, new Thickness(14, 10))
        .Add(Button.MinimumHeightRequestProperty, 44d)
        .Add(Button.MinimumWidthRequestProperty, 44d);

    public static readonly Style<Label> Headline = new Style<Label>()
        .AddAppThemeBinding(Label.TextColorProperty, AppColors.MidnightBlue, AppColors.White)
        .Add(Label.FontSizeProperty, 32d)
        .Add(View.HorizontalOptionsProperty, LayoutOptions.Center)
        .Add(Label.HorizontalTextAlignmentProperty, TextAlignment.Center);

    public static readonly Style<Label> SubHeadline = new Style<Label>()
        .AddAppThemeBinding(Label.TextColorProperty, AppColors.MidnightBlue, AppColors.White)
        .Add(Label.FontSizeProperty, 24d)
        .Add(View.HorizontalOptionsProperty, LayoutOptions.Center)
        .Add(Label.HorizontalTextAlignmentProperty, TextAlignment.Center);
}
