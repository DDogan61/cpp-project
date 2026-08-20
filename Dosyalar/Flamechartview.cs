using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FlameCharter
{
    // What the caller gets about the clicked box. Line == 0 means we have no
    // line info for it ([external] frame or code without a PDB).
    public sealed class FlameSelectionInfo
    {
        public string Name;
        public double StartMs;
        public double EndMs;
        public double DurationMs { get { return EndMs - StartMs; } }

        public string File;
        public int Line;
        public double LineStartMs;
        public double LineEndMs;
        public double LineDurationMs { get { return LineEndMs - LineStartMs; } }
    }

    // Everything is drawn straight into the DrawingContext instead of using WPF
    // elements.
    //
    // Canvas + Rectangle is "retained mode": every box becomes a visual tree node
    // with layout, hit-testing and the property system behind it. A few thousand
    // boxes and the whole app starts stuttering. OnRender is "immediate mode",
    // only the draw commands are stored and no nodes are created.
    public sealed class FlameChartView : FrameworkElement
    {
        public const double RowHeight = 20;
        private const double TimelineHeight = 24;   // axis + ticks + labels

        private const double NameFontSize = 11;
        private const double LineFontSize = 9;
        private const double TextPad = 3;

        private const double MinNameWidth = 34;     // narrower box cannot fit a name
        private const double MinLineWidth = 20;     // narrower slice cannot fit a line no
        private const double TargetGridPx = 80;     // wanted distance between grid lines

        private static readonly Typeface Face = new Typeface("Segoe UI");

        private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(200, 200, 200));
        private static readonly Brush NameBrush = Frozen(Color.FromRgb(20, 20, 20));
        private static readonly Brush LineNoBrush = Frozen(Color.FromRgb(70, 70, 70));
        private static readonly Brush TextBrush = Frozen(Color.FromRgb(40, 40, 40));
        private static readonly Brush SegmentSelectionBrush = Frozen(Color.FromArgb(40, 0, 0, 0));

        private static readonly Pen GridPen = MakeGridPen();
        private static readonly Pen DividerPen = MakeDividerPen();
        private static readonly Pen SelectionPen = MakeSelectionPen();
        private static readonly Pen AxisPen = MakeAxisPen();
        private static readonly Pen SegmentSelectionPen = MakeSegmentSelectionPen();

        private static readonly Dictionary<string, Brush> _brushCache =
            new Dictionary<string, Brush>(StringComparer.Ordinal);

        private readonly Dictionary<string, FormattedText> _nameCache =
            new Dictionary<string, FormattedText>(StringComparer.Ordinal);
        private readonly Dictionary<int, FormattedText> _lineCache =
            new Dictionary<int, FormattedText>();
        private readonly Dictionary<int, FormattedText> _tickCache =
            new Dictionary<int, FormattedText>();

        private FlameChartModel _model;
        private FlameRun _selected;
        private int _selectedSegmentIndex = -1;
        private double _pxPerMs = 0.4;

        // The visible window, handed to us by the ScrollViewer. Needed to stick
        // the names to the visible edge and to skip boxes that are off screen.
        private double _scrollX;
        private double _viewportWidth;

        // Fires when a box is clicked, and with null when the empty area is
        // clicked. Where to show the info is the caller's problem.
        public event Action<FlameSelectionInfo> SelectionChanged;

        // Fires on a double click. Opening the source file is the caller's job.
        public event Action<FlameSelectionInfo> NavigateRequested;

        public FlameChartView()
        {
            // Keeps the 1 pixel edges from coming out blurry.
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        public FlameChartModel Model
        {
            get { return _model; }
            set { _model = value; Refresh(); }
        }

        // Pixels per ms, so horizontal zoom is just this value.
        public double PixelsPerMs
        {
            get { return _pxPerMs; }
            set
            {
                double v = Math.Max(0.01, Math.Min(50, value));
                if (Math.Abs(v - _pxPerMs) < 1e-9) return;

                _pxPerMs = v;
                Refresh();
            }
        }

        // Fed from ScrollViewer.ScrollChanged. The element sits inside the
        // ScrollViewer at full size, so scrolling does not call OnRender again.
        // That is why the visible range comes from outside and we redraw when it
        // changes. Only InvalidateVisual, the width did not change so there is
        // nothing to measure.
        public void SetViewport(double offsetX, double viewportWidth)
        {
            if (Math.Abs(offsetX - _scrollX) < 0.5 &&
                Math.Abs(viewportWidth - _viewportWidth) < 0.5) return;

            _scrollX = offsetX;
            _viewportWidth = viewportWidth;

            InvalidateVisual();
        }

        public void Refresh()
        {
            InvalidateMeasure();   // the width keeps growing while sampling
            InvalidateVisual();
        }

        // ====================================================================
        // Layout and drawing
        // ====================================================================

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_model == null) return new Size(1, RowHeight + TimelineHeight);

            // The ruler sits under the boxes. If its height is left out of the
            // measurement the ScrollViewer just cuts that strip off.
            return new Size(Math.Max(1, _model.TotalMs * _pxPerMs),
                            Math.Max(RowHeight, _model.Depth * RowHeight) + TimelineHeight);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Drawing the background also gives us a hit-test area. Without it
            // the mouse events never reach the element at all.
            dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));

            if (_model == null) return;

            double viewLeft = _scrollX;
            double viewRight = viewLeft + VisibleWidth();

            DrawRuns(dc, dpi, viewLeft, viewRight);

            // Lines go AFTER the boxes. Underneath they would be invisible in the
            // busy part of the chart, which is exactly where they are needed.
            DrawTimeGrid(dc);
            DrawTimeline(dc, dpi);
        }

        // On the first frame ScrollChanged has not arrived yet, so treat the
        // whole element as visible. Otherwise nothing gets drawn.
        private double VisibleWidth()
        {
            return _viewportWidth > 0 ? _viewportWidth : Math.Max(1, RenderSize.Width);
        }

        private void DrawRuns(DrawingContext dc, double dpi, double viewLeft, double viewRight)
        {
            for (int d = 0; d < _model.Levels.Count; d++)
            {
                double y = d * RowHeight;
                List<FlameRun> runs = _model.Levels[d];

                for (int i = 0; i < runs.Count; i++)
                {
                    FlameRun run = runs[i];

                    double x = run.StartMs * _pxPerMs;
                    double w = run.DurationMs * _pxPerMs;

                    // Thinner than half a pixel is invisible anyway.
                    if (w < 0.5) continue;

                    // No draw commands for boxes outside the screen. Runs are
                    // sorted by StartMs, so at the first box on the right we can
                    // drop the rest of the row.
                    if (x > viewRight) break;
                    if (x + w < viewLeft) continue;

                    var rect = new Rect(x, y, Math.Max(w - 1, 0.5), RowHeight - 1);
                    dc.DrawRectangle(BrushFor(run.Name), null, rect);

                    // Keeps the inner drawings from spilling out of the box.
                    dc.PushClip(new RectangleGeometry(rect));

                    // Outline inside the clip: the half of the thick pen that
                    // sticks out gets cut, so it does not sit on the neighbour box.
                    if (ReferenceEquals(run, _selected))
                    {
                        dc.DrawRectangle(null, SelectionPen, rect);
                    }

                    double nameEnd = x;

                    if (w >= MinNameWidth)
                    {
                        FormattedText nameText = NameTextFor(run.Name, dpi);

                        // If the box runs off to the left, the name sticks to the
                        // visible edge and slides along as the box moves.
                        double textX = Math.Max(x + TextPad, viewLeft + TextPad);

                        // But it should not go past the right edge of the box. The
                        // clip would cut a long name anyway, this way as many
                        // letters as possible stay readable until the last moment.
                        double maxX = x + w - nameText.Width - TextPad;
                        if (textX > maxX) textX = Math.Max(x + TextPad, maxX);

                        dc.DrawText(nameText, new Point(textX, y + 1));
                        nameEnd = textX + nameText.Width + 6;
                    }

                    dc.Pop();

                    DrawLineSegments(dc, run, y, nameEnd, dpi);
                }
            }
        }

        private void DrawLineSegments(DrawingContext dc, FlameRun run,
                                      double y, double nameEnd, double dpi)
        {
            List<LineSegment> segments = run.Lines;
            if (segments.Count == 0) return;   // one segment means nothing to split

            double top = y;
            double bottom = y + RowHeight - 1;
            bool selectedRun = ReferenceEquals(run, _selected);

            for (int s = 0; s < segments.Count; s++)
            {
                LineSegment seg = segments[s];

                double sx = seg.StartMs * _pxPerMs;
                double sw = (seg.EndMs - seg.StartMs) * _pxPerMs;

                // Nothing to divide on the left of the first segment.
                if (s > 0)
                {
                    // Half pixel shift so a 1px line lands on a whole pixel.
                    double lineX = Math.Round(sx) + 0.5;
                    dc.DrawLine(DividerPen, new Point(lineX, top), new Point(lineX, bottom));
                }

                if (selectedRun && s == _selectedSegmentIndex)
                {
                    Rect selectedRect = new Rect(sx, top, Math.Max(sw - 1, 0.5), bottom - top);
                    dc.DrawRectangle(SegmentSelectionBrush, SegmentSelectionPen, selectedRect);
                }

                // A slice that begins underneath the function name has its
                // number pushed out to the right of the name rather than being
                // dropped. The name sticks to the visible edge, so on a box
                // that spans the whole screen it sits over the start of the
                // slice we are watching grow, and its number was never drawn.
                if (seg.Line > 0 && sw >= MinLineWidth)
                {
                    FormattedText lineText = LineTextFor(seg.Line, dpi);
                    double textX = Math.Max(sx + 2, nameEnd);

                    // Only if what is left of the slice can still hold it,
                    // otherwise the number would sit on top of the next one.
                    if (textX + lineText.Width <= sx + sw)
                    {
                        dc.DrawText(lineText, new Point(textX, y + 3));
                    }
                }
            }
        }

        private void DrawTimeGrid(DrawingContext dc)
        {
            double step = GridStepMs();
            if (step <= 0) return;

            // RenderSize.Height covers the ruler too now. Drawing that far down
            // would push the lines through the axis and in between the ticks.
            double height = _model.Depth * RowHeight;

            // Otherwise the lines are just noise on top of the boxes.
            dc.PushOpacity(0.55);

            for (double ms = step; ms < _model.TotalMs; ms += step)
            {
                double gx = Math.Round(ms * _pxPerMs) + 0.5;
                dc.DrawLine(GridPen, new Point(gx, 0), new Point(gx, height));
            }

            dc.Pop();
        }

        private void DrawTimeline(DrawingContext dc, double dpi)
        {
            double y = _model.Depth * RowHeight;
            dc.DrawLine(AxisPen, new Point(0, y), new Point(RenderSize.Width, y));

            double step = GridStepMs();
            for (double ms = 0; ms <= _model.TotalMs; ms += step)
            {
                double x = Math.Round(ms * _pxPerMs) + 0.5;
                dc.DrawLine(AxisPen, new Point(x, y), new Point(x, y + 6));
                dc.DrawText(TickTextFor((int)ms, dpi), new Point(x + 3, y + 8));
            }
        }

        // With a fixed 100 ms the lines would either pile up on each other or
        // leave a single line on the screen as you zoom. The gap is kept constant
        // in pixels instead.
        private double GridStepMs()
        {
            double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500,
                               1000, 2000, 5000, 10000, 30000, 60000 };

            double target = TargetGridPx / _pxPerMs;

            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] >= target) return steps[i];
            }

            return steps[steps.Length - 1];
        }

        // ====================================================================
        // Mouse and selection
        // ====================================================================

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            Point p = e.GetPosition(this);

            _selected = HitTest(p);
            _selectedSegmentIndex = _selected == null ? -1 : SegmentIndexAt(_selected, p.X / _pxPerMs);

            InvalidateVisual();   // for the outline
            RaiseSelection();
            if (e.ClickCount == 2 && _selected != null)
            {
                Action<FlameSelectionInfo> nav = NavigateRequested;
                if (nav != null) nav(BuildSelectionInfo());
            }
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            // Ctrl + wheel is horizontal zoom. Without Ctrl leave it to the
            // ScrollViewer.
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            PixelsPerMs = e.Delta > 0 ? _pxPerMs * 1.25 : _pxPerMs / 1.25;
            e.Handled = true;
        }

        private FlameRun HitTest(Point p)
        {
            if (_model == null) return null;

            int depth = (int)(p.Y / RowHeight);
            if (depth < 0 || depth >= _model.Levels.Count) return null;

            double ms = p.X / _pxPerMs;
            List<FlameRun> runs = _model.Levels[depth];

            // Runs are sorted by StartMs, so a binary search gives us the run
            // that contains ms, or the one just left of it.
            int lo = 0, hi = runs.Count - 1, found = -1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;

                if (runs[mid].StartMs <= ms) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            if (found < 0) return null;

            return runs[found].EndMs >= ms ? runs[found] : null;
        }

        // Called from the timer. The selected run keeps growing while sampling:
        // same box, but the duration, the end and the line slice change. The
        // selection is held by reference so there is no need to hit-test again,
        // just publishing the info once more is enough.
        public void RefreshSelection()
        {
            if (_selected == null) return;

            RaiseSelection();
        }

        private void RaiseSelection()
        {
            Action<FlameSelectionInfo> handler = SelectionChanged;
            if (handler == null) return;

            handler(BuildSelectionInfo());
        }

        private FlameSelectionInfo BuildSelectionInfo()
        {
            if (_selected == null) return null;

            LineSegment seg = SelectedSegment();

            return new FlameSelectionInfo
            {
                Name = _selected.Name,
                StartMs = _selected.StartMs,
                EndMs = _selected.EndMs,
                Line = seg == null ? 0 : seg.Line,
                File = seg == null ? null : seg.File,
                LineStartMs = seg == null ? 0 : seg.StartMs,
                LineEndMs = seg == null ? 0 : seg.EndMs
            };
        }

        private LineSegment SelectedSegment()
        {
            if (_selected == null) return null;
            if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _selected.Lines.Count) return null;

            return _selected.Lines[_selectedSegmentIndex];
        }

        // ====================================================================
        // Caches and resources
        // ====================================================================

        private FormattedText NameTextFor(string name, double dpi)
        {
            FormattedText text;
            if (_nameCache.TryGetValue(name, out text)) return text;

            // Building a FormattedText is expensive, so one per name and not one
            // per box.
            text = new FormattedText(name, CultureInfo.CurrentUICulture,
                                     FlowDirection.LeftToRight, Face,
                                     NameFontSize, NameBrush, dpi);

            _nameCache[name] = text;
            return text;
        }

        private FormattedText LineTextFor(int lineNo, double dpi)
        {
            FormattedText text;
            if (_lineCache.TryGetValue(lineNo, out text)) return text;

            text = new FormattedText(lineNo.ToString(CultureInfo.InvariantCulture),
                                     CultureInfo.InvariantCulture,
                                     FlowDirection.LeftToRight, Face,
                                     LineFontSize, LineNoBrush, dpi);

            _lineCache[lineNo] = text;
            return text;
        }

        private FormattedText TickTextFor(int ms, double dpi)
        {
            FormattedText text;
            if (_tickCache.TryGetValue(ms, out text)) return text;

            text = new FormattedText(ms.ToString(CultureInfo.InvariantCulture) + " ms",
                                     CultureInfo.InvariantCulture,
                                     FlowDirection.LeftToRight, Face,
                                     10, TextBrush, dpi);

            _tickCache[ms] = text;
            return text;
        }

        private static int SegmentIndexAt(FlameRun run, double ms)
        {
            for (int i = 0; i < run.Lines.Count; i++)
            {
                LineSegment seg = run.Lines[i];
                if (ms >= seg.StartMs && ms <= seg.EndMs) return i;
            }
            return -1;
        }

        private static Brush BrushFor(string name)
        {
            // FNV-1a instead of string.GetHashCode, which can change from one run
            // to the next. This way a function gets the same tone every time.
            Brush brush;
            if (_brushCache.TryGetValue(name, out brush)) return brush;
            uint hash = 2166136261;
            for (int i = 0; i < name.Length; i++)
            {
                hash = (hash ^ name[i]) * 16777619;
            }
            double hue = 45 + (hash % 20);
            double saturation = 0.45 + ((hash >> 8) % 15) / 100.0;
            double value = 0.85 + ((hash >> 16) % 10) / 100.0;
            brush = new SolidColorBrush(FromHsv(hue, saturation, value));
            brush.Freeze();
            _brushCache[name] = brush;
            return brush;
        }

        private static Color FromHsv(double hue, double sat, double val)
        {
            double c = val * sat;
            double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
            double m = val - c;
            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private static Brush Frozen(Color color)
        {
            // Freeze matters: an unfrozen Brush costs a thread affinity check and
            // a change notification on every single draw.
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen MakeGridPen()
        {
            // Solid line: DashStyle was splitting every line into pieces and
            // building separate geometry, which showed up in the frame time once
            // there were a few hundred lines.
            var pen = new Pen(Frozen(Color.FromRgb(110, 110, 110)), 1);
            pen.Freeze();   // Freeze goes down to the inner Freezables too
            return pen;
        }

        private static Pen MakeDividerPen()
        {
            var pen = new Pen(Frozen(Color.FromArgb(90, 0, 0, 0)), 1);
            pen.Freeze();
            return pen;
        }

        private static Pen MakeSelectionPen()
        {
            var pen = new Pen(Frozen(Color.FromRgb(20, 20, 20)), 2);
            pen.Freeze();
            return pen;
        }

        private static Pen MakeAxisPen()
        {
            var pen = new Pen(Frozen(Color.FromRgb(0, 0, 0)), 1);
            pen.Freeze();
            return pen;
        }
        private static Pen MakeSegmentSelectionPen()
        {
            var pen = new Pen(Frozen(Color.FromRgb(0, 0, 0)), 2);
            pen.Freeze();
            return pen;
        }

    }
}