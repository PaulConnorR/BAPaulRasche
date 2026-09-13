using BA.Models;
using BA.Resources.Styles;
using BA.Services.Transmission;
using BA.ViewModels;
using CommunityToolkit.Maui.Markup;
using LibVLCSharp.MAUI;
using Microsoft.Maui.Controls.Shapes;

namespace BA.Views;

public class ReceivePage : ContentPage
{
    const double VideoWidth = 480;
    const double VideoHeight = 320;

    public ReceivePage(ReceiveViewModel viewModel)
    {
        BindingContext = viewModel;
        Title = viewModel.Title;

        // LibVLC spielt das UDP direkt, latenzarm (ohne HLS-Umweg).
        var media = new VideoView
        {
            WidthRequest = VideoWidth,
            HeightRequest = VideoHeight,
            BackgroundColor = AppColors.Gray950,
            MediaPlayer = viewModel.MediaPlayer,
        };

        var protocolPicker = new Picker
        {
            Title = "Protokoll wählen",
            WidthRequest = 240,
            ItemDisplayBinding = new Binding(nameof(VideoTransmitter.Name)),
        };
        protocolPicker.ItemsSource = (System.Collections.IList)viewModel.Protocols;
        protocolPicker.Bind(
            Picker.SelectedItemProperty,
            static (ReceiveViewModel vm) => vm.SelectedProtocol,
            static (ReceiveViewModel vm, VideoTransmitter? value) => vm.SelectedProtocol = value);

        
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20),
                Spacing = 20,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    //new Label()
                    //    .Text("Empfangen")
                    //    .Style(AppStyles.Headline),

                    // Empfangenes (dekodiertes) Video.
                    //media,

                    new HorizontalStackLayout
                    {
                        Spacing = 10,
                        HorizontalOptions = LayoutOptions.Center,
                        Children =
                        {
                            new ActivityIndicator()
                                .Bind(ActivityIndicator.IsRunningProperty, static (ReceiveViewModel vm) => vm.IsReceiving)
                                .CenterVertical(),
                            new Label()
                                .Text("Warte auf Stream …")
                                .CenterVertical(),
                        }
                    }.Bind(VisualElement.IsVisibleProperty, static (ReceiveViewModel vm) => vm.IsReceiving),

                    // Video-Infofelder
                    //BuildInfoPanel(viewModel.ReceivedInfo),
                    new ScrollView
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Always,
                        Content = new HorizontalStackLayout 
                        {
                            HorizontalOptions = LayoutOptions.Center,
                            Children =
                            {
                                new Border
                                {
                                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                                    StrokeThickness = 1,
                                    Padding = new Thickness(16),
                                    HorizontalOptions = LayoutOptions.Center,
                                    Content = new VerticalStackLayout
                                    {
                                        Spacing = 12,
                                        Children =
                                        {
                                            new Label().Text("Verbindungseigenschaften").Font(bold: true),
                                            LabeledControl("Protokoll", protocolPicker),

                                            new HorizontalStackLayout
                                            {
                                                Spacing = 8,
                                                Children =
                                                {
                                                    new Label().Text("IP-Adresse:").Font(bold: true).CenterVertical(),
                                                    new Label()
                                                        .Bind(Label.TextProperty, static (ReceiveViewModel vm) => vm.LocalIpAddress)
                                                        .CenterVertical(),
                                                    new Button()
                                                        .Text("Aktualisieren")
                                                        .BindCommand(static (ReceiveViewModel vm) => vm.RefreshConnectionInfoCommand),
                                                }
                                            },

                                            new HorizontalStackLayout
                                            {
                                                Spacing = 8,
                                                Children =
                                                {
                                                    new Label().Text("Port:").Font(bold: true).CenterVertical(),
                                                    new Entry { WidthRequest = 120, Keyboard = Keyboard.Numeric }
                                                        .Bind(Entry.TextProperty,
                                                            static (ReceiveViewModel vm) => vm.ListenPortText,
                                                            static (ReceiveViewModel vm, string value) => vm.ListenPortText = value),
                                                }
                                            },

                                            LabeledControl("Sender-IP (Rückkanal, leer = aus)",
                                                new Entry { WidthRequest = 240 }
                                                    .Bind(Entry.TextProperty,
                                                        static (ReceiveViewModel vm) => vm.ReturnHostText,
                                                        static (ReceiveViewModel vm, string value) => vm.ReturnHostText = value)),

                                            InfoRow("Endpunkt",
                                                new Label().Bind(Label.TextProperty, static (ReceiveViewModel vm) => vm.ListenEndpointDisplay)),
                                            InfoRow("Status",
                                                new Label().Bind(Label.TextProperty, static (ReceiveViewModel vm) => vm.ConnectionStatus)),

                                            new Button()
                                                .Bind(Button.TextProperty, static (ReceiveViewModel vm) => vm.ReceiveButtonText)
                                                .BindCommand(static (ReceiveViewModel vm) => vm.ToggleReceivingCommand)
                                                .FillHorizontal(),

                                            new Button()
                                                .Bind(Button.TextProperty, static (ReceiveViewModel vm) => vm.HarnessButtonText)
                                                .BindCommand(static (ReceiveViewModel vm) => vm.ToggleHarnessCommand)
                                                .FillHorizontal(),

                                            // Im Harness-Modus automatisch aus Quellordner + signalisiertem Dateinamen.
                                            new Button()
                                                .Text("Referenz-Original wählen (Qualität)")
                                                .BindCommand(static (ReceiveViewModel vm) => vm.PickReferenceCommand)
                                                .FillHorizontal(),
                                            new Label()
                                                .Bind(Label.TextProperty, static (ReceiveViewModel vm) => vm.ReferencePath)
                                                .FontSize(11),
                                        }
                                    }
                                },

                                LatencyControls.Build(),

                                MetricsPanel.Build(),
                            }
                        },
                    },
                }
            
        };
    }

    static View BuildInfoPanel(VideoInfo info) => new Border
    {
        StrokeShape = new RoundRectangle { CornerRadius = 8 },
        StrokeThickness = 1,
        Padding = new Thickness(12),
        HorizontalOptions = LayoutOptions.Center,
        Content = new VerticalStackLayout
        {
            Spacing = 4,
            BindingContext = info,
            Children =
            {
                new Label().Text("Empfangenes Video").Font(bold: true),
                InfoRow("Auflösung", new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.ResolutionDisplay)),
                InfoRow("Größe", new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.FileSizeDisplay)),
                InfoRow("Dauer", new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.DurationDisplay)),
            }
        }
    };

    static HorizontalStackLayout InfoRow(string caption, Label value) => new()
    {
        Spacing = 6,
        Children =
        {
            new Label().Text($"{caption}:").Font(bold: true),
            value,
        }
    };

    static VerticalStackLayout LabeledControl(string caption, View control) => new()
    {
        Spacing = 4,
        Children =
        {
            new Label().Text(caption).Font(bold: true),
            control,
        }
    };
}
