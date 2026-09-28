using System;
using System.Diagnostics;

namespace MenuPrio
{
    internal static class Launcher
    {
        public static bool Launch(Candidate candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Target))
            {
                Log.Warn("launcher: nothing to launch");
                return false;
            }

            var psi = new ProcessStartInfo();
            psi.FileName = candidate.Target;
            psi.Arguments = candidate.Args ?? "";
            if (!string.IsNullOrEmpty(candidate.WorkDir)) psi.WorkingDirectory = candidate.WorkDir;
            psi.UseShellExecute = true;

            try
            {
                var proc = Process.Start(psi);
                Log.Info("launched: " + candidate.Target
                    + (string.IsNullOrEmpty(candidate.Args) ? "" : " " + candidate.Args)
                    + (proc != null ? " (pid " + proc.Id + ")" : ""));
                if (proc != null) proc.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("launcher: failed to start " + candidate.Target, ex);
                return false;
            }
        }
    }
}
