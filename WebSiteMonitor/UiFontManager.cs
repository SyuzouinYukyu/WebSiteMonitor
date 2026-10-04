using System.Runtime.CompilerServices;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal static class UiFontSettings
{
    public const double MinimumSize = 10.0;
    public const double MaximumSize = 18.0;
    public const double DefaultSize = 10.0;

    public static double Clamp(double value)
        => double.IsFinite(value) ? Math.Clamp(value, MinimumSize, MaximumSize) : DefaultSize;
}

/// <summary>Applies the persisted application font without timers or per-control wheel handlers.</summary>
internal static class UiFontManager
{
    private sealed record FontBaseline(string Family, float Size, FontStyle Style);

    private static readonly ConditionalWeakTable<Control, FontBaseline> ControlFonts = new();
    private static readonly Dictionary<(string Family, float Size, FontStyle Style), Font> Fonts = [];
    private static readonly List<WeakReference<ToolStrip>> RegisteredStrips = [];
    private static double _currentSize = UiFontSettings.DefaultSize;

    public static double CurrentSize => _currentSize;

    public static void Initialize(AppSettings settings)
    {
        settings.UiFontSize = UiFontSettings.Clamp(settings.UiFontSize);
        _currentSize = settings.UiFontSize;
    }

    public static bool Change(AppSettings settings, int detents, Action<AppSettings> save)
    {
        if (detents == 0) return false;
        var current = UiFontSettings.Clamp(settings.UiFontSize);
        var next = UiFontSettings.Clamp(current + Math.Sign(detents));
        if (Math.Abs(next - current) < 0.001) return false;
        settings.UiFontSize = next;
        _currentSize = next;
        save(settings);
        ApplyToOpenForms(next);
        return true;
    }

    public static void Apply(Form form, double requestedSize)
    {
        var size = UiFontSettings.Clamp(requestedSize);
        _currentSize = size;
        CaptureBaselines(form);
        form.SuspendLayout();
        try
        {
            ApplyFont(form, size);
            foreach (var control in Descendants(form)) ApplyFont(control, size);
            foreach (var strip in Descendants(form).OfType<ToolStrip>()) ApplyToolStrip(strip, size, form.Font);
            foreach (var grid in Descendants(form).OfType<DataGridView>()) RefreshGridMetrics(grid);
            RecalculateLayouts(form);
        }
        finally
        {
            form.ResumeLayout(true);
            form.PerformLayout();
            ScrollableDialogLayout.Refresh(form);
        }
    }

    public static void Register(ToolStrip strip, double requestedSize)
    {
        RegisteredStrips.RemoveAll(reference => !reference.TryGetTarget(out _));
        if (!RegisteredStrips.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, strip)))
            RegisteredStrips.Add(new WeakReference<ToolStrip>(strip));
        ApplyToolStrip(strip, UiFontSettings.Clamp(requestedSize));
    }

    private static void ApplyToOpenForms(double size)
    {
                foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (!form.IsDisposed) Apply(form, size);
        }
        foreach (var reference in RegisteredStrips.ToArray())
        {
            if (reference.TryGetTarget(out var strip) && !strip.IsDisposed) ApplyToolStrip(strip, size);
        }
    }

    private static void CaptureBaselines(Form form)
    {
        CaptureBaseline(form);
        foreach (var control in Descendants(form)) CaptureBaseline(control);
    }

    private static void CaptureBaseline(Control control)
    {
        if (!ControlFonts.TryGetValue(control, out _)) ControlFonts.Add(control, new FontBaseline(control.Font.FontFamily.Name, control.Font.SizeInPoints, control.Font.Style));
    }

    private static void ApplyFont(Control control, double size)
    {
        var baseline = ControlFonts.GetValue(control, current => new FontBaseline(current.Font.FontFamily.Name, current.Font.SizeInPoints, current.Font.Style));
        control.Font = GetFont(baseline, size);
    }

    private static void ApplyToolStrip(ToolStrip strip, double size, Font? inheritedFont = null)
    {
        // Lazy dropdowns already inherit the zoomed owner font. Never treat that
        // inherited value (or the OS menu font) as another 9pt scaling baseline.
        strip.Font = inheritedFont ?? GetFont(new FontBaseline("Yu Gothic UI", 9F, FontStyle.Regular), size);
        foreach (ToolStripItem item in strip.Items)
        {
            item.Font = strip.Font;
            if (item is ToolStripDropDownItem { DropDown: { } dropDown }) ApplyToolStrip(dropDown, size, item.Font);
        }
        strip.PerformLayout();
    }
    private static Font GetFont(FontBaseline baseline, double size)
    {
        var adjusted = Math.Max(6F, (float)(size + (baseline.Size - 9F)));
        var key = (baseline.Family, adjusted, baseline.Style);
        if (!Fonts.TryGetValue(key, out var font))
        {
            font = new Font(baseline.Family, adjusted, baseline.Style, GraphicsUnit.Point);
            Fonts.Add(key, font);
        }
        return font;
    }

    private static void RefreshGridMetrics(DataGridView grid)
    {
        grid.DefaultCellStyle.Font = grid.Font;
        grid.ColumnHeadersDefaultCellStyle.Font = grid.Font;
        grid.RowsDefaultCellStyle.Font = grid.Font;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            var required = TextRenderer.MeasureText(column.HeaderText, grid.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 16;
            if (column.Name == "State" && column.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill)
            {
                required = new[] { column.HeaderText, "未確認", "正常", "エラー（9999回）" }
                    .Max(value => TextRenderer.MeasureText(value, grid.Font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width) + 20;
                column.MinimumWidth = required;
            }
            else column.MinimumWidth = Math.Max(column.MinimumWidth, required);
            column.Width = Math.Max(column.Width, column.MinimumWidth);
        }
        grid.AutoResizeColumnHeadersHeight();
        grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
    }

    private static void RecalculateLayouts(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.PerformLayout();
            RecalculateLayouts(control);
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

internal sealed class FontZoomMessageFilter : IMessageFilter, IDisposable
{
    private const int WmKeyDown = 0x0100;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<AppSettings> _saveSettings;
    private bool _disposed;

    public FontZoomMessageFilter(Func<AppSettings> getSettings, Action<AppSettings> saveSettings)
    {
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        Application.AddMessageFilter(this);
    }

    public bool PreFilterMessage(ref Message message)
    {
        return HandleMessage(ref message, Control.ModifierKeys);
    }

    internal bool HandleMessage(ref Message message, Keys modifiers)
    {
        // Wheel, key-up, IME composition and unrelated shortcuts keep their original route.
        if (_disposed || message.Msg != WmKeyDown) return false;
        var target = Control.FromHandle(message.HWnd);
        if (target?.FindForm() is null) return false;
        var step = ZoomStep((Keys)message.WParam.ToInt32(), modifiers);
        if (step == 0) return false;
        UiFontManager.Change(_getSettings(), step, _saveSettings);
        return true;
    }

    internal static int ZoomStep(Keys key, Keys modifiers)
    {
        if ((modifiers & Keys.Control) == 0 || (modifiers & Keys.Alt) != 0) return 0;
        return key switch { Keys.Oemplus or Keys.Add => 1, Keys.OemMinus or Keys.Subtract => -1, _ => 0 };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Application.RemoveMessageFilter(this);
    }
}
