using BA.ViewModels;
using BA.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Markup;
using FFMpegCore;
using LibVLCSharp.MAUI;
using Microsoft.Extensions.Logging;

namespace BA
{
    public static class MauiProgram
    {
        // Ordner mit ffmpeg/ffprobe; leer lassen, wenn ffmpeg im PATH liegt.
        private const string FFmpegBinaryFolder = "";

        public static MauiApp CreateMauiApp()
        {
#if WINDOWS
            // Kindprozesse (ffmpeg) an die App-Lebensdauer koppeln → keine verwaisten ffmpeg-Prozesse,
            // die sonst den Medien-Port blockieren und den nächsten Start scheitern lassen.
            BA.Services.ProcessJob.EnsureKillOnExit();
#endif
            ConfigureFFmpeg();

            LibVLCSharp.Shared.Core.Initialize();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .UseMauiCommunityToolkitMediaElement()
                .UseMauiCommunityToolkitMarkup()
                .UseLibVLCSharp()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif

#if WINDOWS
            // WinUI-ToggleSwitch hat MinWidth 154 (+ On/Off-Content), was den Text wegschiebt; MAUIs WidthRequest
            // überschreibt das NICHT → direkt am nativen Control MinWidth=0 setzen und On/Off-Beschriftung leeren.
            Microsoft.Maui.Handlers.SwitchHandler.Mapper.AppendToMapping("CompactSwitch", (handler, view) =>
            {
                handler.PlatformView.MinWidth = 0;
                handler.PlatformView.OnContent = string.Empty;
                handler.PlatformView.OffContent = string.Empty;
            });
#endif

            builder.Services.AddTransient<MainPage>();
            builder.Services.AddTransient<MainViewModel>();
            builder.Services.AddTransient<ReceivePage>();
            builder.Services.AddTransient<ReceiveViewModel>();
            builder.Services.AddTransient<SecondPage>();
            builder.Services.AddTransient<SecondViewModel>();

            return builder.Build();
        }

        private static void ConfigureFFmpeg()
        {
            if (!string.IsNullOrWhiteSpace(FFmpegBinaryFolder) && Directory.Exists(FFmpegBinaryFolder))
            {
                GlobalFFOptions.Configure(new FFOptions { BinaryFolder = FFmpegBinaryFolder });
            }
        }
    }
}
