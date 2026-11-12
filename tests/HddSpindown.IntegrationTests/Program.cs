using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HddSpindown.Core;
using HddSpindown.Windows;

namespace HddSpindown.IntegrationTests {
 // Real Windows storage stack with disposable NTFS VHDX files: real volume locks,
 // real mount manager, real process termination. The backend runs in test mode and
 // cannot see physical disks. No ATA command is sent (VHDs have no motor).
 static class Program {
  static int assertions; static StreamWriter log; static string root;
  static void Log(string s) { string line=DateTime.Now.ToString("HH:mm:ss")+" "+s; Console.WriteLine(line); log?.WriteLine(line); log?.Flush(); }
  static void Check(bool ok,string label) { assertions++; if(!ok) throw new Exception("FEHLGESCHLAGEN: "+label); Log("  ok  "+label); }
  static T Throws<T>(Action a,string label) where T:Exception { try { a(); } catch(T ex) { Check(true,label+" ("+ex.GetType().Name+": "+First(ex.Message)+")"); return ex; } throw new Exception("FEHLGESCHLAGEN (keine Exception): "+label); }
  static string First(string s) { int i=s.IndexOfAny(new[]{'\r','\n'}); return i<0?s:s.Substring(0,i); }
  static BackendOptions TestOptions() { return new BackendOptions {AllowVirtualTestDisks=true}; }

  static void DiskPart(string script) {
   string file=Path.Combine(root,"dp-"+Guid.NewGuid().ToString("N")+".txt"); File.WriteAllText(file,script,Encoding.ASCII);
   var p=Process.Start(new ProcessStartInfo("diskpart.exe","/s \""+file+"\"") {UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true});
   string output=p.StandardOutput.ReadToEnd(); p.WaitForExit();
   if(p.ExitCode!=0) throw new Exception("diskpart fehlgeschlagen ("+p.ExitCode+"):\n"+output);
  }
  static string[] FreeLetters(int count) {
   var used=new HashSet<char>(DriveInfo.GetDrives().Select(d=>char.ToUpperInvariant(d.Name[0])));
   var b=new WindowsBackend(TestOptions());
   return "ZYXWVUTSRQPONMLKJIHG".Where(c=>!used.Contains(c)).Select(c=>c.ToString()).Where(l=>{ try { return b.LookupLetter(l)==null; } catch { return false; } }).Take(count).ToArray();
  }
  sealed class TestDisk { public string File, DeviceId; public string[] Letters; public Dictionary<string,string> Volumes=new Dictionary<string,string>(); }

