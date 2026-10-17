using System.Windows;
using System.Windows.Media;

namespace CameraGuide
{
    public partial class App : Application
    {
        public App()
        {
            // crisp text on every screen, including touch monitors with Windows scaling
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
            {
                var w = s as Window;
                if (w == null) return;
                w.UseLayoutRounding = true;
                w.SnapsToDevicePixels = true;
                TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
                RenderOptions.SetBitmapScalingMode(w, BitmapScalingMode.HighQuality);
            }));
        }
    }
}
