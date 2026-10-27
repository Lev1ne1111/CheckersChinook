using System.Runtime.InteropServices;

namespace Checkers.EngineWorker.Native;

/// <summary>
/// C# version of the CheckerBoard engine interface (cb_interface.h, API version 2).
/// Every engine DLL for CheckerBoard, KingsRow included, exports these functions.
/// </summary>
internal static class Cb
{
    // Piece codes stored in the 8x8 board array
    public const int Free  = 0;
    public const int White = 1;
    public const int Black = 2;
    public const int Man   = 4;
    public const int King  = 8;

    // Return values of getmove()
    public const int Draw    = 0;
    public const int Win     = 1;
    public const int Loss    = 2;
    public const int Unknown = 3;

    // Bits of the 'info' argument of getmove()
    public const int ResetMoves = 1;
    public const int ExactTime  = 2;

    public const int StatusSize  = 1024;  // char str[1024]
    public const int ReplySize   = 1024;  // enginecommand reply buffer
    public const int CommandSize = 256;   // enginecommand command buffer

    // sizeof(CBmove): 3 ints + from + to + path[12] + del[12] + delpiece[12]
    public const int CbMoveSize = 3 * 4 + 2 * 8 + 12 * 8 + 12 * 8 + 12 * 4;   // 268 bytes

    public static string ResultName(int r) => r switch
    {
        Draw => "DRAW", Win => "WIN", Loss => "LOSS", Unknown => "UNKNOWN", _ => $"?{r}"
    };
}

// All pointers are IntPtr on purpose: the buffers live in unmanaged memory
// (Marshal.AllocHGlobal), so the GC never moves them while the engine writes into them
// from its own search, and another thread can read 'str' / write 'playnow' at the same time.

/// int WINAPI getmove(int board[8][8], int color, double maxtime, char str[1024],
///                    int *playnow, int info, int moreinfo, CBmove *move);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetMoveFn(IntPtr board, int color, double maxtime, IntPtr str,
                                IntPtr playnow, int info, int moreinfo, IntPtr move);

/// int WINAPI enginecommand(char command[256], char reply[1024]);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int EngineCommandFn(IntPtr command, IntPtr reply);

/// <summary>
/// Square number (1..32) &lt;-&gt; board[x][y] for English checkers.
/// Same formulas as numbertocoors()/coorstonumber() in cb_interface.h (default branch).
/// </summary>
internal static class Squares
{
    public static (int x, int y) ToCoords(int number)
    {
        int n = number - 1;
        int y = n / 4;
        int x = 2 * (3 - n % 4);
        if (y % 2 == 1) x++;
        return (x, y);
    }

    public static int ToNumber(int x, int y) => 4 * (y + 1) - x / 2;

    /// Offset in bytes of board[x][y] inside int[8][8] (C row-major: first index is the row).
    public static int ByteOffset(int x, int y) => (x * 8 + y) * sizeof(int);
}
