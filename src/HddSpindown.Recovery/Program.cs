using System;
using System.IO;
using System.Linq;
using System.Text;
using HddSpindown.Core;
using HddSpindown.Windows;

namespace HddSpindown.Recovery {
 // Stand-alone recovery entry point. Uses the same checked core as the service;
 // there is no separate script with diverging logic.
 static class Program {
  const int Ok=0, Failed=1, Usage=2, OpenTransactions=3, ServiceIsRunning=4;

  // Translates catalog texts and expands service tokens.
  static void Say(string text) { Console.WriteLine(L.Expand(L.T(text))); }

  static int Main(string[] args) {
   Console.OutputEncoding=Encoding.UTF8;
   // --lang de|en overrides the configured language for this call.
   int li=Array.FindIndex(args,a=>a=="--lang");
   if(li>=0&&li+1<args.Length) { var l=L.Parse(args[li+1]); if(l.HasValue) L.Default=l.Value; args=args.Where((a,i)=>i!=li&&i!=li+1).ToArray(); }
   string cmd=args.Length>0?args[0].ToLowerInvariant():"help";
   try {
    switch(cmd) {
     case "status": return Status();
     case "list": return List();
     case "recover": return Recover();
     case "import-legacy": return ImportLegacy(args.Length>1?args[1]:null);
     case "export": return Export(args.Length>1?args[1]:null);
     case "preuninstall": return PreUninstall();
     default: Help(); return cmd=="help"||cmd=="/?"||cmd=="-h"?Ok:Usage;
    }
   } catch(Exception ex) { Console.Error.WriteLine(L.F("Fehler: {0}",L.Expand(ex.Message))); return Failed; }
  }

  static void Help() {
   Say("HDD Spindown Recovery");
   Console.WriteLine();
   Say("  status                Zustand von Dienst und offenen Vorgängen anzeigen");
   Say("  list                  Erkannte HDDs und Zulassung anzeigen (nur lesend)");
   Say("  recover               Offene Vorgänge wiederherstellen (Dienst muss gestoppt sein)");
   Say("  import-legacy [Datei] Laufwerksbuchstaben aus dem 0.3.x-Journal wiederherstellen");
   Say("  export [Datei]        Diagnosebericht schreiben (Seriennummern gekürzt)");
   Say("  --lang de|en          Sprache der Ausgabe");
   Console.WriteLine();
   Say("Es werden nie belegte Buchstaben überschrieben, keine Ersatzbuchstaben vergeben, keine Dateien geschlossen und keine Reparaturen (CHKDSK) ausgeführt.");
  }

  static bool ServiceRunning() { uint pid; return ServiceClient.QueryState(out pid)==ServiceClient.ServiceState.Running; }

  static void PrintTransactions(State state) {
   var open=state.Transactions.Where(t=>t.Phase!=Phase.Completed).ToArray();
   Console.WriteLine(L.F("Offene Vorgänge: {0}",open.Length));
   foreach(var t in open) {
    Console.WriteLine("  "+t.RequestId+" · "+t.Device.Identity.Model+" SN "+Paths.ShortSerial(t.Device.Identity.Serial)+" · Phase "+t.Phase);
    foreach(var m in t.Mounts) Console.WriteLine("     "+m.Letter+": → Volume{"+m.VolumeGuid+"} · "+m.Step);
    if(t.LastError!=null) Console.WriteLine("     "+L.F("Letzter Fehler: {0}",L.Expand(t.LastError.Split('\n')[0].Trim())));
   }
  }

  static int Status() {
   uint pid; var state=ServiceClient.QueryState(out pid);
   Console.WriteLine(L.F("Dienst: {0}",state));
   if(state==ServiceClient.ServiceState.Running) {
    var r=ServiceClient.Send(new Request {Op=Ops.Diagnostics});
    Say(r.Text);
    return r.Devices.Any(d=>d.Operation!=null)?OpenTransactions:Ok;
   }
   if(!Directory.Exists(Paths.StateDir)) { Say("Kein Zustandsspeicher vorhanden."); return Ok; }
   using(var store=new DurableStore(Paths.StateDir)) {
    Console.WriteLine(L.F("Speicher bestätigt: {0}",store.Healthy?L.T("ja"):L.T("nein")));
    var s=store.Read(); PrintTransactions(s);
    return s.Transactions.Any(t=>t.Phase!=Phase.Completed)?OpenTransactions:Ok;
   }
  }

  static int List() {
   var backend=new WindowsBackend(new BackendOptions {ProtectedDataPath=Paths.DataRoot});
   foreach(var d in backend.List()) {
    Console.WriteLine(L.F("Datenträger {0}: {1} · SN {2} · {3} TB · {4}",d.Number,L.Expand(d.Model),Paths.ShortSerial(d.Serial),(d.Capacity/1e12).ToString("0.#"),String.Join(" ",d.Letters.Select(l=>l+":"))));
    Console.WriteLine("   "+(d.Eligible?L.T("zugelassen"):L.F("nicht zugelassen: {0}",L.Expand(d.Reason))));
   }
   return Ok;
  }

