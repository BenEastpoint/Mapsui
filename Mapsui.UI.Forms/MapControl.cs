using Mapsui.Fetcher;
using Mapsui.Rendering.Skia;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Linq;
using Mapsui.Logging;
using Mapsui.Widgets;
using SkiaSharp.Views.Forms;
using Xamarin.Forms;
using SkiaSharp;
using Mapsui.Rendering;
using Mapsui.UI.Utils;
using System.Net;

namespace Mapsui.UI.Forms
{
    public partial class MapControl : SKGLView, IMapControl, IDisposable
    {
        class TouchEvent
        {
            public long Id { get; }
            public Geometries.Point Location { get; }
            public long Tick { get; }

            public TouchEvent(long id, Geometries.Point screenPosition, long tick)
            {
                Id = id;
                Location = screenPosition;
                Tick = tick;
            }
        }

        private const int None = 0;
        private const int Dragging = 1;
        private const int Zooming = 2;
        // See http://grepcode.com/file/repository.grepcode.com/java/ext/com.google.android/android/4.0.4_r2.1/android/view/ViewConfiguration.java#ViewConfiguration.0PRESSED_STATE_DURATION for values
        private const int shortTap = 125;
        private const int shortClick = 250;
        private const int delayTap = 300;
        private const int longTap = 500;

        private readonly MapRenderer _renderer = new MapRenderer();
        private float _skiaScale;
        private double _innerRotation;
        private Dictionary<long, TouchEvent> _touches = new Dictionary<long, TouchEvent>();
        private Geometries.Point _firstTouch;
        private System.Threading.Timer _doubleTapTestTimer;
        private int _numOfTaps = 0;
        private Dictionary<long, int> _fingers = new Dictionary<long, int>(20);
        private VelocityTracker _velocityTracker = new VelocityTracker();

        /// <summary>
        /// Saver for angle before last pinch movement
        /// </summary>
        private double _previousAngle;

        /// <summary>
        /// Saver for radius before last pinch movement
        /// </summary>
        private double _previousRadius = 1f;

        private TouchMode _mode;

        public event EventHandler ViewportInitialized;

        public MapControl()
        {
            Initialize();
        }

        public float SkiaScale
        {
            get
            {
                return _skiaScale;
            }
        }

        public ISymbolCache SymbolCache
        {
            get
            {
                return _renderer.SymbolCache;
            }
        }

        public void Initialize()
        {
            Map = new Map();
            BackgroundColor = Color.White;

            TryInitializeViewport();

            EnableTouchEvents = true;

            PaintSurface += OnPaintSurface;
            Touch += OnTouch;
            SizeChanged += OnSizeChanged; 
        }

        private void TryInitializeViewport()
        {
            if (_map.Viewport.Initialized) return;

            _skiaScale = (float)(CanvasSize.Width / Width);

            if (_map.Viewport.TryInitializeViewport(_map.Envelope, CanvasSize.Width / _skiaScale, CanvasSize.Height / _skiaScale))
            {
                Map.RefreshData(true);
                OnViewportInitialized();
            }
        }

        private void OnSizeChanged(object sender, EventArgs e)
        {
            if (Map != null)
            {
                Map.Viewport.Width = Width;
                Map.Viewport.Height = Height;
            }
        }

