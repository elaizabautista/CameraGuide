// ============================================================================
// Camera Guide - a small stand-alone tool: live camera picture + the face oval used on the PrecisionSkin scan screen.
// Same look as the scan screen. No AI, no detectors, no database: it only shows the camera so the camera distance
// and height can be set up.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AForge.Video;
using AForge.Video.DirectShow;
using Microsoft.Win32;

namespace CameraGuide
{
    public partial class MainWindow : Window
    {
        // the same oval layout as the PrecisionSkin scan screen (fractions of the square picture)
        private const double CentreX = 0.50, CentreY = 0.50, RadiusX = 0.36, RadiusY = 0.46;
        private const double OvalScale = 1.05;           // the oval is fixed at 105%

        private static readonly string[] StepName = { "Front view", "Left side", "Right side" };
        private static readonly string[] ViewName = { "Front", "Left", "Right" };
        private static readonly string[] StepHelp =
        {
            "Face the camera straight on",
            "Show your LEFT cheek: turn your head toward your right",
            "Show your RIGHT cheek: turn your head toward your left"
        };

        private FilterInfoCollection _devices;
        private VideoCaptureDevice _source;
        private TouchZoom _zoom;
        private int _step;
        private bool _ready;
        private bool _framePending;
        private volatile bool _square = true;
        private int _frames;
        private DateTime _fpsStart = DateTime.UtcNow;
        private double _fps;
        private string _sizeText = "";
        private BitmapSource _lastShown;          // what is on the screen now (for Snap Now)
        private BitmapSource _uploaded;           // an uploaded picture, before cropping
        private DispatcherTimer _fpsTimer;
        private DispatcherTimer _watchdog;          // a camera that sends no pictures is skipped
        private bool _gotFrame, _loadingSizes;
        private int _autoTried;
        private List<VideoCapabilities> _caps = new List<VideoCapabilities>();
        private bool _autoSize = true;                  // true = the sharpest picture the camera can give

        public MainWindow()
        {
            InitializeComponent();

            _zoom = new TouchZoom(CameraViewportGrid, ZoomLayer, 1.0, 4.0);
            _zoom.Changed += UpdateZoomUi;
            UpdateZoomUi();

            LoadCameras();
            BuildStepper();
            _ready = true;
            UpdateOval();

            _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _fpsTimer.Tick += (s, e) => UpdateInfo();
            _fpsTimer.Start();

            Closing += (s, e) => StopCamera();
            if (SystemParameters.PrimaryScreenHeight > SystemParameters.PrimaryScreenWidth) WindowState = WindowState.Maximized;
        }

        private bool HasPicture => UploadedImageViewer.Source != null;

        // ---------------------------------------------------------------- cameras --

        private void LoadCameras()
        {
            CameraCombo.Items.Clear();
            try { _devices = new FilterInfoCollection(FilterCategory.VideoInputDevice); }
            catch { _devices = null; }

            if (_devices == null || _devices.Count == 0)
            {
                CameraCombo.Items.Add("No camera found");
                CameraCombo.SelectedIndex = 0;
                CameraCombo.IsEnabled = false;
                StartCamBtn.IsEnabled = false;
                VerdictTxt.Text = "Status: no camera was found. Connect a camera, or use File Upload.";
                return;
            }

            string remembered = LastCamera();
            int pick = 0;
            for (int i = 0; i < _devices.Count; i++)
            {
                CameraCombo.Items.Add(_devices[i].Name);
                if (remembered != null && _devices[i].Name == remembered) pick = i;                              // the camera that worked last time
                else if (remembered == null && _devices[i].Name.IndexOf("BRIO", StringComparison.OrdinalIgnoreCase) >= 0) pick = i;   // the study camera, when present
            }
            CameraCombo.SelectedIndex = pick;
        }

        private static string LastCameraFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraGuide", "last_camera.txt");

        private static string LastCamera()
        {
            try { return File.Exists(LastCameraFile) ? File.ReadAllText(LastCameraFile).Trim() : null; } catch { return null; }
        }