  static TestDisk created; // Registered before any check so cleanup always detaches it.
  static TestDisk Create(string name,string[] letters) {
   var t=new TestDisk {File=Path.Combine(root,name+".vhdx"),Letters=letters};
   created=t;
   var sb=new StringBuilder();
   sb.AppendLine("create vdisk file=\""+t.File+"\" maximum=192 type=expandable");
   sb.AppendLine("select vdisk file=\""+t.File+"\"");
   sb.AppendLine("attach vdisk");
   sb.AppendLine("convert gpt");
   for(int i=0;i<letters.Length;i++) {
    sb.AppendLine(i<letters.Length-1?"create partition primary size=64":"create partition primary");
    sb.AppendLine("format fs=ntfs quick label=HDDS-IT"+i);
    sb.AppendLine("assign letter="+letters[i]);
   }
   DiskPart(sb.ToString());
   var b=new WindowsBackend(TestOptions());
   foreach(var l in letters) { File.WriteAllText(l+@":\testdaten.txt","HDD Spindown Integrationstest "+name); t.Volumes[l]=b.LookupLetter(l); }
   var disk=b.List().Single(d=>d.Letters.OrderBy(x=>x).SequenceEqual(letters.OrderBy(x=>x)));
   t.DeviceId=disk.DeviceId;
   if(!t.DeviceId.StartsWith("VHD:")) throw new Exception("Sicherheitsabbruch: Testgerät ist keine VHD.");
   Log("VHD "+name+" angelegt: "+t.DeviceId+" · "+String.Join(" ",letters.Select(l=>l+":"))+" · zugelassen="+disk.Eligible+(disk.Reason!=null?" ("+disk.Reason+")":""));
   Check(disk.Eligible,"Test-VHD als zugelassenes Gerät erkannt");
   return t;
  }
  static void Detach(TestDisk t) { try { DiskPart("select vdisk file=\""+t.File+"\"\r\ndetach vdisk\r\n"); } catch(Exception ex) { Log("Detach: "+ex.Message); } }
  static void AssertOriginal(IDeviceBackend b,TestDisk t,string label) {
   foreach(var kv in t.Volumes) Check(String.Equals(b.LookupLetter(kv.Key),kv.Value,StringComparison.OrdinalIgnoreCase),label+": "+kv.Key+": zeigt auf dasselbe Volume");
   foreach(var l in t.Letters) Check(File.ReadAllText(l+@":\testdaten.txt").StartsWith("HDD Spindown Integrationstest"),label+": Testdaten auf "+l+": lesbar und unverändert");
  }
  static bool VolumeAccessible(string guid) {
   try { using(var fs=new FileStream(@"\\?\Volume{"+guid+@"}\testdaten.txt",FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) return true; }
   catch { return false; }
  }

  // ---------- scenarios ----------
  static void PauseRelease(TestDisk t) {
   Log("Szenario: Pausieren und Freigeben (2 Volumes, 2 Buchstaben)");
   var b=new WindowsBackend(TestOptions()); Guid id=Guid.NewGuid();
   using(var store=new DurableStore(Path.Combine(root,"state-normal")))
   using(var core=new Controller(store,b)) {
    Check(core.Initialize().Length==0,"Sauberer Start");
    var s=core.Pause(t.DeviceId,id);
    Check(s.Phase==Phase.Paused&&s.Access==AccessState.Locked,"Status: pausiert, Volumes gesperrt");
    foreach(var l in t.Letters) Check(b.LookupLetter(l)==null,l+": aus dem Mount-Manager entfernt");
    foreach(var g in t.Volumes.Values) Check(!VolumeAccessible(g),"Volume {"+g.Substring(0,8)+"…} für andere Prozesse gesperrt");
    Check(!Directory.Exists(t.Letters[0]+@":\"),"Explorer-Pfad "+t.Letters[0]+@":\ existiert nicht mehr");
    var r=core.Release(id);
    Check(r.LettersRestored&&r.Access==AccessState.Released,"Freigabe bestätigt");
    AssertOriginal(b,t,"Nach Freigabe");
    foreach(var g in t.Volumes.Values) Check(VolumeAccessible(g),"Volume {"+g.Substring(0,8)+"…} wieder zugänglich");
   }
  }

  static void OpenFileBlocksPause(TestDisk t) {
   Log("Szenario: Offene Datei verhindert Sperre (kein erzwungenes Aushängen)");
   var b=new WindowsBackend(TestOptions());
   using(var store=new DurableStore(Path.Combine(root,"state-openfile")))
   using(var core=new Controller(store,b))
   using(var open=new FileStream(t.Letters[t.Letters.Length-1]+@":\offen.txt",FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite)) {
    core.Initialize();
    open.WriteByte(42); open.Flush();
    var ex=Throws<OperationException>(()=>core.Pause(t.DeviceId,Guid.NewGuid()),"Pause mit offener Datei abgelehnt");
    AssertOriginal(b,t,"Nach Ablehnung");
    Check(core.GetStatuses().All(x=>x.Phase==Phase.Completed),"Kein offener Vorgang zurückgeblieben");
    open.WriteByte(43); open.Flush();
    Check(open.Length==2,"Offene Datei blieb beschreibbar (nicht zwangsweise geschlossen)");
   }
   File.Delete(t.Letters[t.Letters.Length-1]+@":\offen.txt");
  }

  static int RunChild(string state,string device,string mode) {
   var exe=Environment.ProcessPath;
   var p=Process.Start(new ProcessStartInfo(exe,"--child \""+state+"\" \""+device+"\" "+mode) {UseShellExecute=false,CreateNoWindow=true});
   if(!p.WaitForExit(60000)) { p.Kill(); throw new Exception("Kindprozess hängt"); }
   return p.ExitCode;
  }
  static int Child(string state,string device,string mode) {
   var b=new WindowsBackend(TestOptions());
   var store=new DurableStore(state); var core=new Controller(store,b); core.Initialize();
   if(mode=="after-hide") {
    // Crash between hiding and standby: wrap the backend to terminate at the first standby request.
    var crash=new CrashingBackend(b,"standby");
    var store2=store; core.Dispose(); store2.Dispose();
    store=new DurableStore(state); core=new Controller(store,crash); core.Initialize();
   } else if(mode=="after-first-remove") {
    var crash=new CrashingBackend(b,"remove#1");
    core.Dispose(); store.Dispose();
    store=new DurableStore(state); core=new Controller(store,crash); core.Initialize();
   }
   core.Pause(device,Guid.NewGuid());
   Environment.Exit(73); // Abrupt end while paused: handles die with the process, letters stay removed.
   return 74;
  }
  sealed class CrashingBackend : IDeviceBackend {
   readonly IDeviceBackend inner; readonly string at; int removes;
   public CrashingBackend(IDeviceBackend b,string point) { inner=b; at=point; }
   public IDeviceSession Open(string id) { var s=inner.Open(id); return at=="standby"?new Session(s):s; }
   public string LookupLetter(string l) { return inner.LookupLetter(l); }
   public void RemoveLetter(string l) { inner.RemoveLetter(l); if(at=="remove#1"&&++removes==1) Environment.Exit(73); }
   public void RestoreLetter(string l,string v) { inner.RestoreLetter(l,v); }
   public void NotifyExplorer(string l,bool a) { inner.NotifyExplorer(l,a); }
   sealed class Session : IDeviceSession {
    readonly IDeviceSession s; public Session(IDeviceSession x) { s=x; }
    public Descriptor Inspect() { return s.Inspect(); }
    public IDisposable LockVolume(string g) { return s.LockVolume(g); }
    public void RequestStandby() { Environment.Exit(73); }
    public void Dispose() { s.Dispose(); }
   }
  }

  static void CrashRecovery(TestDisk t,string mode) {
   Log("Szenario: Prozessabbruch ("+mode+") und Wiederherstellung beim nächsten Start");
   string state=Path.Combine(root,"state-crash-"+mode);
   Check(RunChild(state,t.DeviceId,mode)==73,"Kindprozess an der vorgesehenen Stelle beendet");
   var b=new WindowsBackend(TestOptions());
   Check(t.Letters.Any(l=>b.LookupLetter(l)==null),"Nach Abbruch fehlt mindestens ein Buchstabe (realistischer Zustand)");
   using(var store=new DurableStore(state))
   using(var core=new Controller(store,b)) {
    Check(core.GetStatuses().All(x=>x.Access!=AccessState.Locked),"Neuer Prozess behauptet keine geerbte Sperre");
    var failures=core.Initialize();
    foreach(var f in failures) Log("    offen: "+f.OriginalError.Message);
    Check(failures.Length==0,"Wiederherstellung beim Start erfolgreich");
    AssertOriginal(b,t,"Nach Wiederherstellung");
   }
  }

  static void LetterConflict(TestDisk t) {
   Log("Szenario: Inzwischen belegter Buchstabe wird nicht überschrieben");
   string state=Path.Combine(root,"state-conflict");
   Check(RunChild(state,t.DeviceId,"paused")==73,"Kindprozess pausiert und bricht ab");
   var b=new WindowsBackend(TestOptions());
   string victim=t.Letters[0]; string substDir=Path.Combine(root,"subst-ziel"); Directory.CreateDirectory(substDir);
   var subst=Process.Start(new ProcessStartInfo("subst.exe",victim+": \""+substDir+"\"") {UseShellExecute=false,CreateNoWindow=true}); subst.WaitForExit();
   try {
    Throws<InvalidOperationException>(()=>b.LookupLetter(victim),"SUBST-Belegung gilt nicht als frei");
    using(var store=new DurableStore(state))
    using(var core=new Controller(store,b)) {
     var failures=core.Initialize();
     Check(failures.Length==1,"Konflikt gemeldet, Vorgang bleibt offen");
     Check(Directory.Exists(victim+@":\")&&!File.Exists(victim+@":\testdaten.txt"),"SUBST-Laufwerk "+victim+": unverändert (nicht überschrieben)");
     Check(core.GetOpenStatuses().Single().Phase==Phase.RecoveryRequired,"Status: Wiederherstellung offen");
     var del=Process.Start(new ProcessStartInfo("subst.exe",victim+": /D") {UseShellExecute=false,CreateNoWindow=true}); del.WaitForExit();
     Check(core.RecoverAll().Length==0,"Nach Auflösen des Konflikts wiederhergestellt");
     AssertOriginal(b,t,"Nach Konfliktlösung");
    }
   } finally { var del=Process.Start(new ProcessStartInfo("subst.exe",victim+": /D") {UseShellExecute=false,CreateNoWindow=true}); del.WaitForExit(); }
  }

  static void Resume(TestDisk t) {
   Log("Szenario: Abgleich nach Resume");
   var b=new WindowsBackend(TestOptions()); Guid id=Guid.NewGuid();
   using(var store=new DurableStore(Path.Combine(root,"state-resume")))
   using(var core=new Controller(store,b)) {
    core.Initialize(); core.Pause(t.DeviceId,id);
    Check(core.Revalidate().Length==0,"Gültige Sperren bestehen den Resume-Abgleich");
    var s=core.GetStatuses().Single();
    Check(s.Access==AccessState.Locked&&s.Hardware==HardwareState.Unknown,"Weiter gesperrt, Hardwarezustand unbekannt");
    core.Release(id); AssertOriginal(b,t,"Nach Freigabe");
   }
  }

  static void Legacy(TestDisk t) {
   Log("Szenario: Übernahme des 0.3.x-Journals");
   var b=new WindowsBackend(TestOptions());
   string l=t.Letters[0]; string vol=t.Volumes[l];
   b.RemoveLetter(l);
   Check(b.LookupLetter(l)==null,"Buchstabe wie von 0.3.x entfernt");
   var entries=new[]{new LegacyEntry {Root=l+@":\",Volume=@"\\?\Volume{"+vol+@"}\"},new LegacyEntry {Root=@"C:\",Volume=@"\\?\Volume{"+vol+@"}\"},new LegacyEntry {Root="kaputt",Volume="x"}};
   var results=LegacyImport.Restore(entries,b);
   Check(results[0].Restored,"Alter Eintrag wiederhergestellt");
   Check(!results[1].Restored&&!results[2].Restored,"Systembuchstabe und ungültiger Eintrag abgelehnt");
   AssertOriginal(b,t,"Nach Legacy-Import");
  }

  static void Inventory(TestDisk t) {
   Log("Szenario: Inventur überspringt pausierte Geräte");
   var b=new WindowsBackend(TestOptions());
   var skipped=b.List(id=>id==t.DeviceId).Single(d=>d.DeviceId==t.DeviceId);
   Check(skipped.Reason=="Pausiert."&&skipped.Letters.Length==0,"Pausiertes Gerät nicht inspiziert");
  }

  static void ServiceEngine(TestDisk t) {
   Log("Szenario: Dienst-Engine und Named Pipe (ohne Installation)");
   HddSpindown.Service.Log.Sink=line=>Log("    [Dienst] "+line);
   var b=new WindowsBackend(TestOptions());
   var engine=new HddSpindown.Service.Engine(b,skip=>b.List(skip),Path.Combine(root,"state-engine"),PowerPolicy.None(),TimeSpan.Zero);
   engine.Start();
   Check(engine.Ready,"Engine nach Wiederherstellungsabgleich bereit");
   var pipe=new HddSpindown.Service.PipeServer(engine); pipe.Start();
   try {
    var list=ServiceClient.Send(new Request {Op=Ops.List},5000,allowConsoleServer:true);
    var view=list.Devices.SingleOrDefault(v=>v.Disk.DeviceId==t.DeviceId);
    Check(list.Ok&&view!=null&&view.Disk.Eligible&&list.StoreHealthy,"Über die Pipe: Testgerät gelistet und zugelassen");
    var denied=engine.Handle(new Request {Version=1,Op=Ops.Pause,DeviceId=t.DeviceId,RequestId=Guid.NewGuid().ToString("D")},false);
    Check(!denied.Ok&&denied.ErrorKind=="Denied","Nicht-Administrator darf nicht pausieren");
    Check(t.Letters.All(l=>b.LookupLetter(l)!=null),"Abgelehnter Auftrag ändert nichts");
    var bad=engine.Handle(new Request {Version=1,Op="format",DeviceId=t.DeviceId},true);
    Check(!bad.Ok&&bad.ErrorKind=="Protocol","Unbekannte Operation abgelehnt");
    var forged=engine.Handle(new Request {Version=1,Op=Ops.Pause,DeviceId="SATA:FREMD:123",RequestId=Guid.NewGuid().ToString("D")},true);
    Check(!forged.Ok&&forged.ErrorKind=="NotFound","Unbekannte Gerätekennung abgelehnt");
    string rid=Guid.NewGuid().ToString("D");
    var paused=ServiceClient.Send(new Request {Op=Ops.Pause,DeviceId=t.DeviceId,RequestId=rid},5000,allowConsoleServer:true);
    Check(paused.Ok,"Über die Pipe pausiert: "+(paused.Error??paused.Messages.FirstOrDefault()));
    Check(t.Letters.All(l=>b.LookupLetter(l)==null),"Buchstaben entfernt");
    var again=ServiceClient.Send(new Request {Op=Ops.Pause,DeviceId=t.DeviceId,RequestId=rid},5000,allowConsoleServer:true);
    Check(again.Ok,"Doppelter Auftrag (gleiche Request-ID) idempotent");
    var op=again.Devices.Single(v=>v.Disk.DeviceId==t.DeviceId).Operation;
    Check(op!=null&&op.Access==AccessState.Locked&&op.LettersMissing.Length==t.Letters.Length,"Status getrennt: gesperrt, ausgeblendet");
    var released=ServiceClient.Send(new Request {Op=Ops.Release,DeviceId=t.DeviceId},5000,allowConsoleServer:true);
    Check(released.Ok,"Über die Pipe freigegeben");
    AssertOriginal(b,t,"Nach Freigabe über Pipe");
    var p2=engine.Handle(new Request {Version=1,Op=Ops.Pause,DeviceId=t.DeviceId,RequestId=Guid.NewGuid().ToString("D")},true);
    Check(p2.Ok,"Erneut pausiert");
   } finally { pipe.Dispose(); }
   engine.Stop(); engine.Dispose();
   AssertOriginal(b,t,"Dienst-Stopp stellt Buchstaben wieder her");

   var strict=new HddSpindown.Service.Engine(b,skip=>b.List(skip),Path.Combine(root,"state-policy"),new PowerPolicy(),TimeSpan.Zero);
   strict.Start();
   var a=strict.Handle(new Request {Version=1,Op=Ops.Pause,DeviceId=t.DeviceId,RequestId=Guid.NewGuid().ToString("D")},true);
   var r=strict.Handle(new Request {Version=1,Op=Ops.Release,DeviceId=t.DeviceId},true);
   var c=strict.Handle(new Request {Version=1,Op=Ops.Pause,DeviceId=t.DeviceId,RequestId=Guid.NewGuid().ToString("D")},true);
   Check(a.Ok&&r.Ok&&!c.Ok&&c.ErrorKind=="Policy","Mindestlaufzeit verhindert sofortiges erneutes Schlafen");
   Check(c.Devices.Single(v=>v.Disk.DeviceId==t.DeviceId).PolicyBlock!=null,"Schonzeit wird in der Geräteansicht angezeigt");
   strict.Stop(); strict.Dispose();
   AssertOriginal(b,t,"Nach Schonregel-Test");
  }

  static int Main(string[] args) {
   if(args.Length==4&&args[0]=="--child") return Child(args[1],args[2],args[3]);
   root=Path.Combine(Path.GetTempPath(),"HDDSpindown-IT-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
   Directory.CreateDirectory(root);
   log=new StreamWriter(Path.Combine(root,"integration.log"),false,Encoding.UTF8);
   Log("Testordner: "+root);
   TestDisk disk=null; int code=0;
   try {
    var letters=FreeLetters(2);
    if(letters.Length<2) throw new Exception("Keine zwei freien Laufwerksbuchstaben.");
    disk=Create("it-disk",letters);
    PauseRelease(disk);
    OpenFileBlocksPause(disk);
    Resume(disk);
    Inventory(disk);
    CrashRecovery(disk,"paused");
    CrashRecovery(disk,"after-hide");
    CrashRecovery(disk,"after-first-remove");
    LetterConflict(disk);
    Legacy(disk);
    ServiceEngine(disk);
    Log(assertions+" Prüfungen bestanden. Echter Mount-Manager und echte Volume-Sperren; keine physischen HDDs, kein ATA-Befehl.");
   } catch(Exception ex) { Log(ex.ToString()); code=1; }
   finally {
    disk=disk??created;
    if(disk!=null) {
     try { var b=new WindowsBackend(TestOptions()); foreach(var kv in disk.Volumes) if(b.LookupLetter(kv.Key)==null) b.RestoreLetter(kv.Key,kv.Value); } catch {}
     Detach(disk);
    }
    Log(code==0?"ERGEBNIS: BESTANDEN":"ERGEBNIS: FEHLGESCHLAGEN");
    File.WriteAllText(Path.Combine(Path.GetTempPath(),"HDDSpindown-IT-last.txt"),root);
    log.Dispose();
   }
   return code;
  }
 }
}
