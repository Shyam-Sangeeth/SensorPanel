using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SensorPanel.Sensors;

/// <summary>
/// Current framerate from RivaTuner Statistics Server's shared memory ("RTSSSharedMemoryV2", the interface its SDK
/// documents for other programs). Uses the game in the foreground, otherwise the most recently updated one.
/// </summary>
internal static class RtssFps
{
    private const uint Signature = 0x52545353; // "RTSS"

    /// <summary>The framerate, optionally only of apps <paramref name="include"/> accepts (by process ID).</summary>
    public static double? Read(Func<int, bool>? include = null)
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Signature) return null;

            uint entrySize = view.ReadUInt32(8);
            uint arrayOffset = view.ReadUInt32(12);
            uint count = view.ReadUInt32(16);
            if (entrySize < 284 || count == 0) return null;

            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
            double? best = null;
            uint newest = 0;
            for (uint i = 0; i < count; i++)
            {
                long entry = arrayOffset + (long)i * entrySize;
                uint pid = view.ReadUInt32(entry);
                if (pid == 0 || (include != null && !include((int)pid))) continue;
                uint time0 = view.ReadUInt32(entry + 268);
                uint time1 = view.ReadUInt32(entry + 272);
                uint frames = view.ReadUInt32(entry + 276);
                if (time1 <= time0 || frames == 0) continue;
                double fps = 1000.0 * frames / (time1 - time0);
                if (pid == foregroundPid) return fps;
                if (time1 > newest)
                {
                    newest = time1;
                    best = fps;
                }
            }
            // Ignore apps that stopped presenting frames a while ago.
            return best != null && unchecked((uint)Environment.TickCount - newest) < 3000 ? best : 0;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
