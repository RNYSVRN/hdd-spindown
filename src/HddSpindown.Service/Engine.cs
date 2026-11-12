using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HddSpindown.Core;
using HddSpindown.Windows;

namespace HddSpindown.Service {
 // Single machine-wide owner of all hardware-changing operations. Requests are
 // processed one at a time; status reads are served from a snapshot so the UI
 // never blocks behind a slow device.
 public sealed class Engine : IDisposable {
  readonly IDeviceBackend backend;
  readonly Func<Func<string,bool>,DiskInfo[]> inventory;
  readonly string stateDir;
  readonly PowerPolicy policy;
  readonly TimeSpan stagger;
  readonly SemaphoreSlim op=new SemaphoreSlim(1,1);
  readonly object snap=new object();
  DurableStore store; Controller controller;
  volatile bool ready, suspended, stopping;
  string startupError;
  DiskInfo[] disks=new DiskInfo[0]; string inventoryUtc;
  DeviceView[] views=new DeviceView[0];
  Timer retry;
  public Action StoreFault;
  public static string Version { get { return typeof(Engine).Assembly.GetName().Version.ToString(3); } }

  public Engine(IDeviceBackend deviceBackend,Func<Func<string,bool>,DiskInfo[]> diskInventory,string stateDirectory,PowerPolicy powerPolicy,TimeSpan releaseStagger) {
   backend=deviceBackend; inventory=diskInventory; stateDir=stateDirectory; policy=powerPolicy; stagger=releaseStagger;
  }
  public bool Ready { get { return ready; } }

  // Recovery before anything else: no new sleep action until the journal is reconciled.
  public void Start() {
   op.Wait();
   try {
    try {
     store=new DurableStore(stateDir);
     controller=new Controller(store,backend,policy);
     var failures=controller.Initialize();
     foreach(var f in failures) Log.Warn("Wiederherstellung offen ("+f.RequestId+"): "+f.OriginalError.Message);
     Log.Info("Wiederherstellungsbestand abgeglichen. Offene Fälle: "+failures.Length);
     ready=true;
    } catch(Exception ex) {
     startupError="Zustandsspeicher nicht verwendbar: "+ex.Message+" Recovery-Werkzeug verwenden.";
     Log.Error("Start im Recovery-Modus",ex);
    }
    RefreshInventory();
   } finally { op.Release(); }
   retry=new Timer(_=>RetryOrphaned(),null,TimeSpan.FromMinutes(1),TimeSpan.FromMinutes(2));
  }

  void RetryOrphaned() {
   if(!ready||suspended||stopping) return;
   if(!op.Wait(0)) return;
   try {
    if(!controller.HasOrphaned) return;
    var failures=controller.RecoverOrphaned();
    Log.Info("Erneuter Wiederherstellungsversuch, offen: "+failures.Length);
    RefreshInventory();
   } catch(Exception ex) { Log.Error("Wiederherstellungsversuch",ex); CheckStore(); }
   finally { op.Release(); }
  }