  static int Recover() {
   if(ServiceRunning()) {
    Say("Der Dienst läuft und hält die Sperren. Freigabe über die App oder:");
    Console.WriteLine("  sc stop HddSpindown   "+L.T("(stellt beim Beenden alle Buchstaben wieder her)"));
    return ServiceIsRunning;
   }
   if(!Directory.Exists(Paths.StateDir)) { Say("Kein Zustandsspeicher vorhanden. Nichts zu tun."); return Ok; }
   var backend=new WindowsBackend(new BackendOptions {ProtectedDataPath=Paths.DataRoot});
   using(var store=new DurableStore(Paths.StateDir))
   using(var core=new Controller(store,backend)) {
    var failures=core.Initialize();
    PrintTransactions(store.Read());
    foreach(var f in failures) Console.WriteLine(L.F("Offen: {0}: {1}",f.RequestId,L.Expand(f.OriginalError.Message)));
    Say(failures.Length==0?"Alle Vorgänge wiederhergestellt.":"Nicht alle Vorgänge konnten wiederhergestellt werden. Konflikt beheben (z. B. Buchstaben freimachen, HDD anschließen) und erneut ausführen.");
    return failures.Length==0?Ok:OpenTransactions;
   }
  }

  static int ImportLegacy(string file) {
   file=file??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HDD Spindown","mount-recovery.json");
   var entries=LegacyImport.ReadFile(file);
   if(entries.Length==0) { Console.WriteLine(L.F("Keine ausstehenden Einträge in {0}",file)); return Ok; }
   bool all;
   if(ServiceRunning()) {
    var r=ServiceClient.Send(new Request {Op=Ops.ImportLegacy,Legacy=entries},30000);
    foreach(var m in r.Messages) Say("  "+m);
    all=r.Ok;
   } else {
    var results=LegacyImport.Restore(entries,new WindowsBackend());
    foreach(var x in results) Say("  "+x.Message);
    all=results.All(x=>x.Restored);
   }
   if(all) { string target=file+".migriert-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Move(file,target); Console.WriteLine(L.F("Altes Journal umbenannt: {0}",target)); return Ok; }
   Say("Altes Journal bleibt erhalten, bis alle Einträge wiederhergestellt sind.");
   return OpenTransactions;
  }

  static int Export(string file) {
   var sb=new StringBuilder();
   sb.AppendLine(L.F("HDD Spindown Diagnosebericht · {0}",DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
   uint pid; var state=ServiceClient.QueryState(out pid); sb.AppendLine(L.F("Dienst: {0}",state));
   if(state==ServiceClient.ServiceState.Running) { var r=ServiceClient.Send(new Request {Op=Ops.Diagnostics}); sb.AppendLine(L.Expand(r.Text)); }
   else if(Directory.Exists(Paths.StateDir)) {
    using(var store=new DurableStore(Paths.StateDir)) {
     foreach(var t in store.Read().Transactions.Where(t=>t.Phase!=Phase.Completed))
      sb.AppendLine(t.RequestId+" "+t.Device.Identity.Model+" SN "+Paths.ShortSerial(t.Device.Identity.Serial)+" "+t.Phase+" "+String.Join(" ",t.Mounts.Select(m=>m.Letter+":"+m.Step+"→{"+m.VolumeGuid+"}")));
    }
   }
   file=file??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"HDD-Spindown-"+(L.Current==Lang.En?"Diagnostics-":"Diagnose-")+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt");
   File.WriteAllText(file,sb.ToString(),Encoding.UTF8);
   Console.WriteLine(L.F("Bericht geschrieben: {0}",file));
   return Ok;
  }

  // Installer gate: update/uninstall only with a confirmed state of all transactions.
  static int PreUninstall() {
   if(ServiceRunning()) {
    var r=ServiceClient.Send(new Request {Op=Ops.ReleaseAll},10000);
    bool open=r.Devices.Any(d=>d.Operation!=null);
    Say(open?"Offene Vorgänge – Deinstallation abgebrochen.":"Alle HDDs freigegeben.");
    return open?OpenTransactions:Ok;
   }
   if(!Directory.Exists(Paths.StateDir)) return Ok;
   using(var store=new DurableStore(Paths.StateDir)) {
    bool open=store.Read().Transactions.Any(t=>t.Phase!=Phase.Completed);
    if(open) { Say("Offene Vorgänge im Zustandsspeicher. Zuerst 'HddSpindown.Recovery recover' ausführen."); return OpenTransactions; }
    return Ok;
   }
  }
 }
}
