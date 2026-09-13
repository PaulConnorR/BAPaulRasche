using BA.Models;
using BA.Resources.Styles;
using BA.Services;
using BA.Services.Compression;
using BA.Services.Transmission;
using BA.ViewModels;
using CommunityToolkit.Maui.Markup;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;

namespace BA.Views;

public class MainPage : ContentPage
{
    const double VideoWidth = 380;
    const double VideoHeight = 260;

    public MainPage(MainViewModel viewModel)
    {
        BindingContext = viewModel;
        Title = viewModel.Title;

        // links = Quelle, rechts = Ergebnis
        var leftMedia = BuildMediaElement();
        leftMedia.Bind(MediaElement.SourceProperty, static (MainViewModel vm) => vm.LeftSource);
        leftMedia.MediaOpened += (_, _) => viewModel.UpdateMediaMetadata(
            viewModel.LeftInfo, leftMedia.MediaWidth, leftMedia.MediaHeight, leftMedia.Duration);

        var rightMedia = BuildMediaElement();
        rightMedia.Bind(MediaElement.SourceProperty, static (MainViewModel vm) => vm.RightSource);
        rightMedia.MediaOpened += (_, _) => viewModel.UpdateMediaMetadata(
            viewModel.RightInfo, rightMedia.MediaWidth, rightMedia.MediaHeight, rightMedia.Duration);

        var compressionPicker = new Picker
        {
            Title = "Verfahren wählen",
            WidthRequest = 240,
            ItemDisplayBinding = new Binding(nameof(VideoCompressor.Name)),
        };
        compressionPicker.ItemsSource = (System.Collections.IList)viewModel.CompressionMethods;
        compressionPicker.Bind(
            Picker.SelectedItemProperty,
            static (MainViewModel vm) => vm.SelectedCompression,
            static (MainViewModel vm, VideoCompressor? value) => vm.SelectedCompression = value);

        var protocolPicker = new Picker
        {
            Title = "Protokoll wählen",
            WidthRequest = 240,
            ItemDisplayBinding = new Binding(nameof(VideoTransmitter.Name)),
        };
        protocolPicker.ItemsSource = (System.Collections.IList)viewModel.TransmissionProtocols;
        protocolPicker.Bind(
            Picker.SelectedItemProperty,
            static (MainViewModel vm) => vm.SelectedProtocol,
            static (MainViewModel vm, VideoTransmitter? value) => vm.SelectedProtocol = value);

        var cameraPicker = new Picker { Title = "Kamera wählen", WidthRequest = 240 };
        cameraPicker.Bind(Picker.ItemsSourceProperty, static (MainViewModel vm) => vm.Cameras);
        cameraPicker.Bind(
            Picker.SelectedItemProperty,
            static (MainViewModel vm) => vm.SelectedCamera,
            static (MainViewModel vm, string? value) => vm.SelectedCamera = value);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(5),
                Spacing = 20,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    new Button()
                        .Text("Video laden (Explorer öffnen)")
                        .BindCommand(static (MainViewModel vm) => vm.PickVideoCommand)
                        .CenterHorizontal(),

                    // Fortschrittsanzeige, nur bei IsBusy sichtbar
                    new HorizontalStackLayout
                    {
                        Spacing = 10,
                        HorizontalOptions = LayoutOptions.Center,
                        Children =
                        {
                            new ActivityIndicator()
                                .Bind(ActivityIndicator.IsRunningProperty, static (MainViewModel vm) => vm.IsBusy)
                                .CenterVertical(),
                            new Label()
                                .Bind(Label.TextProperty, static (MainViewModel vm) => vm.BusyText)
                                .CenterVertical(),
                        }
                    }.Bind(VisualElement.IsVisibleProperty, static (MainViewModel vm) => vm.IsBusy),
                                        
                    new HorizontalStackLayout
                    {
                        Spacing = 24,
                        HorizontalOptions = LayoutOptions.Center,
                        Children =
                        {
                            BuildVideoColumn("Original (Quelle)", leftMedia,
                                BuildInfoPanel(viewModel.LeftInfo, showProcessing: false),
                                "Original komprimieren", "left"),

                            BuildVideoColumn("Ergebnis", rightMedia,
                                BuildInfoPanel(viewModel.RightInfo, showProcessing: true)),
                        }
                    },

                    new HorizontalStackLayout {
                        HorizontalOptions = LayoutOptions.Center,
                        Children =
                        {
                            /*new Border
                            {
                                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                                StrokeThickness = 1,
                                Padding = new Thickness(16),
                                HorizontalOptions = LayoutOptions.Center,
                                Content = new VerticalStackLayout
                                {
                                    Spacing = 10,
                                    HorizontalOptions = LayoutOptions.Center,
                                    Children =
                                    {
                                        new Label().Text("Kamera (Live-Quelle)").Font(bold: true),
                                        new Label()
                                            .Text("Keine lokale Vorschau – Kamerabild über die Empfangsseite prüfen.")
                                            .FontSize(12),
                                        new HorizontalStackLayout
                                        {
                                            Spacing = 10,
                                            Children =
                                            {
                                                cameraPicker,
                                                new Button()
                                                    .Text("Kameras suchen")
                                                    .BindCommand(static (MainViewModel vm) => vm.LoadCamerasCommand),
                                            }
                                        },
                                        new Button()
                                            .Bind(Button.TextProperty, static (MainViewModel vm) => vm.CameraStreamButtonText)
                                            .BindCommand(static (MainViewModel vm) => vm.ToggleCameraStreamCommand)
                                            .FillHorizontal(),
                                    }
                                }
                            },*/

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
                                        LabeledControl("Kompressionsverfahren", compressionPicker),
                                        LabeledControl("Übertragungsprotokoll", protocolPicker),
                                        LabeledControl("Ziel-IP",
                                            new Entry { WidthRequest = 240 }
                                                .Bind(Entry.TextProperty,
                                                    static (MainViewModel vm) => vm.TargetHost,
                                                    static (MainViewModel vm, string value) => vm.TargetHost = value)),
                                        LabeledControl("Ziel-Port",
                                            new Entry { WidthRequest = 240, Keyboard = Keyboard.Numeric }
                                                .Bind(Entry.TextProperty,
                                                    static (MainViewModel vm) => vm.TargetPortText,
                                                    static (MainViewModel vm, string value) => vm.TargetPortText = value)),
                                        LabeledControl("WLAN-Band",
                                            new Picker { WidthRequest = 240 }
                                                .Bind(Picker.ItemsSourceProperty,
                                                    static (MainViewModel vm) => vm.WlanBands)
                                                .Bind(Picker.SelectedItemProperty,
                                                    static (MainViewModel vm) => vm.WlanBandText,
                                                    static (MainViewModel vm, string value) => vm.WlanBandText = value)),
                                        // clumsy wird manuell gestartet (WinDivert braucht Adminrechte).
                                        LabeledControl("clumsy-Preset (Netzbedingung)",
                                            new Picker { WidthRequest = 240 }
                                                .Bind(Picker.ItemsSourceProperty,
                                                    static (MainViewModel vm) => vm.ClumsyPresets)
                                                .Bind(Picker.SelectedItemProperty,
                                                    static (MainViewModel vm) => vm.SelectedClumsyPreset,
                                                    static (MainViewModel vm, object value) => vm.SelectedClumsyPreset = value as ClumsyPreset)),
                                        new Label { LineBreakMode = LineBreakMode.WordWrap, WidthRequest = 240 }
                                            .Font(size: 12)
                                            .Bind(Label.TextProperty, static (MainViewModel vm) => vm.ClumsyCommand),
                                        new Button { WidthRequest = 240 }
                                            .Text("clumsy-Befehl kopieren")
                                            .BindCommand(static (MainViewModel vm) => vm.CopyClumsyCommandCommand),

                                        new HorizontalStackLayout
                                        {
                                            Spacing = 8,
                                            Children =
                                            {
                                                new Switch()
                                                    .Bind(Switch.IsToggledProperty,
                                                        static (MainViewModel vm) => vm.OnePcTest,
                                                        static (MainViewModel vm, bool v) => vm.OnePcTest = v)
                                                    .CenterVertical(),
                                                new VerticalStackLayout
                                                {
                                                    Children =
                                                    {
                                                        new Label().Text("Ein-PC-Test (Loopback 127.0.0.1)").Font(bold: true),
                                                        new Label()
                                                            .Text($"Sendet an 127.0.0.1 (Ziel-IP wird ignoriert). \n „Empfang starten“ --> hier senden")
                                                            .FontSize(11),
                                                    }
                                                }.CenterVertical(),
                                            }
                                        },

                                        new HorizontalStackLayout
                                        {
                                            Spacing = 8,
                                            Children =
                                            {
                                                new Switch { HorizontalOptions = LayoutOptions.Start }
                                                    .Bind(Switch.IsToggledProperty,
                                                        static (MainViewModel vm) => vm.HarnessMode,
                                                        static (MainViewModel vm, bool v) => vm.HarnessMode = v)
                                                    .CenterVertical(),
                                                new VerticalStackLayout
                                                {
                                                    Children =
                                                    {
                                                        new Label().Text("Harness-Server (Empfänger fernsteuern)").Font(bold: true),
                                                        new Label()
                                                            .Text("Rückkanal bleibt offen; teilt dem Empfänger vor jedem Lauf Protokoll/Port/Quelle mit.")
                                                            .FontSize(11),
                                                    }
                                                }.CenterVertical(),
                                            }
                                        },

                                        // Einstufig, live encodiert: NUR dieser Knopf misst Latenz/Qualität.
                                        new Label().Text("→ Für Messungen: einstufig live übertragen").Font(bold: true).FontSize(11),
                                        new Button()
                                            .Bind(Button.TextProperty, static (MainViewModel vm) => vm.FileStreamButtonText)
                                            .BindCommand(static (MainViewModel vm) => vm.ToggleFileStreamCommand)
                                            .FillHorizontal(),
                                        // Zweistufig (-c copy): sendet einmal ohne Neucodierung, keine Messung.
                                        new Button()
                                            .Text("Zweistufig senden (-c copy, keine Messung)")
                                            .BindCommand(static (MainViewModel vm) => vm.StartTransmissionCommand)
                                            .FillHorizontal(),
                                    }
                                }
                            },