        private void OnTouch(object sender, SKTouchEventArgs e)
        {
            // Save time, when the event occures
            long ticks = DateTime.Now.Ticks;

            var location = GetScreenPosition(e.Location);

            // Get finger/handler for this event
            if (!_fingers.Keys.Contains(e.Id))
                if (_fingers.Count < 10)
                {
                    _fingers.Add(e.Id, _fingers.Count);
                }

            var id = _fingers[e.Id];

            if (e.ActionType == SKTouchAction.Pressed)
            {
                _firstTouch = location;

                _touches[id] = new TouchEvent(id, location, ticks);

                _velocityTracker.Clear();

                // Do we have a doubleTapTestTimer running?
                // If yes, stop it and increment _numOfTaps
                if (_doubleTapTestTimer != null)
                {
                    _doubleTapTestTimer.Dispose();
                    _doubleTapTestTimer = null;
                    _numOfTaps++;
                }
                else
                    _numOfTaps = 1;

                e.Handled = OnTouchStart(_touches.Select(t => t.Value.Location).ToList());
            }
            if (e.ActionType == SKTouchAction.Released)
            {
                // Delete e.Id from _fingers, because finger is released
                
                _fingers.Remove(e.Id);

                double velocityX;
                double velocityY;

                (velocityX, velocityY) = _velocityTracker.CalcVelocity(id, ticks);

                // Is this a fling or swipe?
                if (velocityX > 10000 || velocityY > 10000)
                {
                    System.Diagnostics.Debug.WriteLine($"Velocity X = {velocityX}, Velocity Y = {velocityY}");

                    e.Handled = OnFlinged(velocityX, velocityY);
                }

                // Do we have a tap event
                var temp = _touches[id];
                if (_touches[id].Location.Equals(_firstTouch) && ticks - _touches[id].Tick < (e.DeviceType == SKTouchDeviceType.Mouse ? shortClick : longTap) * 10000)
                {
                    // Start a timer with timeout delayTap ms. If than isn't arrived another tap, than it is a single
                    _doubleTapTestTimer = new System.Threading.Timer((l) =>
                    {
                        if (_numOfTaps > 1)
                        {
                            if (!e.Handled)
                                e.Handled = OnDoubleTapped(location, _numOfTaps);
                        }
                        else
                            if (!e.Handled)
                                e.Handled = OnSingleTapped((Geometries.Point)l);
                        _numOfTaps = 1;
                        _doubleTapTestTimer.Dispose();
                        _doubleTapTestTimer = null;
                    }, location, delayTap, -1);
                }
                else if (_touches[id].Location.Equals(_firstTouch) && ticks - _touches[id].Tick >= longTap * 10000)
                {
                    if (!e.Handled)
                        e.Handled = OnLongTapped(location);
                }
                var releasedTouch = _touches[id];
                _touches.Remove(id);

                if (!e.Handled)
                    e.Handled = OnTouchEnd(_touches.Select(t => t.Value.Location).ToList(), releasedTouch.Location);
            }
            if (e.ActionType == SKTouchAction.Moved)
            {
                _touches[id] = new TouchEvent(id, location, ticks);

                if (e.InContact)
                    _velocityTracker.AddEvent(id, location, ticks);

                if (e.InContact && !e.Handled)
                    e.Handled = OnTouchMove(_touches.Select(t => t.Value.Location).ToList());
                else
                    e.Handled = OnHovered(_touches.Select(t => t.Value.Location).FirstOrDefault());
            }
        }

        void OnPaintSurface(object sender, SKPaintGLSurfaceEventArgs skPaintSurfaceEventArgs)
        {
            TryInitializeViewport();
            if (!_map.Viewport.Initialized) return;

            _map.Viewport.Width = Width;
            _map.Viewport.Height = Height;

            skPaintSurfaceEventArgs.Surface.Canvas.Scale(_skiaScale, _skiaScale);

            _renderer.Render(skPaintSurfaceEventArgs.Surface.Canvas,
                _map.Viewport, _map.Layers, _map.Widgets, _map.BackColor);
        }

        private void OnViewportInitialized()
        {
            ViewportInitialized?.Invoke(this, EventArgs.Empty);
        }

        private Geometries.Point GetScreenPosition(SKPoint point)
        {
            return new Geometries.Point(point.X / _skiaScale, point.Y / _skiaScale);
        }

        private void MapRefreshGraphics(object sender, EventArgs eventArgs)
        {
            RefreshGraphics();
        }

        public void RefreshGraphics()
        {
            InvalidateCanvas();
        }

        public void RefreshData()
        {
            _map?.RefreshData(true);
        }

        internal void InvalidateCanvas()
        {
            InvalidateSurface();
        }

        private static void WidgetTouched(IWidget widget, Geometries.Point screenPosition)
        {
            widget.HandleWidgetTouched(screenPosition);
        }

        /// <summary>
        /// Event handlers
        /// </summary>

        /// <summary>
        /// TouchStart is called, when user press a mouse button or touch the display
        /// </summary>
        public event EventHandler<TouchedEventArgs> TouchStarted;

        /// <summary>
        /// TouchEnd is called, when user release a mouse button or doesn't touch display anymore
        /// </summary>
        public event EventHandler<TouchedEventArgs> TouchEnded;

        /// <summary>
        /// TouchMove is called, when user move mouse over map (independent from mouse button state) or move finger on display
        /// </summary>
#if __WPF__
        public new event EventHandler<TouchedEventArgs> TouchMove;
#else
        public event EventHandler<TouchedEventArgs> TouchMove;
#endif

        /// <summary>
        /// Hover is called, when user move mouse over map without pressing mouse button
        /// </summary>
#if __ANDROID__
        public new event EventHandler<HoveredEventArgs> Hovered;
#else
        public event EventHandler<HoveredEventArgs> Hovered;
#endif

        /// <summary>
        /// Swipe is called, when user release mouse button or lift finger while moving with a certain speed 
        /// </summary>
        public event EventHandler<SwipedEventArgs> Swipe;

