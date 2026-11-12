using System.Runtime.InteropServices;
using System.Text;

namespace FolderSync;

/// <summary>
/// The encoding console tools such as schtasks and robocopy write when their output is redirected: the
/// OEM code page (437 or 850 on most Western systems). Left unset, .NET decodes it as the ANSI code page
/// or as UTF-8, which turns "José" into "Jos‚" or "Jos�".
///
/// The OEM code page cannot represent every name (a Japanese file name on a Western system comes out as
/// "??"), so output that has to round-trip a path exactly must not rely on it. Robocopy's list passes use
/// /UNILOG for that reason; this is for everything else.
/// </summary>
public static class ConsoleEncoding
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Lazy<Encoding> OemValue = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch { return Encoding.Default; }
    });

    public static Encoding Oem => OemValue.Value;
}
