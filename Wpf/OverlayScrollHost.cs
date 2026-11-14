using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IPbind
{
    // Thin overlay thumb on a TextBox/RichTextBox. Mirrors AutoHideScrollBox:
    // the bar hides 5 seconds after the pointer leaves (ScrollBarIdleState).
    sealed class OverlayScrollHost
    {
        readonly TextBoxBase box;
        readonly Border track;
        readonly Border thumb;
        readonly ScrollBarIdleState state = new ScrollBarIdleState();
        readonly DispatcherTimer timer;
        readonly Brush idleBrush;
        readonly Brush hotBrush;
        bool dragging;
        double dragStartY;
        double dragStartOffset;

        public OverlayScrollHost(TextBoxBase box, Border track, Border thumb, bool dark)
        {
            this.box = box;
            this.track = track;
            this.thumb = thumb;
            idleBrush = brush("ThumbBrush", dark ? Color.FromRgb(0x6E, 0x6E, 0x73) : Color.FromRgb(0xC6, 0xC6, 0xC8));
            hotBrush = brush("ThumbHotBrush", Color.FromRgb(0x8E, 0x8E, 0x93));
            thumb.Background = idleBrush;

            box.PreviewMouseWheel += OnWheel;
            box.TextChanged += delegate { SyncThumbOnly(); };
            box.SizeChanged += delegate { SyncThumbOnly(); };
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged), true);
            track.MouseEnter += delegate { state.PointerEntered(); Apply(); };
            track.MouseLeave += delegate { state.PointerExited(DateTime.Now); Apply(); };
            track.MouseMove += OnTrackMove;
            track.MouseLeftButtonDown += OnTrackDown;
            track.MouseLeftButtonUp += OnTrackUp;
            box.MouseEnter += delegate { state.PointerEntered(); Apply(); };
            box.MouseLeave += delegate { if (!dragging) { state.PointerExited(DateTime.Now); Apply(); } };

            timer = new DispatcherTimer();
            timer.Interval = AutoHideScrollBars.CheckEvery;
            timer.Tick += delegate
            {
                if (state.Tick(DateTime.Now)) Apply();
            };
            timer.Start();
            box.Unloaded += delegate { timer.Stop(); };
            SyncThumbOnly();
        }

        static Brush brush(string key, Color fallback)
        {
            object v = Application.Current != null ? Application.Current.TryFindResource(key) : null;
            SolidColorBrush b = v as SolidColorBrush;
            return b ?? new SolidColorBrush(fallback);
        }

        ScrollViewer Viewer()
        {
            return FindViewer(box);
        }

        static ScrollViewer FindViewer(DependencyObject d)
        {
            if (d == null) return null;
            ScrollViewer sv = d as ScrollViewer;
            if (sv != null) return sv;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                ScrollViewer found = FindViewer(VisualTreeHelper.GetChild(d, i));
                if (found != null) return found;
            }
            return null;
        }

        public void SyncThumbOnly()
        {
            ScrollViewer sv = Viewer();
            bool can = sv != null && sv.ExtentHeight > sv.ViewportHeight + 0.5;
            if (!can)
            {
                thumb.Visibility = Visibility.Collapsed;
                return;
            }
            double trackH = track.ActualHeight;
            if (trackH < 8) trackH = box.ActualHeight;
            double pad = 4;
            double inner = Math.Max(1, trackH - 2 * pad);
            double ratio = sv.ViewportHeight / sv.ExtentHeight;
            double thumbH = Math.Max(16, inner * ratio);
            if (thumbH > inner) thumbH = inner;
            double max = sv.ExtentHeight - sv.ViewportHeight;
            double y = pad;
            if (max > 0)
                y = pad + (inner - thumbH) * (sv.VerticalOffset / max);
            thumb.Height = thumbH;
            thumb.Margin = new Thickness(0, y, 0, 0);
            thumb.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        void Apply()
        {
            thumb.Visibility = (state.Visible && CanScroll()) ? Visibility.Visible : Visibility.Collapsed;
            SyncThumbOnly();
        }

        bool CanScroll()
        {
            ScrollViewer sv = Viewer();
            return sv != null && sv.ExtentHeight > sv.ViewportHeight + 0.5;
        }

        void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            SyncThumbOnly();
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            state.Scrolled(DateTime.Now);
            Apply();
        }

        void OnTrackDown(object sender, MouseButtonEventArgs e)
        {
            if (!CanScroll()) return;
            state.PointerEntered();
            Apply();
            Point p = e.GetPosition(track);
            double top = thumb.Margin.Top;
            double bottom = top + thumb.Height;
            if (p.Y >= top && p.Y <= bottom)
            {
                dragging = true;
                dragStartY = p.Y;
                ScrollViewer sv = Viewer();
                dragStartOffset = sv != null ? sv.VerticalOffset : 0;
                track.CaptureMouse();
            }
            else
            {
                ScrollViewer sv = Viewer();
                if (sv == null) return;
                double page = sv.ViewportHeight;
                sv.ScrollToVerticalOffset(sv.VerticalOffset + (p.Y < top ? -page : page));
                state.Scrolled(DateTime.Now);
                Apply();
            }
            e.Handled = true;
        }

        void OnTrackMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(track);
            double top = thumb.Margin.Top;
            bool over = p.Y >= top && p.Y <= top + thumb.Height;
            thumb.Background = (dragging || over) ? hotBrush : idleBrush;
            if (!dragging) return;
            ScrollViewer sv = Viewer();
            if (sv == null) return;
            double max = sv.ExtentHeight - sv.ViewportHeight;
            if (max <= 0) return;
            double inner = Math.Max(1, track.ActualHeight - 8 - thumb.Height);
            double delta = e.GetPosition(track).Y - dragStartY;
            sv.ScrollToVerticalOffset(dragStartOffset + delta * max / inner);
            state.Scrolled(DateTime.Now);
            Apply();
        }

        void OnTrackUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            track.ReleaseMouseCapture();
            state.Scrolled(DateTime.Now);
            Apply();
        }
    }
}