        private static void RememberCamera(string name)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(LastCameraFile)); File.WriteAllText(LastCameraFile, name ?? ""); } catch { }
        }

        private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_devices == null || _devices.Count == 0 || CameraCombo.SelectedIndex < 0) return;
            _loadingSizes = true;
            try { LoadSizes(); } finally { _loadingSizes = false; }
            if (_source != null) StartCamera();          // switching camera while running restarts it
        }

        /// <summary>The sharpest mode that still runs smoothly: the most pixels among the modes with at least 15 pictures per second (up to 4K).</summary>
        private static VideoCapabilities BestMode(IList<VideoCapabilities> caps)
        {
            if (caps == null || caps.Count == 0) return null;
            Func<VideoCapabilities, long> px = c => (long)c.FrameSize.Width * c.FrameSize.Height;
            var usable = caps.Where(c => px(c) <= 3840L * 2160L).ToList();
            if (usable.Count == 0) usable = caps.ToList();
            var smooth = usable.Where(c => c.AverageFrameRate <= 0 || c.AverageFrameRate >= 15).ToList();
            if (smooth.Count == 0) smooth = usable;
            return smooth.OrderByDescending(px).ThenByDescending(c => c.AverageFrameRate).First();
        }

        private void LoadSizes()
        {
            _caps = new List<VideoCapabilities>();
            SizeCombo.Items.Clear();
            try
            {
                var probe = new VideoCaptureDevice(_devices[CameraCombo.SelectedIndex].MonikerString);
                if (probe.VideoCapabilities != null)
                    _caps = probe.VideoCapabilities.GroupBy(c => c.FrameSize.Width + "x" + c.FrameSize.Height).Select(g => g.OrderByDescending(c => c.AverageFrameRate).First())
                                 .OrderByDescending(c => c.FrameSize.Width * c.FrameSize.Height).ToList();
            }
            catch { }

            VideoCapabilities best = BestMode(_caps);
            SizeCombo.Items.Add(best == null ? "Sharpest available" : "Sharpest available (" + best.FrameSize.Width + " × " + best.FrameSize.Height + ")");
            foreach (VideoCapabilities c in _caps) SizeCombo.Items.Add(c.FrameSize.Width + " × " + c.FrameSize.Height);
            _autoSize = true;
            SizeCombo.SelectedIndex = 0;
        }

        private void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSizes || !_ready) return;
            _autoSize = SizeCombo.SelectedIndex <= 0;
            if (_source != null) StartCamera();
        }

        private void StartCamBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_source != null) { StopCamera(); return; }
            _autoTried = 0;
            StartCamera();
        }

        /// <summary>Some cameras (for example an infrared camera) open but never send a picture: after 4 seconds try the next camera.</summary>
        private void WatchdogTick(object sender, EventArgs e)
        {
            _watchdog.Stop();
            if (_source == null || _gotFrame) return;

            _autoTried++;
            if (_devices != null && _autoTried < _devices.Count)
            {
                int next = (CameraCombo.SelectedIndex + 1) % _devices.Count;
                LiveStatusTxt.Text = CameraCombo.SelectedItem + " sent no picture. Trying " + _devices[next].Name + " ...";
                CameraCombo.SelectedIndex = next;          // restarts the camera on the next device
                return;
            }

            StopCamera();
            VerdictTxt.Text = "Status: no camera sent a picture.";
            RegionTxt.Text = "Close any other program that is using the camera (Teams, Zoom, the Camera app, a browser tab), check the camera privacy switch, then press Live Cam again.";
        }

        private void StartCamera()
        {
            StopCamera();
            if (_devices == null || _devices.Count == 0 || CameraCombo.SelectedIndex < 0) return;

            try
            {
                _uploaded = null;
                _source = new VideoCaptureDevice(_devices[CameraCombo.SelectedIndex].MonikerString);
                if (_caps.Count > 0)
                {
                    VideoCapabilities mode = _autoSize ? BestMode(_caps) : _caps[Math.Max(0, Math.Min(_caps.Count - 1, SizeCombo.SelectedIndex - 1))];
                    _source.VideoResolution = mode;
                }
                _source.NewFrame += OnNewFrame;
                _source.VideoSourceError += (s, e) => Dispatcher.BeginInvoke(new Action(() => LiveStatusTxt.Text = "Camera problem: " + e.Description));
                _frames = 0; _fpsStart = DateTime.UtcNow; _fps = 0; _gotFrame = false;
                _source.Start();

                if (_watchdog == null) { _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; _watchdog.Tick += WatchdogTick; }
                _watchdog.Stop(); _watchdog.Start();

                StartCamBtn.Content = "⏹ Stop Cam";
                SnapBtn.IsEnabled = true;
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                LiveStatusTxt.Text = "Opening " + CameraCombo.SelectedItem + " ...";
                VerdictTxt.Text = "Status: Live camera active. Position your face in the oval.";
                UpdateOval();
            }
            catch (Exception ex)
            {
                _source = null;
                MessageBox.Show("The camera could not be opened.\n\n" + ex.Message + "\n\nCheck that no other program is using it.", "Camera Guide");
            }
        }

        private void StopCamera()
        {
            if (_watchdog != null) _watchdog.Stop();
            VideoCaptureDevice s = _source;
            _source = null;
            if (s != null)
            {
                try { s.NewFrame -= OnNewFrame; s.SignalToStop(); } catch { }
                // do not wait here: stopping can take a moment and must not freeze the window
                System.Threading.Tasks.Task.Run(() => { try { s.WaitForStop(); } catch { } });
            }
            StartCamBtn.Content = "📷 Live Cam";
            if (_uploaded == null)
            {
                UploadedImageViewer.Source = null;
                _lastShown = null;
                SnapBtn.IsEnabled = false;
                PlaceholderPanel.Visibility = Visibility.Visible;
                LiveStatusTxt.Text = "";
                VerdictTxt.Text = "Status: Awaiting camera / image";
                _zoom.Reset(false);
            }
            UpdateOval();
            UpdateInfo();
        }

        // ----------------------------------------------------------------- frames --

        private void OnNewFrame(object sender, NewFrameEventArgs e)
        {
            if (_framePending) return;                    // the window is still drawing the last picture: drop this one
            _framePending = true;
            BitmapSource bmp;
            int w = e.Frame.Width, h = e.Frame.Height;
            try { bmp = ToBitmapSource(e.Frame, _square); }
            catch { _framePending = false; return; }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_source == null) { _framePending = false; return; }
                if (!_gotFrame) { _gotFrame = true; RememberCamera(CameraCombo.SelectedItem as string); }
                _lastShown = bmp;
                UploadedImageViewer.Source = bmp;
                _frames++;
                _sizeText = w + " × " + h + (_square && w != h ? ", shown as a centre square" : "");
                LiveStatusTxt.Text = "● Live  ·  " + _sizeText;
                UpdateOval();
                _framePending = false;
            }), DispatcherPriority.Render);
        }

        /// <summary>The picture for the screen: the centre square when asked (as on the scan screen), otherwise the whole frame.</summary>
        private static BitmapSource ToBitmapSource(Bitmap frame, bool centreSquare)
        {
            int w = frame.Width, h = frame.Height;
            int side = Math.Min(w, h);
            var rect = centreSquare && w != h ? new System.Drawing.Rectangle((w - side) / 2, (h - side) / 2, side, side)
                                              : new System.Drawing.Rectangle(0, 0, w, h);

            // copy the wanted part into its own bitmap first, then read that whole bitmap
            // (reading a sub-rectangle of the camera frame directly runs past the end of its memory on the last row)
            using (Bitmap part = frame.Clone(rect, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                BitmapData data = part.LockBits(new System.Drawing.Rectangle(0, 0, part.Width, part.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    var bs = BitmapSource.Create(part.Width, part.Height, 96, 96, PixelFormats.Bgr24, null, data.Scan0, data.Stride * part.Height, data.Stride);
                    bs.Freeze();
                    return bs;
                }
                finally { part.UnlockBits(data); }
            }
        }

        private void UpdateInfo()
        {
            if (_source == null) { ScanInfoTxt.Text = _uploaded != null ? "Showing an uploaded picture" : "Camera stopped"; return; }
            double secs = (DateTime.UtcNow - _fpsStart).TotalSeconds;
            if (secs >= 1) { _fps = _frames / secs; _frames = 0; _fpsStart = DateTime.UtcNow; }
            ScanInfoTxt.Text = CameraCombo.SelectedItem + "  ·  " + _sizeText + (_fps > 0 ? "  ·  " + _fps.ToString("0") + " pictures per second" : "");
        }

        // ------------------------------------------------------- snap and upload --

        /// <summary>Saves what is on the screen as a picture, so the set-up can be kept for reference.</summary>
        private void SnapBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_lastShown == null) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CameraGuide");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "CameraGuide_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(_lastShown));
                using (FileStream fs = File.Create(file)) enc.Save(fs);
                VerdictTxt.Text = "Status: picture saved.";
                RegionTxt.Text = file;
            }
            catch (Exception ex)
            {
                VerdictTxt.Text = "Status: the picture could not be saved.";
                RegionTxt.Text = ex.Message;
            }
        }

        private void UploadBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Choose a picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(dlg.FileName);
                bi.EndInit();
                bi.Freeze();

                StopCamera();
                _uploaded = bi;
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ShowUploaded();
                SnapBtn.IsEnabled = true;
                _sizeText = bi.PixelWidth + " × " + bi.PixelHeight;
                LiveStatusTxt.Text = "Uploaded picture  ·  " + Path.GetFileName(dlg.FileName);
                VerdictTxt.Text = "Status: showing the uploaded picture.";
                UpdateInfo();
            }
            catch (Exception ex)
            {
                MessageBox.Show("That picture could not be opened.\n\n" + ex.Message, "Camera Guide");
            }
        }

        private void ShowUploaded()
        {
            if (_uploaded == null) return;
            BitmapSource shown = _uploaded;
            int w = _uploaded.PixelWidth, h = _uploaded.PixelHeight;
            if (_square && w != h)
            {
                int side = Math.Min(w, h);
                var crop = new CroppedBitmap(_uploaded, new Int32Rect((w - side) / 2, (h - side) / 2, side, side));
                crop.Freeze();
                shown = crop;
            }
            _lastShown = shown;
            UploadedImageViewer.Source = shown;
            UpdateOval();
        }

        // ---------------------------------------------------------------- stepper --

        /// <summary>Front / Left / Right indicator, the same as on the scan screen. Touch a step to see its instruction.</summary>
        private void BuildStepper()
        {
            StepperGrid.Children.Clear();
            for (int n = 0; n < 3; n++)
            {
                int index = n;
                bool now = n == _step;
                string bg = now ? "#5F6652" : "#FFFFFF";
                string line = now ? "#5F6652" : "#D8D3C0";
                string ink = now ? "#FFFFFF" : "#8A8576";

                var disc = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0),
                    Background = Brush(now ? "#FFFFFF" : "#EFEAE0"),
                    Child = new TextBlock
                    {
                        Text = (n + 1).ToString(), FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brush(now ? "#5F6652" : "#8A8576")
                    }
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(disc);
                row.Children.Add(new TextBlock { Text = ViewName[n], FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush(ink), VerticalAlignment = VerticalAlignment.Center });

                var pill = new Border
                {
                    Background = Brush(bg), BorderBrush = Brush(line), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(14),
                    MinHeight = 54, Margin = new Thickness(0, 0, n == 2 ? 0 : 8, 0), Child = row, Cursor = Cursors.Hand
                };
                pill.MouseLeftButtonUp += (s, e) => { _step = index; BuildStepper(); };
                StepperGrid.Children.Add(pill);
            }
            ViewStepTxt.Text = StepHelp[_step];
            StateTxt.Text = "Step " + (_step + 1) + " of 3: " + StepName[_step] + " — Position your face in the oval";
        }

        private static SolidColorBrush Brush(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        // ------------------------------------------------------------------- oval --

        private void CameraViewportGrid_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateOval(); }

        /// <summary>Where the (Uniform-stretched) picture sits inside the viewport.</summary>
        private System.Windows.Rect PictureRect()
        {
            double W = CameraViewportGrid.ActualWidth, H = CameraViewportGrid.ActualHeight;
            double iw = H, ih = H;
            var src = UploadedImageViewer.Source as BitmapSource;
            if (src != null && src.PixelWidth > 0 && src.PixelHeight > 0) { iw = src.PixelWidth; ih = src.PixelHeight; }
            if (W <= 0 || H <= 0) return new System.Windows.Rect(0, 0, Math.Max(W, 0), Math.Max(H, 0));
            double scale = Math.Min(W / iw, H / ih);
            double dw = iw * scale, dh = ih * scale;
            return new System.Windows.Rect((W - dw) / 2.0, (H - dh) / 2.0, dw, dh);
        }

        private void UpdateOval()
        {
            if (!_ready) return;
            _square = SquareChk.IsChecked == true;
            System.Windows.Rect r = PictureRect();
            if (r.Width < 10 || r.Height < 10) return;

            double rx = r.Width * RadiusX * OvalScale, ry = r.Height * RadiusY * OvalScale;
            double cx = r.X + r.Width * CentreX, cy = r.Y + r.Height * CentreY;

            GuideOval.Width = rx * 2; GuideOval.Height = ry * 2;
            Canvas.SetLeft(GuideOval, cx - rx);
            Canvas.SetTop(GuideOval, cy - ry);

            CentreV.X1 = cx; CentreV.X2 = cx; CentreV.Y1 = r.Y; CentreV.Y2 = r.Y + r.Height;
            CentreH.Y1 = cy; CentreH.Y2 = cy; CentreH.X1 = r.X; CentreH.X2 = r.X + r.Width;

            bool pic = HasPicture;
            bool oval = OvalChk.IsChecked == true;
            GuideCanvas.Visibility = pic ? Visibility.Visible : Visibility.Collapsed;
            GuideOval.Visibility = oval ? Visibility.Visible : Visibility.Collapsed;
            CentreV.Visibility = CentreH.Visibility = CentreChk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            GuideBanner.Visibility = pic && oval ? Visibility.Visible : Visibility.Collapsed;
            StateBanner.Visibility = pic && oval ? Visibility.Visible : Visibility.Collapsed;
            UploadedImageViewer.RenderTransform = MirrorChk.IsChecked == true ? new ScaleTransform(-1, 1) : Transform.Identity;
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            UpdateOval();
            if (_uploaded != null) ShowUploaded();          // re-crop an uploaded picture
        }

        // ------------------------------------------------------------------- zoom --

        private void UpdateZoomUi()
        {
            if (_zoom == null) return;
            ZoomLevelTxt.Text = _zoom.Scale.ToString("0.0", CultureInfo.InvariantCulture) + "×";
            ZoomInBtn.IsEnabled = _zoom.Scale < _zoom.MaxZoom - 0.001;
            ZoomOutBtn.IsEnabled = _zoom.Scale > _zoom.MinZoom + 0.001;
            ZoomResetBtn.IsEnabled = _zoom.IsZoomed;
        }

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e) { _zoom.ZoomIn(); }
        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) { _zoom.ZoomOut(); }
        private void ZoomResetBtn_Click(object sender, RoutedEventArgs e) { _zoom.Reset(true); }

        // ----------------------------------------------------------------- layout --

        /// <summary>Portrait monitors: a large picture on top, the controls underneath. Landscape: side by side.</summary>
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ContentGrid == null) return;
            ContentGrid.ColumnDefinitions.Clear();
            ContentGrid.RowDefinitions.Clear();

            System.Windows.Size size = e.NewSize;
            bool portrait = size.Height > size.Width * 1.05;
            bool wide = size.Width > 850 && !portrait;

            if (wide)
            {
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ImageCard.ClearValue(FrameworkElement.HeightProperty);
                Grid.SetRow(ImageCard, 0); Grid.SetColumn(ImageCard, 0);
                Grid.SetRow(ControlsCard, 0); Grid.SetColumn(ControlsCard, 1);
                return;
            }

            ImageCard.Height = portrait ? Math.Max(340, Math.Min(size.Width * 0.78, size.Height * 0.52)) : 320;
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(ImageCard, 0); Grid.SetColumn(ImageCard, 0);
            Grid.SetRow(ControlsCard, 1); Grid.SetColumn(ControlsCard, 0);
        }
    }
}