        /// <summary>
        /// Fling is called, when user release mouse button or lift finger while moving with a certain speed, higher than speed of swipe 
        /// </summary>
        public event EventHandler<SwipedEventArgs> Fling;

        /// <summary>
        /// SingleTap is called, when user clicks with a mouse button or tap with a finger on map 
        /// </summary>
        public event EventHandler<TappedEventArgs> SingleTap;

        /// <summary>
        /// LongTap is called, when user clicks with a mouse button or tap with a finger on map for 500 ms
        /// </summary>
        public event EventHandler<TappedEventArgs> LongTap;

        /// <summary>
        /// DoubleTap is called, when user clicks with a mouse button or tap with a finger two or more times on map
        /// </summary>
        public event EventHandler<TappedEventArgs> DoubleTap;        

        /// <summary>
        /// Zoom is called, when map should be zoomed
        /// </summary>
        public event EventHandler<ZoomedEventArgs> Zoomed;

        /// <summary>
        /// Called, when map should zoom out
        /// </summary>
        /// <param name="screenPosition">Center of zoom out event</param>
        private bool OnZoomOut(Geometries.Point screenPosition)
        {
            var args = new ZoomedEventArgs(screenPosition, ZoomDirection.ZoomOut);

            Zoomed?.Invoke(this, args);

            if (args.Handled)
                return true;

            // TODO
            // Perform standard behavior

            return true;
        }

        /// <summary>
        /// Called, when map should zoom in
        /// </summary>
        /// <param name="screenPosition">Center of zoom in event</param>
        private bool OnZoomIn(Geometries.Point screenPosition)
        {
            var args = new ZoomedEventArgs(screenPosition, ZoomDirection.ZoomIn);

            Zoomed?.Invoke(this, args);

            if (args.Handled)
                return true;

            // TODO
            // Perform standard behavior

            return true;
        }

        /// <summary>
        /// Called, when mouse/finger/pen hovers around
        /// </summary>
        /// <param name="screenPosition">Actual position of mouse/finger/pen</param>
        private bool OnHovered(Geometries.Point screenPosition)
        {
            var args = new HoveredEventArgs(screenPosition);

            Hovered?.Invoke(this, args);

            return args.Handled;
        }

        /// <summary>
        /// Called, when mouse/finger/pen swiped over map
        /// </summary>
        /// <param name="velocityX">Velocity in x direction in pixel/second</param>
        /// <param name="velocityY">Velocity in y direction in pixel/second</param>
        private bool OnSwiped(double velocityX, double velocityY)
        {
            var args = new SwipedEventArgs(velocityX, velocityY);

            Swipe?.Invoke(this, args);

            // TODO
            // Perform standard behavior

            return args.Handled;
        }

        /// <summary>
        /// Called, when mouse/finger/pen flinged over map
        /// </summary>
        /// <param name="velocityX">Velocity in x direction in pixel/second</param>
        /// <param name="velocityY">Velocity in y direction in pixel/second</param>
        private bool OnFlinged(double velocityX, double velocityY)
        {
            var args = new SwipedEventArgs(velocityX, velocityY);

            Fling?.Invoke(this, args);

            // TODO
            // Perform standard behavior

            return args.Handled;
        }

        /// <summary>
        /// Called, when mouse/finger/pen click/touch map
        /// </summary>
        /// <param name="touchPoints">List of all touched points</param>
        private bool OnTouchStart(List<Geometries.Point> touchPoints)
        {
            var args = new TouchedEventArgs(touchPoints);

            TouchStarted?.Invoke(this, args);

            if (args.Handled)
                return true;

            if (touchPoints.Count >= 2)
            {
                (_previousCenter, _previousRadius, _previousAngle) = GetPinchValues(touchPoints);
                _mode = TouchMode.Zooming;
                _innerRotation = _map.Viewport.Rotation;
            }
            else
            {
                _mode = TouchMode.Dragging;
                _previousCenter = touchPoints.First();
            }

            return true;
        }

        /// <summary>
        /// Called, when mouse/finger/pen anymore click/touch map
        /// </summary>
        /// <param name="touchPoints">List of all touched points</param>
        /// <param name="releasedPoint">Released point, which was touched before</param>
        private bool OnTouchEnd(List<Geometries.Point> touchPoints, Geometries.Point releasedPoint)
        {
            var args = new TouchedEventArgs(touchPoints);

            TouchEnded?.Invoke(this, args);

            // Last touch released
            if (touchPoints.Count == 0)
            {
                InvalidateCanvas();
                _mode = TouchMode.None;
                _map.RefreshData(true);
            }

            return args.Handled;
        }

