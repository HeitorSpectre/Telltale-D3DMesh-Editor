using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TelltaleD3DMeshEditor.Core;

public static class FileAssociationService
{
    private const string Extension = ".d3dmesh";
    private const string ProgId = "TelltaleD3DMeshEditor.d3dmesh";
    private const string Description = "Telltale D3DMesh file";
    private const int ShcneAssocChanged = 0x08000000;
    private const int ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static void RegisterD3DMeshAssociation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var exePath = Application.ExecutablePath;
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "d3dmesh.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = exePath;
            }

            using (var extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Extension))
            {
                extensionKey?.SetValue("", ProgId);
                extensionKey?.SetValue("Content Type", "application/octet-stream");
            }

            using (var openWithKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}\OpenWithProgIds"))
            {
                openWithKey?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                progIdKey?.SetValue("", Description);
            }

            using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
            {
                iconKey?.SetValue("", $"\"{iconPath}\"");
            }

            using (var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            {
                commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");
            }

            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Could not register .d3dmesh file association");
        }
    }
}
