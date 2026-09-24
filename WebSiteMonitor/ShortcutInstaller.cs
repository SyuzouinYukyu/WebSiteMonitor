using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WebSiteMonitor;

internal static class ShortcutInstaller
{
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static void Create(string exePath, string shortcutPath, string appUserModelId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var linkObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
        var link = (IShellLinkW)linkObject;
        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetDescription("WebSiteMonitor");
            link.SetIconLocation(exePath, 0);
            var store = (IPropertyStore)link;
            var value = new PropVariant { ValueType = (ushort)VarEnum.VT_LPWSTR, PointerValue = Marshal.StringToCoTaskMemUni(appUserModelId) };
            try
            {
                var key = AppUserModelIdKey;
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally { Marshal.FreeCoTaskMem(value.PointerValue); }
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally { Marshal.FinalReleaseComObject(linkObject); }
    }

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void InitPropVariantFromString(string value, out PropVariant propVariant);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr data, uint flags);
        void GetIDList(out IntPtr pidl); void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey); void GetShowCmd(out int showCmd); void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved); void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        uint GetCount(); PropertyKey GetAt(uint propertyIndex); int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value); int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId) { public readonly Guid FormatId = formatId; public readonly uint PropertyId = propertyId; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort ValueType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }
}