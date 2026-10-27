using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Checkers.EngineWorker.Native;

/// <summary>
/// KingsRow writes its log to "&lt;Documents&gt;\Ed Gilbert\Kingsrow\Kingsrow.log", found with
/// SHGetFolderPathA(CSIDL_PERSONAL), as soon as it starts. If that folder cannot be used, the
/// log file does not open and the engine crashes (0xc0000409, CRT fast-fail).
///
/// That is the case for an IIS app pool's virtual account ("IIS AppPool\CheckersApi"): it has a
/// profile, but no usable Documents folder. So before the DLL is loaded, check that Documents
/// works for this account; if not, point this account's Documents folder at a fallback folder
/// (registry, HKCU = the pool account's own profile; a normal user's settings are never changed
/// because for them the check passes).
/// </summary>
internal static class EngineHome
{
    private const int CSIDL_PERSONAL = 0x0005;
    private const int CSIDL_FLAG_CREATE = 0x8000;
    private const string UserShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    private const string ShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

    /// Returns a line for the diagnostics log describing what was found or changed.
    public static string Ensure(string? fallbackDir)
    {
        string? documents = Query(out int hr);
        if (documents is not null && IsWritable(documents))
            return $"engine log folder: {documents}";

        string problem = documents is null ? $"Documents folder unavailable (HRESULT 0x{hr:X8})" : $"Documents folder not writable: {documents}";
        if (string.IsNullOrWhiteSpace(fallbackDir))
            return problem + "; no fallback folder given, the engine may crash";

        string fallback = Path.GetFullPath(fallbackDir);
        Directory.CreateDirectory(fallback);
        using (var key = Registry.CurrentUser.CreateSubKey(UserShellFolders))
            key.SetValue("Personal", fallback, RegistryValueKind.ExpandString);
        using (var key = Registry.CurrentUser.CreateSubKey(ShellFolders))
            key.SetValue("Personal", fallback, RegistryValueKind.String);

        string? now = Query(out hr);
        return $"{problem}; Documents for {Environment.UserName} set to {fallback} (now resolves to {now ?? $"error 0x{hr:X8}"})";
    }

    private static string? Query(out int hr)
    {
        var sb = new StringBuilder(260);
        hr = SHGetFolderPathW(IntPtr.Zero, CSIDL_PERSONAL | CSIDL_FLAG_CREATE, IntPtr.Zero, 0, sb);
        return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, "Ed Gilbert", "Kingsrow");
            Directory.CreateDirectory(probe);
            string file = Path.Combine(probe, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(file, "");
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetFolderPathW(IntPtr hwnd, int csidl, IntPtr hToken, uint flags, StringBuilder path);
}
