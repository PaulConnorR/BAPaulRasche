using BA.Services.Metrics;
using CommunityToolkit.Maui.Markup;
using Microsoft.Maui.Controls.Shapes;

namespace BA.Views;

/// <summary>
/// Wiederverwendbares Auswertungs-Panel: Live-Metriken aus dem ffmpeg-Fortschritt (Sender/Empfänger),
/// ffmpeg-CPU/RAM, letzter SSIM-Wert und CSV-Export. Gebunden an <see cref="MetricsService.Current"/>.
/// </summary>
public static class MetricsPanel
{
    public static View Build()
    {
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            StrokeThickness = 1,
            Padding = new Thickness(16),
            HorizontalOptions = LayoutOptions.Center,
            BindingContext = MetricsService.Current,
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                WidthRequest = 460,
                Children =
                {
                    new Label().Text("Auswertung / Metriken").Font(bold: true),

                    Row("Messlauf", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.RunDisplay)),
                    Row("Rückkanal (Empfänger)", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.RemoteReceiverDisplay)),
                    Row("Uhr-Offset", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.ClockSyncDisplay)),
                    Row("Latenz Enc→Dec", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.LatencyDisplay)),
                    Row("Latenz nur Transport", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.TransportLatencyDisplay)),

                    Row("Sender", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.SenderDisplay)),
                    Row("Empfänger", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.ReceiverDisplay)),
                    Row("Ressourcen", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.ResourceDisplay)),
                    Row("SSIM (Quelle ↔ Ergebnis)", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.SsimDisplay)),
                    Row("Empfangs-Qualität", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.ReceivedQualityDisplay)),

                    new Label().Text("Transport (gemessen, protokoll-übergreifend)").Font(bold: true),
                    Row("Verlust/Jitter/Retransmit", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.TransportDisplay)),
                    Row("Frame-Zustellung", new Label().Bind(Label.TextProperty, static (MetricsService m) => m.FrameDeliveryDisplay)),

                    new HorizontalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            new Button()
                                .Text("CSV exportieren")
                                .BindCommand(static (MetricsService m) => m.ExportCsvCommand),
                            new Button()
                                .Text("Messreihe zurücksetzen")
                                .BindCommand(static (MetricsService m) => m.ResetSamplesCommand),
                        }
                    },

                    new Label()
                        .Bind(Label.TextProperty, static (MetricsService m) => m.ExportStatus)
                        .FontSize(12),
                }
            }
        };
    }

    static HorizontalStackLayout Row(string caption, Label value) => new()
    {
        Spacing = 6,
        Children =
        {
            new Label().Text($"{caption}:").Font(bold: true),
            value,
        }
    };
}
