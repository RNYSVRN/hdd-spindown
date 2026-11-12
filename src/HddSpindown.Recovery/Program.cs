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

  static int Main(string[] args) {
   Console.OutputEncoding=Encoding.UTF8;
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
   } catch(Exception ex) { Console.Error.WriteLine("Fehler: "+ex.Message); return Failed; }
  }

  static void Help() {
   Console.WriteLine(@"HDD Spindown Recovery

  status               Zustand von Dienst und offenen VorgÃ¤ngen anzeigen
  list                 Erkannte HDDs und Zulassung anzeigen (nur lesend)
  recover              Offene VorgÃ¤nge wiederherstellen (Dienst muss gestoppt sein)
  import-legacy [Datei] Laufwerksbuchstaben aus dem 0.3.x-Journal wiederherstellen
                       (Standard: %LOCALAPPDATA%\HDD Spindown\mount-recovery.json)
  export [Datei]       Diagnosebericht schreiben (Seriennummern gekÃ¼rzt)

Es werden nie belegte Buchstaben Ã¼berschrieben, keine Ersatzbuchstaben vergeben,
keine Dateien geschlossen und keine Reparaturen (CHKDSK) ausgefÃ¼hrt.");
  }

  static bool ServiceRunning() { uint pid; return ServiceClient.QueryState(out pid)==ServiceClient.ServiceState.Running; }

  static void PrintTransactions(State state) {
   var open=state.Transactions.Where(t=>t.Phase!=Phase.Completed).ToArray();
   Console.WriteLine("Offene VorgÃ¤nge: "+open.Length);
   foreach(var t in open) {
    Console.WriteLine("  "+t.RequestId+" Â· "+t.Device.Identity.Model+" SN "+Paths.ShortSerial(t.Device.Identity.Serial)+" Â· Phase "+t.Phase);
    foreach(var m in t.Mounts) Console.WriteLine("     "+m.Letter+": â†’ Volume{"+m.VolumeGuid+"} Â· "+m.Step);
    if(t.LastError!=null) Console.WriteLine("     Letzter Fehler: "+t.LastError.Split('\n')[0].Trim());
   }
  }

  static int Status() {
   uint pid; var state=ServiceClient.QueryState(out pid);
   Console.WriteLine("Dienst: "+state);
   if(state==ServiceClient.ServiceState.Running) {
    var r=ServiceClient.Send(new Request {Op=Ops.Diagnostics});
    Console.WriteLine(r.Text);
    return r.Devices.Any(d=>d.Operation!=null)?OpenTransactions:Ok;
   }
   if(!Directory.Exists(Paths.StateDir)) { Console.WriteLine("Kein Zustandsspeicher vorhanden."); return Ok; }
   using(var store=new DurableStore(Paths.StateDir)) {
    Console.WriteLine("Speicher bestÃ¤tigt: "+store.Healthy);
    var s=store.Read(); PrintTransactions(s);
    return s.Transactions.Any(t=>t.Phase!=Phase.Completed)?OpenTransactions:Ok;
   }
  }

  static int List() {
   var backend=new WindowsBackend(new BackendOptions {ProtectedDataPath=Paths.DataRoot});
   foreach(var d in backend.List()) {
    Console.WriteLine("DatentrÃ¤ger "+d.Number+": "+d.Model+" Â· SN "+Paths.ShortSerial(d.Serial)+" Â· "+(d.Capacity/1e12).ToString("0.#")+" TB Â· "+String.Join(" ",d.Letters.Select(l=>l+":")));
    Console.WriteLine("   "+(d.Eligible?"zugelassen":"nicht zugelassen: "+d.Reason));
   }
   return Ok;
  }

  static int Recover() {
   if(ServiceRunning()) {
    Console.WriteLine("Der Dienst lÃ¤uft und hÃ¤lt die Sperren. Freigabe Ã¼ber die App oder:\n  sc stop HddSpindown   (stellt beim Beenden alle Buchstaben wieder her)");
    return ServiceIsRunning;
   }
   if(!Directory.Exists(Paths.StateDir)) { Console.WriteLine("Kein Zustandsspeicher vorhanden. Nichts zu tun."); return Ok; }
   var backend=new WindowsBackend(new BackendOptions {ProtectedDataPath=Paths.DataRoot});
   using(var store=new DurableStore(Paths.StateDir))
   using(var core=new Controller(store,backend)) {
    var failures=core.Initialize();
    PrintTransactions(store.Read());
    foreach(var f in failures) Console.WriteLine("Offen: "+f.RequestId+": "+f.OriginalError.Message);
    Console.WriteLine(failures.Length==0?"Alle VorgÃ¤nge wiederhergestellt.":"Nicht alle VorgÃ¤nge konnten wiederhergestellt werden. Konflikt beheben (z. B. Buchstaben freimachen, HDD anschlieÃŸen) und erneut ausfÃ¼hren.");
    return failures.Length==0?Ok:OpenTransactions;
   }
  }

  static int ImportLegacy(string file) {
   file=file??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HDD Spindown","mount-recovery.json");
   var entries=LegacyImport.ReadFile(file);
   if(entries.Length==0) { Console.WriteLine("Keine ausstehenden EintrÃ¤ge in "+file); return Ok; }
   bool all;
   if(ServiceRunning()) {
    var r=ServiceClient.Send(new Request {Op=Ops.ImportLegacy,Legacy=entries},30000);
    foreach(var m in r.Messages) Console.WriteLine("  "+m);
    all=r.Ok;
   } else {
    var results=LegacyImport.Restore(entries,new WindowsBackend());
    foreach(var x in results) Console.WriteLine("  "+x.Message);
    all=results.All(x=>x.Restored);
   }
   if(all) { string target=file+".migriert-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Move(file,target); Console.WriteLine("Altes Journal umbenannt: "+target); return Ok; }
   Console.WriteLine("Altes Journal bleibt erhalten, bis alle EintrÃ¤ge wiederhergestellt sind.");
   return OpenTransactions;
  }

  static int Export(string file) {
   var sb=new StringBuilder();
   sb.AppendLine("HDD Spindown Diagnosebericht Â· "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
   uint pid; var state=ServiceClient.QueryState(out pid); sb.AppendLine("Dienst: "+state);
   if(state==ServiceClient.ServiceState.Running) { var r=ServiceClient.Send(new Request {Op=Ops.Diagnostics}); sb.AppendLine(r.Text); }
   else if(Directory.Exists(Paths.StateDir)) {
    using(var store=new DurableStore(Paths.StateDir)) {
     foreach(var t in store.Read().Transactions.Where(t=>t.Phase!=Phase.Completed))
      sb.AppendLine(t.RequestId+" "+t.Device.Identity.Model+" SN "+Paths.ShortSerial(t.Device.Identity.Serial)+" "+t.Phase+" "+String.Join(" ",t.Mounts.Select(m=>m.Letter+":"+m.Step+"â†’{"+m.VolumeGuid+"}")));
    }
   }
   file=file??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"HDD-Spindown-Diagnose-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt");
   File.WriteAllText(file,sb.ToString(),Encoding.UTF8);
   Console.WriteLine("Bericht geschrieben: "+file);
   return Ok;
  }

  // Installer gate: update/uninstall only with a confirmed state of all transactions.
  static int PreUninstall() {
   if(ServiceRunning()) {
    var r=ServiceClient.Send(new Request {Op=Ops.ReleaseAll},10000);
    bool open=r.Devices.Any(d=>d.Operation!=null);
    Console.WriteLine(open?"Offene VorgÃ¤nge â€“ Deinstallation abgebrochen.":"Alle HDDs freigegeben.");
    return open?OpenTransactions:Ok;
   }
   if(!Directory.Exists(Paths.StateDir)) return Ok;
   using(var store=new DurableStore(Paths.StateDir)) {
    bool open=store.Read().Transactions.Any(t=>t.Phase!=Phase.Completed);
    if(open) { Console.WriteLine("Offene VorgÃ¤nge im Zustandsspeicher. Zuerst 'HddSpindown.Recovery recover' ausfÃ¼hren."); return OpenTransactions; }
    return Ok;
   }
  }
 }
}
