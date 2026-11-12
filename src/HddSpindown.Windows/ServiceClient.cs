using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using HddSpindown.Core;

namespace HddSpindown.Windows {
 public sealed class ServiceUnavailableException : Exception { public ServiceUnavailableException(string text,Exception inner=null):base(text,inner) {} }

 // One request per connection. Verifies that the pipe server process is the
 // registered HDD Spindown service (protects against a squatted pipe name).
 public static class ServiceClient {
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
  [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr OpenSCManager(string machine,string db,uint access);
  [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr OpenService(IntPtr scm,string name,uint access);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatusEx(IntPtr service,int level,byte[] buffer,int size,out int needed);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool CloseServiceHandle(IntPtr h);

  public enum ServiceState { NotInstalled, Stopped, StartPending, StopPending, Running, Other }
  public static ServiceState QueryState(out uint pid) {
   pid=0;
   IntPtr scm=OpenSCManager(null,null,1); if(scm==IntPtr.Zero) return ServiceState.Other;
   try {
    IntPtr svc=OpenService(scm,Paths.ServiceName,4); // SERVICE_QUERY_STATUS
    if(svc==IntPtr.Zero) return ServiceState.NotInstalled;
    try {
     var b=new byte[36]; int needed;
     if(!QueryServiceStatusEx(svc,0,b,b.Length,out needed)) return ServiceState.Other;
     int state=BitConverter.ToInt32(b,4); pid=BitConverter.ToUInt32(b,28);
     switch(state) { case 1: return ServiceState.Stopped; case 2: return ServiceState.StartPending; case 3: return ServiceState.StopPending; case 4: return ServiceState.Running; default: return ServiceState.Other; }
    } finally { CloseServiceHandle(svc); }
   } finally { CloseServiceHandle(scm); }
  }

  // allowConsoleServer: diagnostics build only; accepts a server not registered as service.
  public static Response Send(Request request,int timeoutMs=5000,bool allowConsoleServer=false,string pipeName=null) {
   request.Version=PipeNames.ProtocolVersion;
   if(request.Language==null) request.Language=L.CodeOf(L.Current);
   using(var pipe=new NamedPipeClientStream(".",pipeName??PipeNames.Service,PipeDirection.InOut,PipeOptions.None,System.Security.Principal.TokenImpersonationLevel.Impersonation)) {
    try { pipe.Connect(timeoutMs); }
    catch(TimeoutException ex) { throw new ServiceUnavailableException(L.T("Der HDD-Spindown-Dienst antwortet nicht."),ex); }
    catch(IOException ex) { throw new ServiceUnavailableException(L.T("Verbindung zum HDD-Spindown-Dienst fehlgeschlagen."),ex); }
    catch(UnauthorizedAccessException ex) { throw new ServiceUnavailableException(L.T("Keine Berechtigung für die Verbindung zum Dienst."),ex); }
    uint serverPid;
    if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out serverPid)) throw new ServiceUnavailableException(L.T("Pipe-Server nicht prüfbar."));
    uint servicePid; var state=QueryState(out servicePid);
    if(!allowConsoleServer&&(state!=ServiceState.Running||servicePid!=serverPid)) throw new ServiceUnavailableException(L.T("Die Verbindung stammt nicht vom registrierten HDD-Spindown-Dienst. Abbruch."));
    pipe.ReadMode=PipeTransmissionMode.Message;
    var bytes=Encoding.UTF8.GetBytes(Codec.Serializer().Serialize(request));
    pipe.Write(bytes,0,bytes.Length); pipe.Flush();
    var ms=new MemoryStream(); var buffer=new byte[16384];
    do {
     int n=pipe.Read(buffer,0,buffer.Length); if(n==0) break; ms.Write(buffer,0,n);
     if(ms.Length>PipeNames.MaxMessageBytes*4) throw new InvalidOperationException(L.T("Antwort zu groß."));
    } while(!pipe.IsMessageComplete);
    if(ms.Length==0) throw new ServiceUnavailableException(L.T("Dienst hat die Verbindung ohne Antwort geschlossen."));
    return Codec.Serializer().Deserialize<Response>(Encoding.UTF8.GetString(ms.ToArray()));
   }
  }
 }
}
