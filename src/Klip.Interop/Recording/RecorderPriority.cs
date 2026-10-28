using System.Diagnostics;

namespace Klip.Interop.Recording;

/// <summary>
/// Priority of the recorder process (Klip.exe --recorder). The main app stays on EcoQoS;
/// this process is short lived and has a deadline every frame, so it asks for more.
/// </summary>
public static class RecorderPriority
{
    /// <summary>
    /// Above normal cpu class, no EcoQoS, above normal gpu scheduling class. Each step is
    /// best effort and the result goes back as one line for the log.
    /// </summary>
    public static string RaiseCurrentProcess()
    {
        string cpu;
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.AboveNormal;
            cpu = "ok";
        }
        catch (Exception ex)
        {
            cpu = ex.Message;
        }

        // the parent sits on EcoQoS; say explicitly that this one should not
        bool highQos = PowerEfficiency.EnterProcessHighQos();
        PowerEfficiency.IgnoreTimerResolutionRequests(false);

        // REALTIME needs SeIncreaseBasePriorityPrivilege (admin) and can starve the game
        // being recorded. ABOVE_NORMAL needs nothing and is enough to beat the desktop
        int gpuStatus;
        try
        {
            gpuStatus = NativeMethods.D3DKMTSetProcessSchedulingPriorityClass(
                -1, NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_ABOVE_NORMAL);
        }
        catch (Exception)
        {
            gpuStatus = -1;
        }

        return $"cpu=AboveNormal({cpu}) highQos={highQos} gpuClass=AboveNormal(0x{gpuStatus:X8})";
    }
}