        /// <summary>
        /// Called, when mouse/finger/pen moves over map
        /// </summary>
        /// <param name="touchPoints">List of all touched points</param>
        private bool OnTouchMove(List<Geometries.Point> touchPoints)
        {
            var args = new TouchedEventArgs(touchPoints);

            TouchMove?.Invoke(this, args);

            if (args.Handled)
                return true;

            switch (_mode)
            {
                case TouchMode.Dragging:
                    {
                        if (touchPoints.Count != 1)
                            return false;

                        var touchPosition = touchPoints.First();

                        if (_previousCenter != null && !_previousCenter.IsEmpty())
                        {
                            _map.Viewport.Transform(touchPosition.X, touchPosition.Y, _previousCenter.X, _previousCenter.Y);

                            ViewportLimiter.LimitExtent(_map.Viewport, _map.PanMode, _map.PanLimits, _map.Envelope);

                            InvalidateCanvas();
                        }

                        _previousCenter = touchPosition;
                    }
                    break;
                case TouchMode.Zooming:
                    {
                        if (touchPoints.Count < 2)
                            return false;

                        var (prevCenter, prevRadius, prevAngle) = (_previousCenter, _previousRadius, _previousAngle);
                        var (center, radius, angle) = GetPinchValues(touchPoints);

                        double rotationDelta = 0;

                        if (RotationLock)
                        {
                            _innerRotation += angle - prevAngle;
                            _innerRotation %= 360;

                            if (_innerRotation > 180)
                                _innerRotation -= 360;
                            else if (_innerRotation < -180)
                                _innerRotation += 360;

                            if (_map.Viewport.Rotation == 0 && Math.Abs(_innerRotation) >= Math.Abs(UnSnapRotationDegrees))
                                rotationDelta = _innerRotation;
                            else if (_map.Viewport.Rotation != 0)
                            {
                                if (Math.Abs(_innerRotation) <= Math.Abs(ReSnapRotationDegrees))
                                    rotationDelta = -_map.Viewport.Rotation;
                                else
                                    rotationDelta = _innerRotation - _map.Viewport.Rotation;
                            }
                        }

                        _map.Viewport.Transform(center.X, center.Y, prevCenter.X, prevCenter.Y, radius / prevRadius, rotationDelta);

                        (_previousCenter, _previousRadius, _previousAngle) = (center, radius, angle);

                        ViewportLimiter.Limit(_map.Viewport,
                            _map.ZoomMode, _map.ZoomLimits, _map.Resolutions,
                            _map.PanMode, _map.PanLimits, _map.Envelope);

                        InvalidateCanvas();
                    }
                    break;
            }

            return true;
        }

        /// <summary>
        /// Called, when mouse/finger/pen tapped on map 2 or more times
        /// </summary>
        /// <param name="screenPosition">First clicked/touched position on screen</param>
        /// <param name="numOfTaps">Number of taps on map (2 is a double click/tap)</param>
        private bool OnDoubleTapped(Geometries.Point screenPosition, int numOfTaps)
        {
            var args = new TappedEventArgs(screenPosition, numOfTaps);

            DoubleTap?.Invoke(this, args);

            if (args.Handled)
                return true;

            var tapWasHandled = Map.InvokeInfo(screenPosition, screenPosition, _scale, _renderer.SymbolCache, WidgetTouched, numOfTaps);

            if (!tapWasHandled)
            {
                // Double tap as zoom
                return OnZoomIn(screenPosition);
            }

            return false;
        }

        /// <summary>
        /// Called, when mouse/finger/pen tapped on map one time
        /// </summary>
        /// <param name="screenPosition">Clicked/touched position on screen</param>
        private bool OnSingleTapped(Geometries.Point screenPosition)
        {
            var args = new TappedEventArgs(screenPosition, 1);

            SingleTap?.Invoke(this, args);

            if (args.Handled)
                return true;

            return Map.InvokeInfo(screenPosition, screenPosition, _scale, _renderer.SymbolCache, WidgetTouched, 1);
        }

        /// <summary>
        /// Called, when mouse/finger/pen tapped long on map
        /// </summary>
        /// <param name="screenPosition">Clicked/touched position on screen</param>
        private bool OnLongTapped(Geometries.Point screenPosition)
        {
            var args = new TappedEventArgs(screenPosition, 1);

            LongTap?.Invoke(this, args);

            return args.Handled;
        }

        /// <summary>
        /// Public functions
        /// </summary>

        public float GetDeviceIndependentUnits()
        {
            return SkiaScale;
        }

        private void RunOnUIThread(Action action)
        {
            Device.BeginInvokeOnMainThread(action);
        }

#if !__WPF__ && !__UWP__
        public new void Dispose()
        {
            Unsubscribe();
        }

        protected new void Dispose(bool disposing)
        {
            Unsubscribe();
        }
#endif
    }
}