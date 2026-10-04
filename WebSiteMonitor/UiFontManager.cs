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
    private static readonly ConditionalWeakTable<ToolStripItem, FontBaseline> ItemFonts = new();
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
                        foreach (var strip in Descendants(form).OfType<ToolStrip>()) ApplyToolStrip(strip, size);
            foreach (var grid in Descendants(form).OfType<DataGridView>()) RefreshGridMetrics(grid);
            RecalculateLayouts(form);
        }
        finally
        {
            form.ResumeLayout(true);
            form.PerformLayout();
        }
    }

    internal static bool ShouldZoom(Control? target, bool controlPressed)
    {
        if (controlPressed) return true;
        if (target is null) return false;
        return target is not DataGridView
            && target is not ListBox
            && target is not ComboBox
            && target is not TextBoxBase { Multiline: true }
            && target is not Panel
            && target is not ScrollableControl { AutoScroll: true };
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
        foreach (var item in Descendants(form).OfType<ToolStrip>().SelectMany(strip => strip.Items.Cast<ToolStripItem>())) CaptureBaseline(item);
    }

    private static void CaptureBaseline(Control control)
    {
        if (!ControlFonts.TryGetValue(control, out _)) ControlFonts.Add(control, new FontBaseline(control.Font.FontFamily.Name, control.Font.SizeInPoints, control.Font.Style));
    }

    private static void CaptureBaseline(ToolStripItem item)
    {
        if (!ItemFonts.TryGetValue(item, out _)) ItemFonts.Add(item, new FontBaseline(item.Font.FontFamily.Name, item.Font.SizeInPoints, item.Font.Style));
    }

    private static void ApplyFont(Control control, double size)
    {
        var baseline = ControlFonts.GetValue(control, current => new FontBaseline(current.Font.FontFamily.Name, current.Font.SizeInPoints, current.Font.Style));
        control.Font = GetFont(baseline, size);
    }

    private static void ApplyFont(ToolStripItem item, double size)
    {
        var baseline = ItemFonts.GetValue(item, current => new FontBaseline(current.Font.FontFamily.Name, current.Font.SizeInPoints, current.Font.Style));
        item.Font = GetFont(baseline, size);
    }

    private static void ApplyToolStrip(ToolStrip strip, double size)
    {
        CaptureBaseline(strip);
        ApplyFont(strip, size);
        foreach (ToolStripItem item in strip.Items)
        {
            CaptureBaseline(item);
            ApplyFont(item, size);
            if (item is ToolStripDropDownItem { DropDown: { } dropDown }) ApplyToolStrip(dropDown, size);
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
    private const int WmMouseWheel = 0x020A;
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
        if (_disposed || message.Msg != WmMouseWheel) return false;
        var target = Control.FromHandle(message.HWnd);
        if (target?.FindForm() is null) return false;
        var controlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;
        if (!UiFontManager.ShouldZoom(target, controlPressed)) return false;
        var delta = unchecked((short)(((long)message.WParam >> 16) & 0xffff));
        if (delta == 0) return false;
        UiFontManager.Change(_getSettings(), Math.Sign(delta), _saveSettings);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Application.RemoveMessageFilter(this);
    }
}
