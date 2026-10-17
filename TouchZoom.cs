// ============================================================================
// TouchZoom.cs  -  namespace CameraGuide
//
// Pinch-to-zoom and pan for the live camera preview.
//
//   * Two-finger pinch (Windows touch manipulation) zooms around the point between the fingers; one or two fingers drag the zoomed picture.
//   * Mouse: wheel zooms at the pointer, drag pans, double-click resets.
//   * Buttons: ZoomIn / ZoomOut / Reset animate smoothly.
//
// DISPLAY ONLY. The zoom is a RenderTransform on the layer that holds the preview picture, so it changes what is drawn on screen
// and nothing else: the frames that are captured, analysed (classifier, Grad-CAM++, lesion detector) and saved are never touched.
// ============================================================================

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CameraGuide
{
    public sealed class TouchZoom
    {
        private readonly FrameworkElement _viewport;     // receives the gestures (fixed on screen)
        private readonly FrameworkElement _content;      // is scaled and moved (same size as the viewport)
        private readonly MatrixTransform _transform = new MatrixTransform();

        private double _scale = 1.0, _tx, _ty;
        private Point? _dragStart;
        private double _dragTx, _dragTy;

        // smooth animation (buttons, double-click reset)
        private bool _animating;
        private double _fromScale, _fromTx, _fromTy, _toScale, _toTx, _toTy;
        private DateTime _animStart;
        private const double AnimMs = 220;

        public double MinZoom { get; private set; } = 1.0;
        public double MaxZoom { get; private set; } = 4.0;
        public double Scale => _scale;
        public bool IsZoomed => _scale > MinZoom + 0.001 || Math.Abs(_tx) > 0.5 || Math.Abs(_ty) > 0.5;

        /// <summary>Raised whenever the zoom or position changes (to update the "2.0×" label and the reset button).</summary>
        public event Action Changed;

        public TouchZoom(FrameworkElement viewport, FrameworkElement content, double minZoom, double maxZoom)
        {
            _viewport = viewport;
            _content = content;
            SetLimits(minZoom, maxZoom);

            _content.RenderTransformOrigin = new Point(0, 0);
            _content.RenderTransform = _transform;

            _viewport.IsManipulationEnabled = true;
            _viewport.ManipulationStarting += (s, e) =>
            {
                e.ManipulationContainer = _viewport;
                e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
                StopAnimation();
                e.Handled = true;
            };
            _viewport.ManipulationInertiaStarting += (s, e) =>
            {
                e.TranslationBehavior.DesiredDeceleration = 0.0025;       // device-independent px / ms^2: a short, smooth glide
                e.ExpansionBehavior.DesiredDeceleration = 0.0008;
                e.Handled = true;
            };
            _viewport.ManipulationDelta += OnManipulationDelta;
            _viewport.MouseWheel += OnMouseWheel;
            _viewport.MouseLeftButtonDown += OnMouseDown;
            _viewport.MouseMove += OnMouseMove;
            _viewport.MouseLeftButtonUp += OnMouseUp;
            _viewport.LostMouseCapture += (s, e) => _dragStart = null;
            _viewport.SizeChanged += (s, e) => { Apply(_scale, _tx, _ty); };
        }

        public void SetLimits(double min, double max)
        {
            MinZoom = Math.Max(0.5, Math.Min(1.0, min));
            MaxZoom = Math.Max(1.5, Math.Min(8.0, max));
            if (_content != null) Apply(_scale, _tx, _ty);
        }

        // ------------------------------------------------------------------ touch --

        private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            double s = e.DeltaManipulation.Scale.X;                      // uniform: both axes equal for a pinch
            if (s <= 0) s = 1;
            Point o = e.ManipulationOrigin;                              // the point between the fingers, in viewport coordinates

            double newScale = Clamp(_scale * s, MinZoom, MaxZoom);
            double k = newScale / _scale;
            // keep the picture point under the fingers where it is, then follow the fingers' movement
            double tx = o.X - (o.X - _tx) * k + e.DeltaManipulation.Translation.X;
            double ty = o.Y - (o.Y - _ty) * k + e.DeltaManipulation.Translation.Y;
            Apply(newScale, tx, ty);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ mouse --

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            StopAnimation();
            double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            ZoomAt(factor, e.GetPosition(_viewport), animate: false);
            e.Handled = true;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null) return;                          // a touch is handled by the manipulation events
            if (e.ClickCount == 2) { Reset(true); e.Handled = true; return; }
            if (_scale <= MinZoom + 0.001) return;
            StopAnimation();
            _dragStart = e.GetPosition(_viewport);
            _dragTx = _tx; _dragTy = _ty;
            _viewport.CaptureMouse();
            _viewport.Cursor = Cursors.SizeAll;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(_viewport);
            Apply(_scale, _dragTx + (p.X - _dragStart.Value.X), _dragTy + (p.Y - _dragStart.Value.Y));
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStart == null) return;
            _dragStart = null;
            _viewport.ReleaseMouseCapture();
            _viewport.Cursor = Cursors.Arrow;
        }

        // ---------------------------------------------------------------- buttons --

        public void ZoomIn() => ZoomAt(1.4, Centre(), animate: true);
        public void ZoomOut() => ZoomAt(1 / 1.4, Centre(), animate: true);

        /// <summary>Jump straight to a zoom level around a point (no animation).</summary>
        public void ZoomTo(double scale, Point centre)
        {
            StopAnimation();
            ZoomAt(Clamp(scale, MinZoom, MaxZoom) / _scale, centre, animate: false);
        }

        public void Reset(bool animate)
        {
            if (animate) AnimateTo(1.0, 0, 0);
            else { StopAnimation(); Apply(1.0, 0, 0); }
        }

        private Point Centre() => new Point(_viewport.ActualWidth / 2.0, _viewport.ActualHeight / 2.0);

        private void ZoomAt(double factor, Point o, bool animate)
        {
            double newScale = Clamp(_scale * factor, MinZoom, MaxZoom);
            double k = newScale / _scale;
            double tx = o.X - (o.X - _tx) * k;
            double ty = o.Y - (o.Y - _ty) * k;
            if (animate) AnimateTo(newScale, tx, ty);
            else Apply(newScale, tx, ty);
        }

        // -------------------------------------------------------------- animation --

        private void AnimateTo(double scale, double tx, double ty)
        {
            Clamped(ref scale, ref tx, ref ty);
            _fromScale = _scale; _fromTx = _tx; _fromTy = _ty;
            _toScale = scale; _toTx = tx; _toTy = ty;
            _animStart = DateTime.UtcNow;
            if (!_animating) { _animating = true; CompositionTarget.Rendering += OnRendering; }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _animStart).TotalMilliseconds / AnimMs;
            if (t >= 1) { StopAnimation(); Apply(_toScale, _toTx, _toTy); return; }
            double eased = 1 - Math.Pow(1 - t, 3);                       // ease-out
            Apply(_fromScale + (_toScale - _fromScale) * eased, _fromTx + (_toTx - _fromTx) * eased, _fromTy + (_toTy - _fromTy) * eased);
        }

        private void StopAnimation()
        {
            if (!_animating) return;
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        // ----------------------------------------------------------------- maths --

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Keeps the picture covering the viewport (or centred when it is smaller than the viewport).</summary>
        private void Clamped(ref double scale, ref double tx, ref double ty)
        {
            scale = Clamp(scale, MinZoom, MaxZoom);
            double w = _viewport.ActualWidth, h = _viewport.ActualHeight;
            tx = scale >= 1.0 ? Clamp(tx, w - w * scale, 0) : (w - w * scale) / 2.0;
            ty = scale >= 1.0 ? Clamp(ty, h - h * scale, 0) : (h - h * scale) / 2.0;
        }

        private void Apply(double scale, double tx, double ty)
        {
            Clamped(ref scale, ref tx, ref ty);
            _scale = scale; _tx = tx; _ty = ty;
            _transform.Matrix = new Matrix(scale, 0, 0, scale, tx, ty);
            Changed?.Invoke();
        }
    }
}