                            LatencyControls.Build(),

                        }
                    },
                    new HorizontalStackLayout
                    {
                        HorizontalOptions = LayoutOptions.Center,
                        Children =
                        {

                            BuildHarnessPanel(),

                            MetricsPanel.Build(),
                        }
                    },
                }
            }
        };
    }

    static View BuildHarnessPanel() => new Border
    {
        StrokeShape = new RoundRectangle { CornerRadius = 8 },
        StrokeThickness = 1,
        Padding = new Thickness(16),
        HorizontalOptions = LayoutOptions.Center,
        Content = new VerticalStackLayout
        {
            Spacing = 10,
            WidthRequest = 320,
            Children =
            {
                new Label().Text("Auto-Messreihe (Harness)").Font(bold: true),
                new Label()
                    .Text("Fährt Codec × Protokoll × GOP × Ratenmodus × Messmodus ab (feste Quelle, endliche Sequenz). Alle Protokolle inkl. WebRTC und alle Codecs; Netzbedingung fest.")
                    .FontSize(11),

                LabeledControl("GOP-Werte (Komma)",
                    new Entry { WidthRequest = 240 }
                        .Bind(Entry.TextProperty,
                            static (MainViewModel vm) => vm.HarnessGopList,
                            static (MainViewModel vm, string v) => vm.HarnessGopList = v)),
                LabeledControl("Wiederholungen",
                    new Entry { WidthRequest = 240, Keyboard = Keyboard.Numeric }
                        .Bind(Entry.TextProperty,
                            static (MainViewModel vm) => vm.HarnessRepetitionsText,
                            static (MainViewModel vm, string v) => vm.HarnessRepetitionsText = v)),
                LabeledControl("Aufwärmzeit (s, verworfen)",
                    new Entry { WidthRequest = 240, Keyboard = Keyboard.Numeric }
                        .Bind(Entry.TextProperty,
                            static (MainViewModel vm) => vm.HarnessWarmupText,
                            static (MainViewModel vm, string v) => vm.HarnessWarmupText = v)),

                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        new Switch { HorizontalOptions = LayoutOptions.Start }
                            .Bind(Switch.IsToggledProperty,
                                static (MainViewModel vm) => vm.HarnessUseCrf,
                                static (MainViewModel vm, bool v) => vm.HarnessUseCrf = v)
                            .CenterVertical(),
                        new Label().Text("CRF-Läufe").CenterVertical(),
                        new Switch { HorizontalOptions = LayoutOptions.Start }
                            .Bind(Switch.IsToggledProperty,
                                static (MainViewModel vm) => vm.HarnessUseCbr,
                                static (MainViewModel vm, bool v) => vm.HarnessUseCbr = v)
                            .CenterVertical(),
                        new Label().Text("CBR-Läufe").CenterVertical(),
                    }
                },

                new Label()
                    .Text("Messmodus + Messdauer + CBR-Bitrate kommen aus den Latenz-Einstellungen (Latenz/Qualität-Schalter aktivieren die jeweiligen Sub-Läufe).")
                    .FontSize(11),

                new Button()
                    .Bind(Button.TextProperty, static (MainViewModel vm) => vm.HarnessRunButtonText)
                    .BindCommand(static (MainViewModel vm) => vm.RunAutoMeasurementCommand)
                    .FillHorizontal(),
                new Label()
                    .Bind(Label.TextProperty, static (MainViewModel vm) => vm.HarnessProgress)
                    .FontSize(11),
            }
        }
    };

    static MediaElement BuildMediaElement() => new()
    {
        WidthRequest = VideoWidth,
        HeightRequest = VideoHeight,
        // Explizit zentrieren: sonst setzt der geerbte Fill-Default das Element bei breiterer Spalte linksbündig.
        HorizontalOptions = LayoutOptions.Center,
        ShouldAutoPlay = false,
        ShouldShowPlaybackControls = true,
        ShouldLoopPlayback = true,
        Aspect = Aspect.AspectFit,
        BackgroundColor = AppColors.Gray950,
    };

    static View BuildVideoColumn(string caption, MediaElement media, View infoPanel, string? compressLabel = null, string? side = null) {
        var container = new VerticalStackLayout
        {
            Spacing = 10,
            WidthRequest = VideoWidth,
            Children =
            {
                new Label()
                    .Text(caption)
                    .Font(bold: true)
                    .CenterHorizontal(),
                media,
                infoPanel,
            }
        };
        if (compressLabel != null) container.Add(new Button()
                    .Text(compressLabel)
                    .Invoke(b => b.CommandParameter = side)
                    .BindCommand(static (MainViewModel vm) => vm.CompressCommand)
                    .FillHorizontal());
        return container;
    }

    static View BuildInfoPanel(VideoInfo info, bool showProcessing)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 4,
            BindingContext = info,
            Children =
            {
                new Label()
                    .Bind(Label.TextProperty, static (VideoInfo v) => v.FileName)
                    .Font(bold: true),
                InfoRow("Auflösung", new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.ResolutionDisplay)),
                InfoRow("Größe", new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.FileSizeDisplay)),
                InfoRow("Dauer", new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.DurationDisplay)),
            }
        };

        if (showProcessing)
        {
            stack.Children.Add(
                InfoRow("Verarbeitungszeit",
                    new Label().Bind(Label.TextProperty, static (VideoInfo v) => v.ProcessingTimeDisplay)));
        }

        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            StrokeThickness = 1,
            Padding = new Thickness(12),
            Content = stack,
        };
    }

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
        Spacing = 2,
        Children =
        {
            new Label().Text(caption).Font(bold: true),
            control,
        }
    };
}
