using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace LowLevelDesign
{
    internal sealed class DebugPrivilege : IDisposable
    {
        private static readonly Type privilegeType = Type.GetType("System.Security.AccessControl.Privilege");

        private readonly TraceSource logger;

        private object privilege;
        private bool privilegeObtained = false;

        public DebugPrivilege(TraceSource logger)
        {
            this.logger = logger;

            if (IsAdministrator()) {
                privilege = Activator.CreateInstance(privilegeType, "SeDebugPrivilege");
                // we have an elevated token so let's try to aquire the SeDebugPrivilege
                try {
                    privilegeType.GetMethod("Enable").Invoke(privilege, null);
                    privilegeObtained = true;
                    logger.TraceEvent(TraceEventType.Information, 0, "Successfully obtained SeDebugPrivilege.");
                } catch (Exception ex) {
                    logger.TraceEvent(TraceEventType.Warning, 0, "Failed to obtain the SeDebugPrivilege: {0}", ex.Message);
                }
            }
        }

        public void Dispose()
        {
            if (privilegeObtained) {
                try {
                    privilegeType.GetMethod("Revert").Invoke(privilege, null);
                } catch (Exception ex) {
                    logger.TraceEvent(TraceEventType.Error, 0, "Error while reverting the SeDebugPrivilege: {0}", ex.Message);
                }
            }
        }

        private static bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole (WindowsBuiltInRole.Administrator);
        }
    }
}
