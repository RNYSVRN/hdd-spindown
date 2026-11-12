using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HddSpindown.Windows {
 public static class Paths {
  public const string ServiceName="HddSpindown";
  public const string ServiceDisplayName="HDD Spindown";
  public const string EventSource="HDD Spindown";
  public static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"HDD Spindown"); } }
  public static string StateDir { get { return Path.Combine(DataRoot,"state"); } }
  public static string LogDir { get { return Path.Combine(DataRoot,"logs"); } }
  public static string ExportDir { get { return Path.Combine(DataRoot,"exports"); } }

  // SYSTEM and Administrators only; no inheritance from ProgramData (which grants Users read).
  public static void EnsureProtectedDirectory(string path) {
   Directory.CreateDirectory(path);
   var security=new DirectorySecurity();
   security.SetAccessRuleProtection(true,false);
   var inherit=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
   security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
   security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
   security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
   new DirectoryInfo(path).SetAccessControl(security);
  }
  public static string ShortSerial(string serial) {
   if(String.IsNullOrEmpty(serial)) return "";
   return serial.Length<=4?"****":"…"+serial.Substring(serial.Length-4);
  }
 }
}
