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
     foreach(var f in failures) Log.Warn(L.F("Wiederherstellung offen ({0}): {1}",f.RequestId,f.OriginalError.Message));
     Log.Info(L.F("Wiederherstellungsbestand abgeglichen. Offene Fälle: {0}",failures.Length));
     ready=true;
    } catch(Exception ex) {
     startupError=L.Tok("Zustandsspeicher nicht verwendbar: {0} Recovery-Werkzeug verwenden.",ex.Message);
     Log.Error(L.T("Start im Recovery-Modus"),ex);
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
    Log.Info(L.F("Erneuter Wiederherstellungsversuch, offen: {0}",failures.Length));
    RefreshInventory();
   } catch(Exception ex) { Log.Error(L.T("Wiederherstellungsversuch"),ex); CheckStore(); }
   finally { op.Release(); }
  }

  void RefreshInventory() {
   try {
    var paused=new HashSet<string>(ready?controller.GetOpenStatuses().Select(s=>s.DeviceId):new string[0]);
    disks=inventory(id=>paused.Contains(id));
    inventoryUtc=DateTime.UtcNow.ToString("O");
   } catch(Exception ex) { Log.Error(L.T("Laufwerkserkennung"),ex); }
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
    list.Add(new DeviceView {Disk=new DiskInfo {DeviceId=s.DeviceId,Model=s.Model,Serial=s.Serial,Letters=s.Letters,Eligible=false,Reason=L.Tok("Nicht angeschlossen.")},Operation=s,Present=false});
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
   using(L.Use(request==null?null:L.Parse(request.Language))) return HandleCore(request,isAdmin);
  }
  Response HandleCore(Request request,bool isAdmin) {
   if(request==null||request.Version!=PipeNames.ProtocolVersion||request.Op==null||!Ops.All.Contains(request.Op)) return Snapshot(false,L.T("Ungültige Anfrage."),"Protocol");
   bool mutating=request.Op==Ops.Pause||request.Op==Ops.Release||request.Op==Ops.ReleaseAll||request.Op==Ops.ImportLegacy;
   if(mutating&&!isAdmin) return Snapshot(false,L.T("Nur Mitglieder der lokalen Administratorengruppe dürfen HDDs steuern."),"Denied");
   if(request.Op==Ops.Hello||request.Op==Ops.List) return Snapshot();
   if(request.Op==Ops.Diagnostics) { var r=Snapshot(); r.Text=Diagnostics(); return r; }
   if(stopping) return Snapshot(false,L.T("Dienst wird beendet."),"Stopping");
   if(!op.Wait(TimeSpan.FromSeconds(request.Op==Ops.Refresh?2:30))) return Snapshot(false,L.T("Ein anderer Vorgang läuft noch. Bitte kurz warten."),"Busy");
   try {
    if(request.Op==Ops.Refresh) { RefreshInventory(); return Snapshot(); }
    if(!ready) return Snapshot(false,startupError??L.T("Dienst startet noch."),"NotReady");
    if(request.Op==Ops.ImportLegacy) return ImportLegacy(request.Legacy);
    if(request.Op==Ops.ReleaseAll) return ReleaseAll();
    if(String.IsNullOrWhiteSpace(request.DeviceId)||request.DeviceId.Length>200) return Snapshot(false,L.T("Gerätekennung fehlt."),"Protocol");
    if(request.Op==Ops.Pause) return Pause(request);
    return Release(request.DeviceId);
   } finally { op.Release(); }
  }

  Response Pause(Request request) {
   Guid id; if(!Guid.TryParse(request.RequestId,out id)||id==Guid.Empty) return Snapshot(false,L.T("Request-ID fehlt."),"Protocol");
   if(suspended) return Snapshot(false,L.T("Windows wechselt in den Energiesparmodus. Keine neuen Vorgänge."),"Suspended");
   var disk=disks.FirstOrDefault(d=>d.DeviceId==request.DeviceId);
   bool known=controller.GetStatuses().Any(s=>s.RequestId==id.ToString("D"));
   if(!known) {
    if(disk==null) return Snapshot(false,L.T("Gerät nicht gefunden. Bitte neu einlesen."),"NotFound");
    if(!disk.Eligible) return Snapshot(false,disk.Reason??L.T("Gerät ist nicht zugelassen."),"NotEligible");
   }
   try {
    Log.Info(L.F("Pause angefordert: {0} SN {1} Tx {2}",disk==null?"?":disk.Model,Paths.ShortSerial(disk==null?"":disk.Serial),id));
    var status=controller.Pause(request.DeviceId,id);
    Log.Info(L.F("Pausiert: Tx {0} Phase {1}",id,status.Phase));
    BuildViews();
    return Snapshot(true,null,null,new[]{L.F("{0} gesperrt und ausgeblendet · Standby angefordert.",String.Join(", ",status.Letters.Select(l=>l+":")))});
   } catch(PolicyException ex) { BuildViews(); return Snapshot(false,ex.Message,"Policy"); }
   catch(OperationException ex) {
    Log.Error(L.F("Pause fehlgeschlagen Tx {0}: {1}",id,ex.OriginalError.Message)+(ex.CleanupErrors.Length>0?" | "+L.F("Rücksetzfehler: {0}",String.Join("; ",ex.CleanupErrors.Select(c=>c.Message))):""));
    CheckStore(); RefreshInventory();
    string text=ex.OriginalError.Message;
    var s=controller.StoreHealthy?controller.GetStatuses().FirstOrDefault(x=>x.RequestId==id.ToString("D")):null;
    if(s!=null&&s.Phase==Phase.Completed) text+=" "+L.T("Keine Sperre behalten; Laufwerksbuchstaben sind wiederhergestellt.");
    else text+=" "+L.T("Wiederherstellung offen – siehe Gerätestatus.");
    return Snapshot(false,text,"Failed");
   } catch(Exception ex) { Log.Error(L.T("Pause abgelehnt"),ex); CheckStore(); BuildViews(); return Snapshot(false,ex.Message,"Failed"); }
  }

  Response Release(string deviceId) {
   var request=controller.OpenRequestFor(deviceId);
   if(request==null) { RefreshInventory(); return Snapshot(true,null,null,new[]{L.T("Kein offener Vorgang für dieses Gerät.")}); }
   try {
    var status=controller.Release(request.Value);
    Log.Info(L.F("Freigegeben: Tx {0}",request.Value));
    RefreshInventory();
    return Snapshot(true,null,null,new[]{L.F("{0} wieder verfügbar.",String.Join(", ",status.Letters.Select(l=>l+":")))});
   } catch(OperationException ex) {
    Log.Error(L.F("Freigabe offen Tx {0}: {1}",request.Value,ex.OriginalError.Message));
    CheckStore(); RefreshInventory();
    return Snapshot(false,ex.OriginalError.Message,"Failed");
   }
  }

  Response ReleaseAll() {
   var messages=new List<string>(); bool ok=true; bool first=true;
   foreach(var s in controller.GetOpenStatuses()) {
    if(!first&&stagger>TimeSpan.Zero) Thread.Sleep(stagger); // Staggered spin-up of several HDDs.
    first=false;
    try { controller.Release(Guid.Parse(s.RequestId)); messages.Add(L.F("{0} wieder verfügbar.",String.Join(", ",s.Letters.Select(l=>l+":")))); Log.Info(L.F("Freigegeben: Tx {0}",s.RequestId)); }
    catch(Exception ex) { ok=false; messages.Add(String.Join(", ",s.Letters.Select(l=>l+":"))+": "+ex.Message); Log.Error(L.F("Freigabe offen Tx {0}",s.RequestId),ex); CheckStore(); if(!controller.StoreHealthy) break; }
   }
   RefreshInventory();
   return Snapshot(ok,ok?null:L.T("Nicht alle HDDs konnten freigegeben werden."),ok?null:"Failed",messages);
  }

  Response ImportLegacy(LegacyEntry[] entries) {
   var results=LegacyImport.Restore(entries,backend);
   foreach(var r in results) Log.Info(L.F("Altes Journal (0.3.x): {0}",r.Message));
   RefreshInventory();
   bool ok=results.All(r=>r.Restored);
   return Snapshot(ok,ok?null:L.T("Nicht alle alten Zuordnungen konnten wiederhergestellt werden."),ok?null:"Legacy",results.Select(r=>r.Message));
  }

  void CheckStore() {
   if(ready&&!controller.StoreHealthy) {
    Log.Error(L.T("Zustandsspeicher nicht bestätigt. Dienst startet neu, um den Bestand neu einzulesen und abzugleichen."));
    ready=false; startupError=L.Tok("Zustandsspeicher-Schreibfehler. Der Dienst startet zur Wiederherstellung neu.");
    var fault=StoreFault; if(fault!=null) ThreadPool.QueueUserWorkItem(_=>fault());
   }
  }

  string Diagnostics() {
   var sb=new StringBuilder();
   sb.AppendLine(L.F("HDD Spindown Dienst {0} · Windows {1}",Version,Environment.OSVersion.Version));
   sb.AppendLine(L.F("Bereit: {0} · Speicher bestätigt: {1} · Energiesparmodus: {2}",YesNo(ready),YesNo(ready&&controller.StoreHealthy),YesNo(suspended)));
   sb.AppendLine(L.F("Letzte Erkennung (UTC): {0}",inventoryUtc));
   sb.AppendLine(L.F("Mindestlaufzeit: {0} min · Standby-Limit/24 h: {1}",policy.MinimumRuntime.TotalMinutes,policy.MaxStandbyPer24h));
   if(startupError!=null) sb.AppendLine(L.F("Startfehler: {0}",L.Expand(startupError)));
   DeviceView[] v; lock(snap) v=views;
   foreach(var d in v) {
    sb.AppendLine();
    sb.AppendLine(L.F("{0} · SN {1} · Nr. {2} · {3}",L.Expand(d.Disk.Model??"?"),Paths.ShortSerial(d.Disk.Serial),d.Disk.Number,d.Present?L.T("angeschlossen"):L.T("fehlt")));
    sb.AppendLine("  "+L.F("Buchstaben: {0} · zugelassen: {1}",String.Join(" ",d.Disk.Letters??new string[0]),YesNo(d.Disk.Eligible))+(d.Disk.Reason!=null?" ("+L.Expand(d.Disk.Reason)+")":""));
    if(d.Operation!=null) sb.AppendLine("  "+L.F("Vorgang {0}: {1} · Zugriff {2} · Hardware {3} · Standby {4}",d.Operation.RequestId,d.Operation.Phase,d.Operation.Access,d.Operation.Hardware,d.Operation.StandbyUtc)+(d.Operation.Error!=null?" · "+L.F("Fehler: {0}",FirstLine(d.Operation.Error)):""));
    if(d.PolicyBlock!=null) sb.AppendLine("  "+L.F("Schonregel: {0}",L.Expand(d.PolicyBlock)));
   }
   return sb.ToString();
  }
  static string YesNo(bool b) { return b?L.T("ja"):L.T("nein"); }
  static string FirstLine(string s) { int i=s.IndexOfAny(new[]{'\r','\n'}); return i<0?s:s.Substring(0,i); }

  // ---------- Windows lifecycle ----------
  public void Suspend() {
   suspended=true; if(controller!=null) controller.SetSuspended(true);
   Log.Info(L.T("Energiesparmodus: keine neuen Vorgänge."));
   BuildViewsSafe();
  }
  public void Resume() {
   op.Wait();
   try {
    suspended=false; if(controller!=null) controller.SetSuspended(false);
    if(ready) {
     var failures=controller.Revalidate();
     Log.Info(L.F("Resume: Sperren abgeglichen. Abweichungen: {0}",failures.Length));
     foreach(var f in failures) Log.Warn(L.F("Resume-Abgleich Tx {0}: {1}",f.RequestId,f.OriginalError.Message));
     CheckStore();
    }
    RefreshInventory();
   } catch(Exception ex) { Log.Error(L.T("Resume-Abgleich"),ex); }
   finally { op.Release(); }
  }
  void BuildViewsSafe() { try { BuildViews(); } catch {} }

  // Normal stop or shutdown: restore original availability for every disk.
  public void Stop() {
   stopping=true;
   if(retry!=null) retry.Dispose();
   if(!op.Wait(TimeSpan.FromSeconds(60))) { Log.Error(L.T("Beenden: laufender Vorgang reagiert nicht. Wiederherstellung beim nächsten Start.")); return; }
   try {
    if(ready) {
     var failures=controller.RecoverAll();
     Log.Info(L.F("Beenden: Buchstaben wiederhergestellt. Offen: {0}",failures.Length));
     foreach(var f in failures) Log.Warn(L.F("Beim Beenden offen Tx {0}: {1}",f.RequestId,f.OriginalError.Message));
    }
   } catch(Exception ex) { Log.Error(L.T("Beenden des Dienstes"),ex); }
   finally { op.Release(); }
  }
  public void Dispose() {
   try { if(controller!=null) controller.Dispose(); } catch(Exception ex) { Log.Error(L.T("Ressourcen beim Beenden"),ex); }
   try { if(store!=null) store.Dispose(); } catch {}
  }
 }
}
