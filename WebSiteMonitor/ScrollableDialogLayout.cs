using System.Runtime.CompilerServices;

namespace WebSiteMonitor;

/// <summary>Only the site editor and settings dialog use this scroll-body/fixed-footer host.</summary>
internal static class ScrollableDialogLayout
{
    private static readonly ConditionalWeakTable<Form, State> States = new();

    internal static void Install(Form form, TableLayoutPanel body, FlowLayoutPanel footer, Size minimum)
    {
        body.Name = "ScrollableBody";
        body.Dock = DockStyle.Top;
        body.AutoSize = true;
        body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        footer.Name = "FixedFooter";
        footer.Dock = DockStyle.Fill;
        footer.AutoSize = true;
        footer.Padding = new Padding(12, 6, 12, 10);
        var viewport = new Panel { Name = "BodyViewport", Dock = DockStyle.Fill, AutoScroll = true, TabIndex = 0 };
        viewport.Controls.Add(body);
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        viewport.Margin = Padding.Empty; footer.Margin = Padding.Empty; footer.TabIndex = 1;
        host.Controls.Add(viewport, 0, 0); host.Controls.Add(footer, 0, 1);
        form.Controls.Add(host);
        var state = new State(form, body, minimum);
        States.Add(form, state);
        form.Shown += (_, _) => state.Fit(Screen.FromControl(form).WorkingArea);
        form.ResizeBegin += (_, _) => state.Dragging = true;
        form.ResizeEnd += (_, _) => { state.Dragging = false; state.Fit(Screen.FromControl(form).WorkingArea); };
        form.LocationChanged += (_, _) => { if (form.Visible && !state.Dragging) state.Fit(Screen.FromControl(form).WorkingArea); };
        form.SizeChanged += (_, _) => { if (form.Visible && !state.Dragging) state.Fit(Screen.FromControl(form).WorkingArea); };
        form.DpiChanged += (_, _) =>
        {
            // Windows applies the suggested post-DPI bounds after this event.
            if (form.IsHandleCreated) form.BeginInvoke((Action)(() => { if (!form.IsDisposed) state.Fit(Screen.FromControl(form).WorkingArea); }));
        };
        body.Layout += (_, _) => state.Reflow();
        viewport.ClientSizeChanged += (_, _) => state.Reflow();
    }

    internal static void Refresh(Form form)
    {
        if (!States.TryGetValue(form, out var state)) return;
        state.Reflow();
        if (form.Visible && !state.Dragging) state.Fit(Screen.FromControl(form).WorkingArea);
    }

    internal static void FitToWorkingArea(Form form, Rectangle workingArea)
    {
        if (States.TryGetValue(form, out var state)) state.Fit(workingArea);
    }

    internal static Rectangle ConstrainBounds(Rectangle bounds, Rectangle work, Size logicalMinimum, int dpi)
    {
        var minimum = ScaledMinimum(work, logicalMinimum, dpi);
        var width = Math.Clamp(bounds.Width, minimum.Width, work.Width);
        var height = Math.Clamp(bounds.Height, minimum.Height, work.Height);
        return new Rectangle(Math.Clamp(bounds.X, work.Left, work.Right - width),
            Math.Clamp(bounds.Y, work.Top, work.Bottom - height), width, height);
    }

    private static Size ScaledMinimum(Rectangle work, Size minimum, int dpi)
        => new(Math.Min(work.Width, (int)Math.Ceiling(minimum.Width * dpi / 96.0)),
            Math.Min(work.Height, (int)Math.Ceiling(minimum.Height * dpi / 96.0)));

    private sealed class State(Form form, TableLayoutPanel body, Size minimum)
    {
        private bool _fitting, _reflowing;
        internal bool Dragging;

        internal void Fit(Rectangle work)
        {
            if (_fitting || form.IsDisposed || form.WindowState != FormWindowState.Normal) return;
            _fitting = true;
            try
            {
                var desired = form.Bounds;
                form.MinimumSize = ScaledMinimum(work, minimum, form.DeviceDpi);
                form.Bounds = ConstrainBounds(desired, work, minimum, form.DeviceDpi);
                Reflow();
            }
            finally { _fitting = false; }
        }

        internal void Reflow()
        {
            if (_reflowing || form.IsDisposed) return;
            _reflowing = true;
            try { WrapText(body); }
            finally { _reflowing = false; }
        }

        private static void WrapText(Control parent)
        {
            var widths = (parent as TableLayoutPanel)?.GetColumnWidths();
            foreach (Control control in parent.Controls)
            {
                if (control.AutoSize && control is Label or CheckBox)
                {
                    int available;
                    if (parent is TableLayoutPanel table && widths is not null)
                    {
                        var column = table.GetColumn(control); var span = table.GetColumnSpan(control);
                        if (column < 0) continue;
                        // Caption columns expand to their text; wrapping is for the
                        // percent-width fields and multi-column explanatory text.
                        if (span == 1 && column < table.ColumnStyles.Count && table.ColumnStyles[column].SizeType == SizeType.AutoSize) continue;
                        available = widths.Skip(column).Take(span).Sum() - control.Margin.Horizontal;
                    }
                    else available = parent.ClientSize.Width - parent.Padding.Horizontal - control.Margin.Horizontal;
                    if (available > 0)
                    {
                        var maximum = new Size(available, 0);
                        if (control.MaximumSize != maximum) control.MaximumSize = maximum;
                    }
                }
                WrapText(control);
            }
        }
    }
}
