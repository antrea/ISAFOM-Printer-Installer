namespace Isafom.Core;

public static class VerificationPreflight
{
    public static void Validate(PrinterSettings settings, SystemState state)
    {
        static bool Same(string a,string b) => a.Equals(b,StringComparison.OrdinalIgnoreCase);
        if(!state.SupportedWindows) throw new InstallerException(ErrorCode.UnsupportedWindows);
        if(!state.X64) throw new InstallerException(ErrorCode.UnsupportedArchitecture);
        if (!state.SpoolerRunning) throw new InstallerException(ErrorCode.SpoolerUnavailable);
        var candidates = state.Queues.Where(q => Same(q.Name,settings.PrinterName) ||
            (Same(q.DriverName,settings.DriverName) && state.Ports.Any(p => Same(p.Name,q.PortName) && Planner.IsTarget(settings,p.Host)))).ToArray();
        if(candidates.Length==0) throw new InstallerException(ErrorCode.PrinterMissing);
        if(candidates.Length>1) throw new InstallerException(ErrorCode.AmbiguousQueue);
        var queue=candidates[0];
        if(!Same(queue.Name,settings.PrinterName)||!Same(queue.DriverName,settings.DriverName)||queue.Shared||!queue.Managed)
            throw new InstallerException(ErrorCode.QueueConflict);
        var drivers=state.Drivers.Where(d=>Same(d.Name,settings.DriverName)).ToArray();
        if(drivers.Length!=1||drivers[0].Version!=settings.DriverVersion) throw new InstallerException(ErrorCode.DriverConflict);
        var ports=state.Ports.Where(p=>Same(p.Name,queue.PortName)).ToArray();
        var port=ports.Length==1?ports[0]:null;
        if(port is null||!Planner.IsTarget(settings,port.Host)||!Planner.CorrectPort(settings,port))
            throw new InstallerException(ErrorCode.PortConflict);
        if(queue.Jobs!=0) throw new InstallerException(ErrorCode.ExistingJobs);
    }
}
