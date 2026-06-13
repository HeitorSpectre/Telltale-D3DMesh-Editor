using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TelltaleD3DMeshEditor.Core;

public static class FileAssociationService
{
    private const int ShcneAssocChanged = 0x08000000;
    private const int ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static void RegisterD3DMeshAssociation()
        => RegisterAssociation(
            ".d3dmesh",
            "TelltaleD3DMeshEditor.d3dmesh",
            "Telltale D3DMesh file",
            EmbeddedIconResources.D3DMesh,
            "d3dmesh.ico");

    public static void RegisterSkeletonAssociation()
        => RegisterAssociation(
            ".skl",
            "TelltaleD3DMeshEditor.skl",
            "Telltale Skeleton file",
            EmbeddedIconResources.Skeleton,
            "skl.ico");

    private static void RegisterAssociation(
        string extension,
        string progId,
        string description,
        string iconResource,
        string iconFileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var exePath = Application.ExecutablePath;
            var iconPath = EmbeddedIconResources.ExtractToLocalCache(iconResource, iconFileName) ?? exePath;

            using (var extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension))
            {
                extensionKey?.SetValue("", progId);
                extensionKey?.SetValue("Content Type", "application/octet-stream");
            }

            using (var openWithKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgIds"))
            {
                openWithKey?.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
            {
                progIdKey?.SetValue("", description);
            }

            using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\DefaultIcon"))
            {
                iconKey?.SetValue("", $"\"{iconPath}\"");
            }

            using (var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command"))
            {
                commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");
            }

            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Could not register {extension} file association");
        }
    }
}