  void RefreshInventory() {
   try {
    var paused=new HashSet<string>(ready?controller.GetOpenStatuses().Select(s=>s.DeviceId):new string[0]);
    disks=inventory(id=>paused.Contains(id));
    inventoryUtc=DateTime.UtcNow.ToString("O");
   } catch(Exception ex) { Log.Error("Laufwerkserkennung",ex); }
   BuildViews();
  }
  void BuildViews() {
   var list=new List<DeviceView>();
   Status[] open=ready?controller.GetOpenStatuses():new Status[0];
   foreach(var d in disks) {
    var s=d.DeviceId==null?null:open.FirstOrDefault(o=>o.DeviceId==d.DeviceId);
    string block=null;
    if(ready&&s==null&&d.Eligible&&d.DeviceId!=null) block=controller.PolicyBlock(d.DeviceId);
    list.Add(new DeviceView {Disk=d,Operation=s,PolicyBlock=block,Present=true});
   }
   foreach(var s in open.Where(o=>!disks.Any(d=>d.DeviceId==o.DeviceId)))
    list.Add(new DeviceView {Disk=new DiskInfo {DeviceId=s.DeviceId,Model=s.Model,Serial=s.Serial,Letters=s.Letters,Eligible=false,Reason="Nicht angeschlossen."},Operation=s,Present=false});
   lock(snap) views=list.ToArray();
  }
  Response Snapshot(bool ok=true,string error=null,string kind=null,IEnumerable<string> messages=null) {
   DeviceView[] v; lock(snap) v=views;
   bool healthy=ready&&controller.StoreHealthy;
   return new Response {Ok=ok,Error=error,ErrorKind=kind,ServiceVersion=Version,StoreHealthy=healthy,Suspended=suspended,
    RecoveryPending=!ready||v.Any(x=>x.Operation!=null&&x.Operation.Phase==Phase.RecoveryRequired),
    LastInventoryUtc=inventoryUtc,Devices=v,Messages=(messages??new string[0]).Concat(startupError!=null?new[]{startupError}:new string[0]).ToArray()};
  }

  public Response Handle(Request request,bool isAdmin) {
   if(request==null||request.Version!=PipeNames.ProtocolVersion||request.Op==null||!Ops.All.Contains(request.Op)) return Snapshot(false,"Ungültige Anfrage.","Protocol");
   bool mutating=request.Op==Ops.Pause||request.Op==Ops.Release||request.Op==Ops.ReleaseAll||request.Op==Ops.ImportLegacy;
   if(mutating&&!isAdmin) return Snapshot(false,"Nur Mitglieder der lokalen Administratorengruppe dürfen HDDs steuern.","Denied");
   if(request.Op==Ops.Hello||request.Op==Ops.List) return Snapshot();
   if(request.Op==Ops.Diagnostics) { var r=Snapshot(); r.Text=Diagnostics(); return r; }
   if(stopping) return Snapshot(false,"Dienst wird beendet.","Stopping");
   if(!op.Wait(TimeSpan.FromSeconds(request.Op==Ops.Refresh?2:30))) return Snapshot(false,"Ein anderer Vorgang läuft noch. Bitte kurz warten.","Busy");
   try {
    if(request.Op==Ops.Refresh) { RefreshInventory(); return Snapshot(); }
    if(!ready) return Snapshot(false,startupError??"Dienst startet noch.","NotReady");
    if(request.Op==Ops.ImportLegacy) return ImportLegacy(request.Legacy);
    if(request.Op==Ops.ReleaseAll) return ReleaseAll();
    if(String.IsNullOrWhiteSpace(request.DeviceId)||request.DeviceId.Length>200) return Snapshot(false,"Gerätekennung fehlt.","Protocol");
    if(request.Op==Ops.Pause) return Pause(request);
    return Release(request.DeviceId);
   } finally { op.Release(); }
  }

