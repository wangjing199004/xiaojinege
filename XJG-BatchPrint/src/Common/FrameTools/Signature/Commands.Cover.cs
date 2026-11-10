#nullable disable
#if AUTOCAD
#if ACAD_CORE
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
using CadDatabase = Autodesk.AutoCAD.DatabaseServices.Database;
using CadRuntime = Autodesk.AutoCAD.Runtime;
#else
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadDatabase = ZwSoft.ZwCAD.DatabaseServices.Database;
using CadRuntime = ZwSoft.ZwCAD.Runtime;
#endif
namespace CadSignature
{
    public partial class Commands
    {
        private static string[] CoverRoles()
        {
            var roles = new System.Collections.Generic.List<string>();
            foreach (string role in Roles) if (role != "制图") roles.Add(role);
            return roles.ToArray();
        }
    }
}
