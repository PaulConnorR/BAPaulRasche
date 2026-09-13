using BA.Services;
using CommunityToolkit.Maui.Markup;
using Microsoft.Maui.Controls.Shapes;

namespace BA.Views;

/// <summary>
/// Wiederverwendbares Panel mit Slidern für die Latenz-Hebel (an <see cref="PipelineSettings.Current"/>  gebunden). 
/// Wird auf Sende- und Empfangsseite eingebunden. Werte wirken ab dem nächsten Start.
/// </summary>
public static class LatencyControls
{
    public static View Build()
    {
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            StrokeThickness = 1,
            Padding = new Thickness(16),
            HorizontalOptions = LayoutOptions.Center,
            BindingContext = PipelineSettings.Current,
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label().Text("Latenz-Hebel (wirken ab nächstem Start)").Font(bold: true),

                    Row("VLC-Caching",
                        new Label().Bind(Label.TextProperty, static (PipelineSettings s) => s.VlcCachingDisplay),
                        new Slider { Minimum = 0, Maximum = 1000 }
                            .Bind(Slider.ValueProperty,
                                static (PipelineSettings s) => s.VlcCachingMs,
                                static (PipelineSettings s, double v) => s.VlcCachingMs = v)),

                    Row("SRT-Latenz",
                        new Label().Bind(Label.TextProperty, static (PipelineSettings s) => s.SrtLatencyDisplay),
                        new Slider { Minimum = 0, Maximum = 500 }
                            .Bind(Slider.ValueProperty,
                                static (PipelineSettings s) => s.SrtLatencyMs,
                                static (PipelineSettings s, double v) => s.SrtLatencyMs = v)),

                    Row("GOP (Keyframe-Intervall)",
                        new Label().Bind(Label.TextProperty, static (PipelineSettings s) => s.GopDisplay),
                        new Slider { Minimum = 1, Maximum = 120 }
                            .Bind(Slider.ValueProperty,
                                static (PipelineSettings s) => s.GopSize,
                                static (PipelineSettings s, double v) => s.GopSize = v)),

                    new HorizontalStackLayout
                    {
                        HorizontalOptions = LayoutOptions.Start,
                        Spacing = 8,
                        Children =
                        {
                            new Switch { HorizontalOptions = LayoutOptions.Start }
                                .Bind(Switch.IsToggledProperty,
                                    static (PipelineSettings s) => s.CbrEnabled,
                                    static (PipelineSettings s, bool v) => s.CbrEnabled = v)
                                .CenterVertical(),
                            new VerticalStackLayout
                            {
                                Children =
                                {
                                    new Label().Text("Bitraten-Modus: CBR statt CRF").Font(bold: true),
                                    new Label()
                                        .Bind(Label.TextProperty, static (PipelineSettings s) => s.RateControlDisplay)
                                        .FontSize(11),
                                }
                            }.CenterVertical(),
                        }
                    },

                    Row("Zielbitrate (CBR)",
                        new Label().Bind(Label.TextProperty, static (PipelineSettings s) => s.TargetBitrateDisplay),
                        new Slider { Minimum = 500, Maximum = 20000 }
                            .Bind(Slider.ValueProperty,
                                static (PipelineSettings s) => s.TargetBitrateKbps,
                                static (PipelineSettings s, double v) => s.TargetBitrateKbps = v)),

                    Row("Qualität (CRF)",
                        new Label().Bind(Label.TextProperty, static (PipelineSettings s) => s.CrfDisplay),
                        new Slider { Minimum = 0, Maximum = 51 }
                            .Bind(Slider.ValueProperty,
                                static (PipelineSettings s) => s.CrfValue,
                                static (PipelineSettings s, double v) => s.CrfValue = v)),

                    new HorizontalStackLayout
                    {
                        HorizontalOptions = LayoutOptions.Start,
                        Spacing = 8,
                        Children =
                        {
                            // Switch-Mindestbreite global im SwitchHandler-Mapping (MauiProgram) auf 0 gesetzt,
                            // sonst reserviert die WinUI-ToggleSwitch Haufen Platz und schiebt den Text weg.
                            new Switch { HorizontalOptions = LayoutOptions.Start }
                                .Bind(Switch.IsToggledProperty,
                                    static (PipelineSettings s) => s.MeasureLatencyEnabled,
                                    static (PipelineSettings s, bool v) => s.MeasureLatencyEnabled = v)
                                .CenterVertical(),
                            new VerticalStackLayout
                            {
                                Children =
                                {
                                    new Label().Text("Latenz messen: Encode+Transport+Decode").Font(bold: true),
                                    new Label()
                                        .Text("Empfang dekodiert dann OHNE Bild. Ab nächstem Start.")
                                        .FontSize(11),
                                }
                            }.CenterVertical(),
                        }
                    },

                    new HorizontalStackLayout
                    {
                        HorizontalOptions = LayoutOptions.Start,
                        Spacing = 8,
                        Children =
                        {
                            new Switch { HorizontalOptions = LayoutOptions.Start }
                                .Bind(Switch.IsToggledProperty,
                                    static (PipelineSettings s) => s.FiniteMeasurement,
                                    static (PipelineSettings s, bool v) => s.FiniteMeasurement = v)
                                .CenterVertical(),
                            new VerticalStackLayout
                            {
                                Children =
                                {
                                    new Label().Text("Endliche Messsequenz (statt Endlosschleife)").Font(bold: true),
                                    new Label()
                                        .Bind(Label.TextProperty, static (PipelineSettings s) => s.FiniteMeasurementDisplay)
                                        .FontSize(11),
                                }
                            }.CenterVertical(),
                        }
                    },

                    Row("Messdauer",
                        new Label().Bind(Label.TextProperty, static (PipelineSettings s) => s.MeasureDurationDisplay),
                        new Slider { Minimum = 2, Maximum = 120 }
                            .Bind(Slider.ValueProperty,
                                static (PipelineSettings s) => s.MeasureDurationSeconds,
                                static (PipelineSettings s, double v) => s.MeasureDurationSeconds = v)),

                    new HorizontalStackLayout
                    {
                        HorizontalOptions = LayoutOptions.Start,
                        Spacing = 8,
                        Children =
                        {
                            new Switch { HorizontalOptions = LayoutOptions.Start }
                                .Bind(Switch.IsToggledProperty,
                                    static (PipelineSettings s) => s.QualityMeasurementEnabled,
                                    static (PipelineSettings s, bool v) => s.QualityMeasurementEnabled = v)
                                .CenterVertical(),
                            new VerticalStackLayout
                            {
                                Children =
                                {
                                    new Label().Text("Empfangs-Qualität messen (SSIM/PSNR/VMAF)").Font(bold: true),
                                    new Label()
                                        .Text("Empfänger schneidet mit, misst offline gegen das Original. Am besten mit endlicher Sequenz.")
                                        .FontSize(11),
                                }
                            }.CenterVertical(),
                        }
                    },

                    new VerticalStackLayout
                    {
                        Spacing = 2,
                        WidthRequest = 300,
                        HorizontalOptions = LayoutOptions.Start,
                        Children =
                        {
                            new Label().Text("Quellordner (Originale, für Referenz):").Font(bold: true),
                            new Entry { Placeholder = @"z.B. C:\BA\Quellen (Harness: nur Dateiname signalisiert)" }
                                .Bind(Entry.TextProperty,
                                    static (PipelineSettings s) => s.SourceFolder,
                                    static (PipelineSettings s, string v) => s.SourceFolder = v),
                        }
                    },
                }
            }
        };
    }

    static View Row(string caption, Label valueLabel, Slider slider) => new VerticalStackLayout
    {
        Spacing = 2,
        WidthRequest = 300,
        HorizontalOptions = LayoutOptions.Start,
        Children =
        {
            new HorizontalStackLayout
            {
                Spacing = 8,
                Children = { new Label().Text($"{caption}:").Font(bold: true), valueLabel },
            },
            slider,
        }
    };
}