  Response Pause(Request request) {
   Guid id; if(!Guid.TryParse(request.RequestId,out id)||id==Guid.Empty) return Snapshot(false,"Request-ID fehlt.","Protocol");
   if(suspended) return Snapshot(false,"Windows wechselt in den Energiesparmodus. Keine neuen Vorgänge.","Suspended");
   var disk=disks.FirstOrDefault(d=>d.DeviceId==request.DeviceId);
   bool known=controller.GetStatuses().Any(s=>s.RequestId==id.ToString("D"));
   if(!known) {
    if(disk==null) return Snapshot(false,"Gerät nicht gefunden. Bitte neu einlesen.","NotFound");
    if(!disk.Eligible) return Snapshot(false,disk.Reason??"Gerät ist nicht zugelassen.","NotEligible");
   }
   try {
    Log.Info("Pause angefordert: "+(disk==null?"?":disk.Model)+" SN "+Paths.ShortSerial(disk==null?"":disk.Serial)+" Tx "+id);
    var status=controller.Pause(request.DeviceId,id);
    Log.Info("Pausiert: Tx "+id+" Phase "+status.Phase);
    BuildViews();
    return Snapshot(true,null,null,new[]{String.Join(", ",status.Letters.Select(l=>l+":"))+" gesperrt und ausgeblendet · Standby angefordert."});
   } catch(PolicyException ex) { BuildViews(); return Snapshot(false,ex.Message,"Policy"); }
   catch(OperationException ex) {
    Log.Error("Pause fehlgeschlagen Tx "+id+": "+ex.OriginalError.Message+(ex.CleanupErrors.Length>0?" | Rücksetzfehler: "+String.Join("; ",ex.CleanupErrors.Select(c=>c.Message)):""));
    CheckStore(); RefreshInventory();
    string text=ex.OriginalError.Message;
    var s=controller.StoreHealthy?controller.GetStatuses().FirstOrDefault(x=>x.RequestId==id.ToString("D")):null;
    if(s!=null&&s.Phase==Phase.Completed) text+=" Keine Sperre behalten; Laufwerksbuchstaben sind wiederhergestellt.";
    else text+=" Wiederherstellung offen – siehe Gerätestatus.";
    return Snapshot(false,text,"Failed");
   } catch(Exception ex) { Log.Error("Pause abgelehnt",ex); CheckStore(); BuildViews(); return Snapshot(false,ex.Message,"Failed"); }
  }

  Response Release(string deviceId) {
   var request=controller.OpenRequestFor(deviceId);
   if(request==null) { RefreshInventory(); return Snapshot(true,null,null,new[]{"Kein offener Vorgang für dieses Gerät."}); }
   try {
    var status=controller.Release(request.Value);
    Log.Info("Freigegeben: Tx "+request.Value);
    RefreshInventory();
    return Snapshot(true,null,null,new[]{String.Join(", ",status.Letters.Select(l=>l+":"))+" wieder verfügbar."});
   } catch(OperationException ex) {
    Log.Error("Freigabe offen Tx "+request.Value+": "+ex.OriginalError.Message);
    CheckStore(); RefreshInventory();
    return Snapshot(false,ex.OriginalError.Message,"Failed");
   }
  }

  Response ReleaseAll() {
   var messages=new List<string>(); bool ok=true; bool first=true;
   foreach(var s in controller.GetOpenStatuses()) {
    if(!first&&stagger>TimeSpan.Zero) Thread.Sleep(stagger); // Staggered spin-up of several HDDs.
    first=false;
    try { controller.Release(Guid.Parse(s.RequestId)); messages.Add(String.Join(", ",s.Letters.Select(l=>l+":"))+" wieder verfügbar."); Log.Info("Freigegeben: Tx "+s.RequestId); }
    catch(Exception ex) { ok=false; messages.Add(String.Join(", ",s.Letters.Select(l=>l+":"))+": "+ex.Message); Log.Error("Freigabe offen Tx "+s.RequestId,ex); CheckStore(); if(!controller.StoreHealthy) break; }
   }
   RefreshInventory();
   return Snapshot(ok,ok?null:"Nicht alle HDDs konnten freigegeben werden.",ok?null:"Failed",messages);
  }

  Response ImportLegacy(LegacyEntry[] entries) {
   var results=LegacyImport.Restore(entries,backend);
   foreach(var r in results) Log.Info("Altes Journal (0.3.x): "+r.Message);
   RefreshInventory();
   bool ok=results.All(r=>r.Restored);
   return Snapshot(ok,ok?null:"Nicht alle alten Zuordnungen konnten wiederhergestellt werden.",ok?null:"Legacy",results.Select(r=>r.Message));
  }

  void CheckStore() {
   if(ready&&!controller.StoreHealthy) {
    Log.Error("Zustandsspeicher nicht bestätigt. Dienst startet neu, um den Bestand neu einzulesen und abzugleichen.");
    ready=false; startupError="Zustandsspeicher-Schreibfehler. Der Dienst startet zur Wiederherstellung neu.";
    var fault=StoreFault; if(fault!=null) ThreadPool.QueueUserWorkItem(_=>fault());
   }
  }

