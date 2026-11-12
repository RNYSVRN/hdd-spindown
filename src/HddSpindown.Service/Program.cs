using System;
using System.ServiceProcess;
using System.Threading;
using HddSpindown.Core;
using HddSpindown.Windows;

namespace HddSpindown.Service {
 public sealed class SpindownService : ServiceBase {
  Engine engine; PipeServer pipe; Thread starter;
  public SpindownService() {
   ServiceName=Paths.ServiceName; CanStop=true; CanShutdown=true; CanHandlePowerEvent=true; CanPauseAndContinue=false; AutoLog=false;
  }
  public static Engine CreateEngine() {
   Paths.EnsureProtectedDirectory(Paths.DataRoot);
   Paths.EnsureProtectedDirectory(Paths.StateDir);
   var backend=new WindowsBackend(new BackendOptions {ProtectedDataPath=Paths.DataRoot});
   return new Engine(backend,skip=>backend.List(skip),Paths.StateDir,new PowerPolicy(),TimeSpan.FromSeconds(5));
  }
  protected override void OnStart(string[] args) {
   Log.Init();
   Log.Info(L.F("Dienst startet, Version {0}",Engine.Version));
   try { ServiceRecovery.Configure(); } catch(Exception ex) { Log.Error(L.T("Wiederanlauf-Konfiguration"),ex); }
   engine=CreateEngine();
   engine.StoreFault=()=> { Thread.Sleep(3000); Log.Error(L.T("Neustart nach Speicherfehler.")); Environment.Exit(2); };
   pipe=new PipeServer(engine); pipe.Start();
   // Recovery may wait on slow devices; do not block the service control manager.
   starter=new Thread(()=> { try { engine.Start(); } catch(Exception ex) { Log.Error("Start",ex); } }) {IsBackground=true,Name="Recovery"};
   starter.Start();
  }
  void Shutdown(string reason) {
   Log.Info(reason);
   RequestAdditionalTime(60000);
   try { if(starter!=null) starter.Join(TimeSpan.FromSeconds(30)); } catch {}
   if(engine!=null) engine.Stop();
   if(pipe!=null) pipe.Dispose();
   if(engine!=null) engine.Dispose();
   Log.Info(L.T("Dienst beendet."));
  }
  protected override void OnStop() { Shutdown(L.T("Dienst wird beendet: Laufwerksbuchstaben werden wiederhergestellt.")); }
  protected override void OnShutdown() { Shutdown(L.T("Windows fährt herunter: Laufwerksbuchstaben werden wiederhergestellt.")); }
  protected override bool OnPowerEvent(PowerBroadcastStatus status) {
   if(engine==null) return true;
   switch(status) {
    case PowerBroadcastStatus.QuerySuspend: case PowerBroadcastStatus.Suspend: engine.Suspend(); break;
    case PowerBroadcastStatus.ResumeAutomatic: case PowerBroadcastStatus.ResumeSuspend: case PowerBroadcastStatus.ResumeCritical:
     ThreadPool.QueueUserWorkItem(_=>engine.Resume()); break; // Short handler, work in background.
   }
   return true;
  }
 }

 static class Program {
  static int Main(string[] args) {
   if(args.Length>0&&args[0]=="--console") {
    // Diagnostics only: runs the same engine in a console window (administrator).
    Log.ConsoleEcho=true; Log.Init();
    var engine=SpindownService.CreateEngine(); var pipe=new PipeServer(engine); pipe.Start(); engine.Start();
    Console.WriteLine(L.T("HDD Spindown Dienst im Konsolenmodus. Enter beendet und stellt alle Laufwerksbuchstaben wieder her."));
    Console.ReadLine(); engine.Stop(); pipe.Dispose(); engine.Dispose(); return 0;
   }
   if(Environment.UserInteractive) { Console.WriteLine(L.T("HDD Spindown Dienst. Wird vom Installer als Windows-Dienst eingerichtet.")); return 1; }
   ServiceBase.Run(new SpindownService());
   return 0;
  }
 }
}
