using System;
using System.Runtime.InteropServices;
using HddSpindown.Windows;

namespace HddSpindown.Service {
 // SCM failure actions: restart after 5 s, 30 s, 120 s, then explicitly no further
 // action, so Windows does not repeat the last restart forever (concept section 4).
 static class ServiceRecovery {
  [StructLayout(LayoutKind.Sequential)] struct ScAction { public int Type; public uint Delay; }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct FailureActions { public uint ResetPeriod; public string RebootMsg; public string Command; public uint Count; public IntPtr Actions; }
  [StructLayout(LayoutKind.Sequential)] struct FailureActionsFlag { public int OnNonCrashFailures; }
  [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr OpenSCManager(string machine,string db,uint access);
  [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr OpenService(IntPtr scm,string name,uint access);
  [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool ChangeServiceConfig2(IntPtr service,int level,ref FailureActions info);
  [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool ChangeServiceConfig2(IntPtr service,int level,ref FailureActionsFlag info);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool CloseServiceHandle(IntPtr h);
  const int SC_ACTION_NONE=0, SC_ACTION_RESTART=1, SERVICE_CONFIG_FAILURE_ACTIONS=2, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG=4;
  const uint SC_MANAGER_CONNECT=1, SERVICE_CHANGE_CONFIG=2, SERVICE_START=0x10;

  public static void Configure() {
   IntPtr scm=OpenSCManager(null,null,SC_MANAGER_CONNECT); if(scm==IntPtr.Zero) { Log.Warn("Dienst-Wiederanlauf nicht konfiguriert: SCM nicht erreichbar."); return; }
   try {
    IntPtr svc=OpenService(scm,Paths.ServiceName,SERVICE_CHANGE_CONFIG|SERVICE_START); if(svc==IntPtr.Zero) { Log.Warn("Dienst-Wiederanlauf nicht konfiguriert: Dienst nicht gefunden."); return; }
    try {
     var actions=new[]{new ScAction{Type=SC_ACTION_RESTART,Delay=5000},new ScAction{Type=SC_ACTION_RESTART,Delay=30000},new ScAction{Type=SC_ACTION_RESTART,Delay=120000},new ScAction{Type=SC_ACTION_NONE,Delay=0}};
     int size=Marshal.SizeOf(typeof(ScAction)); IntPtr buffer=Marshal.AllocHGlobal(size*actions.Length);
     try {
      for(int i=0;i<actions.Length;i++) Marshal.StructureToPtr(actions[i],buffer+i*size,false);
      var info=new FailureActions {ResetPeriod=86400,RebootMsg=null,Command=null,Count=(uint)actions.Length,Actions=buffer};
      if(!ChangeServiceConfig2(svc,SERVICE_CONFIG_FAILURE_ACTIONS,ref info)) Log.Warn("Dienst-Wiederanlauf: Windows-Fehler "+Marshal.GetLastWin32Error());
      var flag=new FailureActionsFlag {OnNonCrashFailures=1};
      ChangeServiceConfig2(svc,SERVICE_CONFIG_FAILURE_ACTIONS_FLAG,ref flag);
     } finally { Marshal.FreeHGlobal(buffer); }
    } finally { CloseServiceHandle(svc); }
   } finally { CloseServiceHandle(scm); }
  }
 }
}