  string Diagnostics() {
   var sb=new StringBuilder();
   sb.AppendLine("HDD Spindown Dienst "+Version+" · Windows "+Environment.OSVersion.Version);
   sb.AppendLine("Bereit: "+ready+" · Speicher bestätigt: "+(ready&&controller.StoreHealthy)+" · Energiesparmodus: "+suspended);
   sb.AppendLine("Letzte Erkennung (UTC): "+inventoryUtc);
   sb.AppendLine("Mindestlaufzeit: "+policy.MinimumRuntime.TotalMinutes+" min · Standby-Limit/24 h: "+policy.MaxStandbyPer24h);
   if(startupError!=null) sb.AppendLine("Startfehler: "+startupError);
   DeviceView[] v; lock(snap) v=views;
   foreach(var d in v) {
    sb.AppendLine();
    sb.AppendLine((d.Disk.Model??"?")+" · SN "+Paths.ShortSerial(d.Disk.Serial)+" · Nr. "+d.Disk.Number+" · "+(d.Present?"angeschlossen":"fehlt"));
    sb.AppendLine("  Buchstaben: "+String.Join(" ",d.Disk.Letters??new string[0])+" · zugelassen: "+d.Disk.Eligible+(d.Disk.Reason!=null?" ("+d.Disk.Reason+")":""));
    if(d.Operation!=null) sb.AppendLine("  Vorgang "+d.Operation.RequestId+": "+d.Operation.Phase+" · Zugriff "+d.Operation.Access+" · Hardware "+d.Operation.Hardware+" · Standby "+d.Operation.StandbyUtc+(d.Operation.Error!=null?" · Fehler: "+FirstLine(d.Operation.Error):""));
    if(d.PolicyBlock!=null) sb.AppendLine("  Schonregel: "+d.PolicyBlock);
   }
   return sb.ToString();
  }
  static string FirstLine(string s) { int i=s.IndexOfAny(new[]{'\r','\n'}); return i<0?s:s.Substring(0,i); }

  // ---------- Windows lifecycle ----------
  public void Suspend() {
   suspended=true; if(controller!=null) controller.SetSuspended(true);
   Log.Info("Energiesparmodus: keine neuen Vorgänge.");
   BuildViewsSafe();
  }
  public void Resume() {
   op.Wait();
   try {
    suspended=false; if(controller!=null) controller.SetSuspended(false);
    if(ready) {
     var failures=controller.Revalidate();
     Log.Info("Resume: Sperren abgeglichen. Abweichungen: "+failures.Length);
     foreach(var f in failures) Log.Warn("Resume-Abgleich Tx "+f.RequestId+": "+f.OriginalError.Message);
     CheckStore();
    }
    RefreshInventory();
   } catch(Exception ex) { Log.Error("Resume-Abgleich",ex); }
   finally { op.Release(); }
  }
  void BuildViewsSafe() { try { BuildViews(); } catch {} }

  // Normal stop or shutdown: restore original availability for every disk.
  public void Stop() {
   stopping=true;
   if(retry!=null) retry.Dispose();
   if(!op.Wait(TimeSpan.FromSeconds(60))) { Log.Error("Beenden: laufender Vorgang reagiert nicht. Wiederherstellung beim nächsten Start."); return; }
   try {
    if(ready) {
     var failures=controller.RecoverAll();
     Log.Info("Beenden: Buchstaben wiederhergestellt. Offen: "+failures.Length);
     foreach(var f in failures) Log.Warn("Beim Beenden offen Tx "+f.RequestId+": "+f.OriginalError.Message);
    }
   } catch(Exception ex) { Log.Error("Beenden",ex); }
   finally { op.Release(); }
  }
  public void Dispose() {
   try { if(controller!=null) controller.Dispose(); } catch(Exception ex) { Log.Error("Ressourcen beim Beenden",ex); }
   try { if(store!=null) store.Dispose(); } catch {}
  }
 }
}
