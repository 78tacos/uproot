using System.Runtime.InteropServices;
using System.Text;

namespace Uproot.Core;

/// <summary>
/// The slice of msi.dll the uninstaller needs: product state, component paths and clients, and
/// read-only queries against a package's cached database.
///
/// Everything here reads. Nothing opens a database for writing or asks Windows Installer to act,
/// so it is safe to call on any product, installed or not, from an unelevated process.
/// </summary>
internal static class MsiInterop
{
    public const int InstallStateLocal = 3;
    public const int InstallStateSource = 4;
    public const int InstallStateDefault = 5;
    public const int InstallStateAdvertised = 1;
    public const int InstallStateMoreData = -3;

    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const int MsiNullInteger = unchecked((int)0x80000000);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductStateW(string product);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder? value, ref int length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder? value, ref int length);

    [DllImport("msi.dll")]
    private static extern int MsiRecordGetInteger(IntPtr record, uint field);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathW(string product, string component, StringBuilder? path, ref int length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumClientsW(string component, uint index, StringBuilder product);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumPatchesW(string product, uint index, StringBuilder patch, StringBuilder transforms, ref int transformsLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder product);

    /// <summary>INSTALLSTATE for a product: 5 (default) when installed, -1 when unknown to Windows.</summary>
    public static int QueryProductState(string productCode)
    {
        try { return MsiQueryProductStateW(productCode); }
        catch (DllNotFoundException) { return -1; }
    }

    public static bool IsProductInstalled(string productCode) =>
        QueryProductState(productCode) == InstallStateDefault;

    public static string? GetProductInfo(string productCode, string property)
    {
        try
        {
            var length = 0;
            var result = MsiGetProductInfoW(productCode, property, null, ref length);
            if (result != ErrorSuccess && result != ErrorMoreData) return null;

            length++;
            var buffer = new StringBuilder(length);
            result = MsiGetProductInfoW(productCode, property, buffer, ref length);
            return result == ErrorSuccess ? NullIfEmpty(buffer.ToString()) : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a component's key path is installed for a product, and in what state. A registry key
    /// path comes back encoded as "02:\SOFTWARE\...", which <see cref="MsiRegistryPath"/> decodes.
    /// </summary>
    public static (int State, string? Path) GetComponentPath(string productCode, string componentId)
    {
        var length = 512;
        var buffer = new StringBuilder(length);
        var state = MsiGetComponentPathW(productCode, componentId, buffer, ref length);

        if (state == InstallStateMoreData)
        {
            length++;
            buffer = new StringBuilder(length);
            state = MsiGetComponentPathW(productCode, componentId, buffer, ref length);
        }

        return (state, NullIfEmpty(buffer.ToString()));
    }

    /// <summary>Every product that has this component installed.</summary>
    public static List<string> EnumClients(string componentId)
    {
        var clients = new List<string>();
        var buffer = new StringBuilder(39);

        for (uint i = 0; i < 64; i++)
        {
            buffer.Clear();
            if (MsiEnumClientsW(componentId, i, buffer) != ErrorSuccess) break;
            clients.Add(buffer.ToString());
        }

        return clients;
    }

    public static int CountPatches(string productCode)
    {
        var count = 0;
        var patch = new StringBuilder(39);

        for (uint i = 0; i < 256; i++)
        {
            var transformsLength = 1024;
            var transforms = new StringBuilder(transformsLength);
            patch.Clear();

            var result = MsiEnumPatchesW(productCode, i, patch, transforms, ref transformsLength);
            if (result != ErrorSuccess) break;
            count++;
        }

        return count;
    }

    public static List<string> EnumRelatedProducts(string upgradeCode)
    {
        var products = new List<string>();
        var buffer = new StringBuilder(39);

        for (uint i = 0; i < 64; i++)
        {
            buffer.Clear();
            if (MsiEnumRelatedProductsW(upgradeCode, 0, i, buffer) != ErrorSuccess) break;
            products.Add(buffer.ToString());
        }

        return products;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ---- Database ------------------------------------------------------------

    /// <summary>A package database opened read-only.</summary>
    public sealed class Database : IDisposable
    {
        private IntPtr _handle;

        private Database(IntPtr handle) => _handle = handle;

        /// <summary>Opens read-only (MSIDBOPEN_READONLY). Null when the file cannot be opened.</summary>
        public static Database? Open(string path)
        {
            try
            {
                return MsiOpenDatabaseW(path, IntPtr.Zero, out var handle) == ErrorSuccess
                    ? new Database(handle)
                    : null;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs a query and returns each row as an array of fields. Integer columns come back as
        /// their decimal text; a query against a table the package does not have returns nothing.
        /// </summary>
        public List<string?[]> Query(string sql, int columns)
        {
            var rows = new List<string?[]>();
            if (MsiDatabaseOpenViewW(_handle, sql, out var view) != ErrorSuccess) return rows;

            try
            {
                if (MsiViewExecute(view, IntPtr.Zero) != ErrorSuccess) return rows;

                while (MsiViewFetch(view, out var record) == ErrorSuccess)
                {
                    try
                    {
                        var row = new string?[columns];
                        for (uint field = 1; field <= columns; field++) row[field - 1] = ReadString(record, field);
                        rows.Add(row);
                    }
                    finally
                    {
                        MsiCloseHandle(record);
                    }
                }
            }
            finally
            {
                MsiCloseHandle(view);
            }

            return rows;
        }

        public bool HasTable(string table) =>
            Query($"SELECT `Name` FROM `_Tables` WHERE `Name`='{table}'", 1).Count > 0;

        private static string? ReadString(IntPtr record, uint field)
        {
            var length = 0;
            var result = MsiRecordGetStringW(record, field, new StringBuilder(1), ref length);

            if (result == ErrorMoreData || length > 0)
            {
                length++;
                var buffer = new StringBuilder(length);
                result = MsiRecordGetStringW(record, field, buffer, ref length);
                return result == ErrorSuccess ? buffer.ToString() : null;
            }

            if (result == ErrorSuccess) return string.Empty;

            var number = MsiRecordGetInteger(record, field);
            return number == MsiNullInteger ? null : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            MsiCloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ---- Registry key paths --------------------------------------------------

    /// <summary>
    /// Decodes a component key path that names a registry key: "02:\SOFTWARE\X" is HKLM, and
    /// adding 20 to the root ("22:") marks the 64-bit view. Null for anything else.
    /// </summary>
    public static (Microsoft.Win32.RegistryHive Hive, bool Is64Bit, string SubKey)? MsiRegistryPath(string? keyPath)
    {
        if (keyPath is null || keyPath.Length < 4 || keyPath[2] != ':' ||
            !char.IsDigit(keyPath[0]) || !char.IsDigit(keyPath[1]))
        {
            return null;
        }

        var code = (keyPath[0] - '0') * 10 + (keyPath[1] - '0');
        var is64 = code >= 20;
        var root = code % 20;

        Microsoft.Win32.RegistryHive? hive = root switch
        {
            0 => Microsoft.Win32.RegistryHive.ClassesRoot,
            1 => Microsoft.Win32.RegistryHive.CurrentUser,
            2 => Microsoft.Win32.RegistryHive.LocalMachine,
            3 => Microsoft.Win32.RegistryHive.Users,
            _ => null
        };

        if (hive is null) return null;

        var subKey = keyPath[3..].Trim('\\');
        return (hive.Value, is64, subKey);
    }
}
