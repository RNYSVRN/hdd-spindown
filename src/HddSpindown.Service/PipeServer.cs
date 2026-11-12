using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HddSpindown.Core;
using HddSpindown.Windows;

namespace HddSpindown.Service {
 // Local-only named pipe. ACL: SYSTEM and Administrators full, interactive users
 // read/write (to connect), network logons denied. Mutating requests additionally
 // require a token that belongs to the local Administrators group.
 public sealed class PipeServer : IDisposable {
  const int Instances=4;
  readonly Engine engine;
  readonly CancellationTokenSource cancel=new CancellationTokenSource();
  Task[] loops;
  public PipeServer(Engine e) { engine=e; }

  static PipeSecurity Security() {
   var s=new PipeSecurity();
   s.SetAccessRuleProtection(true,false);
   s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
   s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
   s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
   s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid,null),PipeAccessRights.ReadWrite,AccessControlType.Allow));
   return s;
  }

  public void Start() {
   loops=new Task[Instances];
   for(int i=0;i<Instances;i++) { bool first=i==0; loops[i]=Task.Run(()=>Loop(first)); }
  }

  async Task Loop(bool first) {
   while(!cancel.IsCancellationRequested) {
    NamedPipeServerStream pipe=null;
    try {
     var options=PipeOptions.Asynchronous|PipeOptions.WriteThrough;
     if(first) options|=(PipeOptions)0x00080000; // FILE_FLAG_FIRST_PIPE_INSTANCE: refuse if another process squats the name.
     first=false;
     pipe=NamedPipeServerStreamAcl.Create(PipeNames.Service,PipeDirection.InOut,16,PipeTransmissionMode.Message,options,PipeNames.MaxMessageBytes,PipeNames.MaxMessageBytes,Security());
     await pipe.WaitForConnectionAsync(cancel.Token).ConfigureAwait(false);
     var client=pipe; pipe=null;
     _=Task.Run(()=>Serve(client));
    } catch(OperationCanceledException) { break; }
    catch(Exception ex) { Log.Error("Named Pipe",ex); try { await Task.Delay(1000,cancel.Token).ConfigureAwait(false); } catch { break; } }
    finally { if(pipe!=null) pipe.Dispose(); }
   }
  }

  static byte[] ReadMessage(NamedPipeServerStream pipe) {
   var ms=new MemoryStream(); var buffer=new byte[8192];
   do {
    int n=pipe.Read(buffer,0,buffer.Length);
    if(n==0) return null;
    ms.Write(buffer,0,n);
    if(ms.Length>PipeNames.MaxMessageBytes) throw new InvalidOperationException("Nachricht zu groß.");
   } while(!pipe.IsMessageComplete);
   return ms.ToArray();
  }

  void Serve(NamedPipeServerStream pipe) {
   using(pipe) {
    try {
     bool admin=false; string user="?";
     pipe.RunAsClient(()=> {
      using(var id=WindowsIdentity.GetCurrent(TokenAccessLevels.Query)) { admin=TokenCheck.IsAdministratorAccount(id); user=id.Name; }
     });
     pipe.ReadMode=PipeTransmissionMode.Message;
     while(pipe.IsConnected&&!cancel.IsCancellationRequested) {
      var bytes=ReadMessage(pipe); if(bytes==null) break;
      Response response;
      try {
       var request=Codec.Serializer().Deserialize<Request>(Encoding.UTF8.GetString(bytes));
       if(request!=null&&request.Op!=Ops.List&&request.Op!=Ops.Hello) Log.Info("Anfrage "+request.Op+" von "+user+(admin?" (Admin)":""));
       response=engine.Handle(request,admin);
      } catch(Exception ex) { Log.Warn("Ungültige Nachricht von "+user+": "+ex.Message); response=new Response {Ok=false,Error="Ungültige Nachricht.",ErrorKind="Protocol"}; }
      var outBytes=Encoding.UTF8.GetBytes(Codec.Serializer().Serialize(response));
      pipe.Write(outBytes,0,outBytes.Length); pipe.Flush();
     }
    } catch(IOException) { /* client went away */ }
    catch(Exception ex) { Log.Error("Pipe-Client",ex); }
   }
  }

  public void Dispose() { cancel.Cancel(); try { if(loops!=null) Task.WaitAll(loops,TimeSpan.FromSeconds(3)); } catch {} }
 }
}
