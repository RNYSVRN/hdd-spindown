using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace HddSpindown.Core {
 // A future Windows adapter must independently obtain trusted facts from an
 // opened device. No implementation in this milestone can access real drives.
 public interface IDeviceSession : IDisposable {
  Descriptor Inspect();
  IDisposable LockVolume(string volumeGuid);
  void RequestStandby();
 }
 public interface IDeviceBackend {
  IDeviceSession Open(string trustedDeviceId);
  string LookupLetter(string letter); // null = proven free; otherwise volume GUID
  void RemoveLetter(string letter);
  void RestoreLetter(string letter,string volumeGuid);
  void NotifyExplorer(string letter,bool added);
 }
 public sealed class OperationException : Exception {
  public string RequestId {get;private set;}
  public Exception OriginalError {get;private set;}
  public Exception[] CleanupErrors {get;private set;}
  public OperationException(string id,Exception primary,IEnumerable<Exception> cleanup)
   :base(L.F("Vorgang {0} fehlgeschlagen: {1}",id,primary.Message),primary) {
   RequestId=id; OriginalError=primary; CleanupErrors=cleanup.ToArray();
  }
 }
 public sealed class Controller : IDisposable {
  sealed class Live {
   public readonly List<IDisposable> Locks=new List<IDisposable>();
   public IDeviceSession Device;
  }
  readonly object gate=new object();
  readonly IStateStore store;
  readonly IDeviceBackend backend;
  readonly Dictionary<string,Live> live=new Dictionary<string,Live>();
  readonly List<IDeviceSession> cleanupDevices=new List<IDeviceSession>();
  readonly PowerPolicy policy;
  readonly Func<DateTime> clock;
  bool initialized,disposed,suspended;
  public Controller(IStateStore stateStore,IDeviceBackend devices,PowerPolicy powerPolicy=null,Func<DateTime> utcClock=null) {
   store=stateStore; backend=devices; policy=powerPolicy??PowerPolicy.None(); clock=utcClock??(()=>DateTime.UtcNow);
  }
  static DateTime? Parse(string value) { DateTime t; return value!=null&&DateTime.TryParse(value,null,System.Globalization.DateTimeStyles.RoundtripKind,out t)?t.ToUniversalTime():(DateTime?)null; }
  // Product rule: no rapid start/stop cycles. Unknown history never counts as fulfilled.
  void CheckPolicy(State state,string deviceId) {
   var mine=state.Transactions.Where(t=>t.Device.Identity.DeviceId==deviceId).ToArray();
   DateTime now=clock();
   var lastCompleted=mine.Where(t=>t.Phase==Phase.Completed).Select(t=>Parse(t.CompletedUtc)??Parse(t.UpdatedUtc)).Where(t=>t.HasValue).Select(t=>t.Value).DefaultIfEmpty(DateTime.MinValue).Max();
   if(lastCompleted!=DateTime.MinValue && policy.MinimumRuntime>TimeSpan.Zero) {
    var since=now-lastCompleted;
    if(since<TimeSpan.Zero) throw new PolicyException(L.Tok("Systemzeit liegt vor der letzten Freigabe. Mindestlaufzeit kann nicht bestätigt werden."));
    if(since<policy.MinimumRuntime) { var rest=policy.MinimumRuntime-since; throw new PolicyException(L.Tok("Mindestlaufzeit nach Freigabe noch nicht erreicht. Erneut möglich in {0} min.",Math.Ceiling(rest.TotalMinutes)),rest); }
   }
   int recent=mine.Count(t=>{ var s=Parse(t.StandbyUtc); return s.HasValue&&now-s.Value<TimeSpan.FromHours(24); });
   if(recent>=policy.MaxStandbyPer24h) throw new PolicyException(L.Tok("Limit von {0} Standby-Anforderungen in 24 Stunden erreicht. Häufige Wechsel belasten die HDD.",policy.MaxStandbyPer24h));
  }
  public void SetSuspended(bool value) { lock(gate) { suspended=value; } }
  void Ready() {
   if(disposed)throw new ObjectDisposedException("Controller");
   if(!initialized)throw new InvalidOperationException(L.T("Zuerst Wiederherstellungsbestand abgleichen."));
   if(!store.Healthy)throw new StoreException(L.T("Speicher nicht bestätigt. Neustart und Recovery erforderlich."));
  }
  static void Mark(Transaction tx,Phase phase) { tx.Phase=phase; tx.UpdatedUtc=DateTime.UtcNow.ToString("O"); }
  void Save(State state,Transaction tx,Phase phase) { Mark(tx,phase); store.Save(state); }
  static bool SameVolume(string a,string b) { Guid x,y; return Guid.TryParse(a,out x)&&Guid.TryParse(b,out y)&&x==y; }
  static void Verify(Descriptor expected,Descriptor actual,bool pausing=true) {
   actual.ValidateTopology();
   if(pausing)actual.Validate();
   else if(actual.Protected||actual.Encrypted)throw new InvalidOperationException(L.T("Wiederherstellung benötigt Klärung der geschützten Gerätekonfiguration."));
   if(!expected.SameTopology(actual))throw new InvalidOperationException(L.T("Geräteidentität oder Partitionstopologie hat sich geändert."));
   if(pausing && expected.Volumes.Any(v=>!v.Letters.OrderBy(l=>l).SequenceEqual(actual.Volumes.Single(o=>SameVolume(v.VolumeGuid,o.VolumeGuid)).Letters.OrderBy(l=>l))))
    throw new InvalidOperationException(L.T("Laufwerksbuchstaben haben sich während der Sperrung geändert."));
  }
  static void CheckSystemLetter(string letter) {
   string system=Path.GetPathRoot(Environment.SystemDirectory);
   if(!String.IsNullOrEmpty(system)&&String.Equals(letter,system.Substring(0,1),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(L.T("Der Windows-Laufwerksbuchstabe darf nicht verändert werden."));
  }
  // Startup never resumes an old sleep request. It reconciles access first.
  public OperationException[] Initialize() {
   lock(gate) {
    if(disposed)throw new ObjectDisposedException("Controller");
    if(initialized)throw new InvalidOperationException(L.T("Controller ist bereits initialisiert."));
    if(!store.Healthy)store.Repair();
    initialized=true;
    var failures=RecoverAllInternal();
    try { Compact(); } catch { /* Compaction is optional; a failed write poisons the store and is reported by Ready(). */ }
    return failures;
   }
  }
  public Status Pause(string trustedDeviceId,Guid request) {
   lock(gate) {
    Ready(); if(request==Guid.Empty)throw new ArgumentException(L.T("Request-ID fehlt."));
    State state=store.Read(); string id=request.ToString("D");
    Transaction old=state.Transactions.SingleOrDefault(t=>t.RequestId==id);
    if(old!=null) {
     if(old.Device.Identity.DeviceId!=trustedDeviceId)throw new InvalidOperationException(L.T("Request-ID gehört zu einem anderen Gerät."));
     return ToStatus(old);
    }
    if(suspended)throw new InvalidOperationException(L.T("Windows wechselt in den Energiesparmodus. Keine neuen Vorgänge."));
    if(state.Transactions.Any(t=>t.Device.Identity.DeviceId==trustedDeviceId&&t.Phase!=Phase.Completed))throw new InvalidOperationException(L.T("Gerät hat bereits einen offenen Vorgang."));
    CheckPolicy(state,trustedDeviceId);
    var resources=new Live(); Transaction tx=null;
    live.Add(id,resources); // Track ownership even if preparation/cleanup fails.
    try {
     resources.Device=backend.Open(trustedDeviceId);
     Descriptor descriptor=Codec.Copy(resources.Device.Inspect()); descriptor.Validate();
     if(descriptor.Identity.DeviceId!=trustedDeviceId)throw new InvalidOperationException(L.T("Geöffnetes Gerät passt nicht zum freigegebenen Schlüssel."));
     tx=new Transaction {RequestId=id,Device=descriptor,Hardware=HardwareState.Unknown,Mounts=new List<MountRecord>(),CreatedUtc=clock().ToString("O")};
     foreach(var volume in descriptor.Volumes) foreach(string letter in volume.Letters) {
      CheckSystemLetter(letter);
      if(!SameVolume(backend.LookupLetter(letter),volume.VolumeGuid))throw new InvalidOperationException(L.F("Ursprüngliche Zuordnung stimmt nicht: {0}",letter));
      tx.Mounts.Add(new MountRecord {Letter=letter,VolumeGuid=volume.VolumeGuid,Step=MountStep.Present});
     }
     state.Transactions.Add(tx); Save(state,tx,Phase.Prepared);
     Save(state,tx,Phase.Locking);
     foreach(var volume in descriptor.Volumes) {
      var handle=resources.Device.LockVolume(volume.VolumeGuid);
      if(handle==null)throw new InvalidOperationException(L.T("Backend hat keinen bestätigten Sperr-Handle geliefert."));
      resources.Locks.Add(handle);
     }
     Verify(descriptor,resources.Device.Inspect());
     Save(state,tx,Phase.Locked);
     foreach(var mount in tx.Mounts) {
      if(!SameVolume(backend.LookupLetter(mount.Letter),mount.VolumeGuid))throw new InvalidOperationException(L.F("Zuordnung vor Ausblenden geändert: {0}",mount.Letter));
      mount.Step=MountStep.RemoveIntent; Save(state,tx,Phase.Hiding);
      backend.RemoveLetter(mount.Letter);
      if(backend.LookupLetter(mount.Letter)!=null)throw new InvalidOperationException(L.F("Buchstabe nicht entfernt: {0}",mount.Letter));
      mount.Step=MountStep.Removed; store.Save(state);
      backend.NotifyExplorer(mount.Letter,false);
     }
     tx.StandbyUtc=clock().ToString("O"); // Counted even if the command fails: it was sent.
     Save(state,tx,Phase.StandbyPending);
     resources.Device.RequestStandby();
     tx.Hardware=HardwareState.StandbyRequested;
     Save(state,tx,Phase.Paused);
     return ToStatus(tx);
    } catch(Exception primary) {
     var cleanup=ReleaseHandles(resources);
     if(live.ContainsKey(id)&&resources.Locks.Count==0&&resources.Device==null)live.Remove(id);
     if(tx!=null&&store.Healthy) {
      // Use the last durable state, never an uncommitted mutated DTO.
      try {
       State saved=store.Read(); Transaction pending=saved.Transactions.SingleOrDefault(t=>t.RequestId==id);
       if(pending!=null) {
        pending.LastError=primary.ToString(); Save(saved,pending,Phase.RecoveryRequired);
        if(cleanup.Count==0) RecoverOne(saved,pending);
       }
      } catch(Exception rollback) {
       cleanup.Add(rollback);
       try { RecordRecoveryError(id,new AggregateException(L.T("Ursprünglicher Fehler und fehlgeschlagene Wiederherstellung."),primary,rollback)); }
       catch(Exception recording) { cleanup.Add(recording); }
      }
     }
     throw new OperationException(id,primary,cleanup);
    }
   }
  }
  List<Exception> ReleaseHandles(Live resources) {
   var errors=new List<Exception>();
   for(int i=resources.Locks.Count-1;i>=0;i--) {
    try { resources.Locks[i].Dispose(); resources.Locks.RemoveAt(i); } catch(Exception ex) { errors.Add(ex); }
   }
   if(resources.Device!=null)try { resources.Device.Dispose(); resources.Device=null; } catch(Exception ex) { errors.Add(ex); }
   return errors;
  }
  public Status Release(Guid request) {
   lock(gate) {
    Ready(); var state=store.Read(); string id=request.ToString("D");
    var tx=state.Transactions.SingleOrDefault(t=>t.RequestId==id);
    if(tx==null)throw new InvalidOperationException(L.T("Unbekannter Vorgang."));
    if(tx.Phase==Phase.Completed)return ToStatus(tx);
    try { RecoverOne(state,tx); }
    catch(Exception ex) {
     RecordRecoveryError(id,ex);
     throw new OperationException(id,ex,new Exception[0]);
    }
    return ToStatus(store.Read().Transactions.Single(t=>t.RequestId==id));
   }
  }
  void RecordRecoveryError(string id,Exception error) {
   if(!store.Healthy)return;
   try { var state=store.Read(); var tx=state.Transactions.Single(t=>t.RequestId==id); tx.LastError=error.ToString(); Save(state,tx,Phase.RecoveryRequired); }
   catch(Exception recording) { throw new OperationException(id,error,new[]{recording}); }
  }
  void RecoverOne(State state,Transaction tx) {
   foreach(var mount in tx.Mounts)CheckSystemLetter(mount.Letter);
   // Validate device BEFORE releasing existing handles or changing mounts.
   Live resources; bool held=live.TryGetValue(tx.RequestId,out resources);
   IDeviceSession inspection=held?resources.Device:backend.Open(tx.Device.Identity.DeviceId);
   if(inspection==null) inspection=backend.Open(tx.Device.Identity.DeviceId);
   bool ownsInspection=!held||inspection!=resources.Device;
   Exception original=null;
   try {
    Verify(tx.Device,inspection.Inspect(),false);
    // Check all letters before the first restore; one conflict holds this disk.
    foreach(var mount in tx.Mounts) {
     string current=backend.LookupLetter(mount.Letter);
     if(current!=null&&!SameVolume(current,mount.VolumeGuid))throw new InvalidOperationException(L.F("Buchstabenkonflikt: {0}. Keine fremde Zuordnung wird überschrieben.",mount.Letter));
    }
    Save(state,tx,Phase.Releasing);
    if(held) {
     var errors=ReleaseHandles(resources);
     if(errors.Count>0)throw new AggregateException(L.T("Sperren konnten nicht vollständig gelöst werden."),errors);
     live.Remove(tx.RequestId);
    }
    foreach(var mount in tx.Mounts) {
     string current=backend.LookupLetter(mount.Letter);
     if(current!=null&&!SameVolume(current,mount.VolumeGuid))throw new InvalidOperationException(L.F("Buchstabe während Freigabe neu belegt: {0}",mount.Letter));
     mount.Step=MountStep.RestoreIntent; store.Save(state);
     if(current==null)backend.RestoreLetter(mount.Letter,mount.VolumeGuid);
     if(!SameVolume(backend.LookupLetter(mount.Letter),mount.VolumeGuid))throw new InvalidOperationException(L.F("Wiederherstellung nicht bestätigt: {0}",mount.Letter));
     mount.Step=MountStep.Restored; store.Save(state);
     backend.NotifyExplorer(mount.Letter,true);
    }
    tx.Hardware=HardwareState.Unknown; tx.LastError=null; tx.CompletedUtc=clock().ToString("O");
    Save(state,tx,Phase.Completed);
   } catch(Exception ex) { original=ex; throw; }
   finally {
    if(ownsInspection)try { inspection.Dispose(); }
    catch(Exception closeError) {
     cleanupDevices.Add(inspection); // Retain ownership for a later cleanup attempt.
     if(original!=null)throw new AggregateException(L.T("Wiederherstellung und Ressourcenfreigabe fehlgeschlagen."),original,closeError);
     throw;
    }
   }
  }
  OperationException[] RecoverAllInternal() {
   var failures=new List<OperationException>();
   foreach(string id in store.Read().Transactions.Where(t=>t.Phase!=Phase.Completed).Select(t=>t.RequestId).ToArray()) {
    if(!store.Healthy)break;
    try { var state=store.Read(); RecoverOne(state,state.Transactions.Single(t=>t.RequestId==id)); }
    catch(Exception error) {
     try { RecordRecoveryError(id,error); failures.Add(new OperationException(id,error,new Exception[0])); }
     catch(OperationException combined) { failures.Add(combined); }
    }
   }
   return failures.ToArray();
  }
  public OperationException[] RecoverAll() { lock(gate) { Ready(); return RecoverAllInternal(); } }
  // Retries only transactions this process does not hold (e.g. a disk that was
  // missing at startup). Deliberately paused disks with live locks stay paused.
  public OperationException[] RecoverOrphaned() {
   lock(gate) {
    Ready();
    var failures=new List<OperationException>();
    foreach(string id in store.Read().Transactions.Where(t=>t.Phase!=Phase.Completed&&!live.ContainsKey(t.RequestId)).Select(t=>t.RequestId).ToArray()) {
     if(!store.Healthy)break;
     try { var state=store.Read(); RecoverOne(state,state.Transactions.Single(t=>t.RequestId==id)); }
     catch(Exception error) {
      try { RecordRecoveryError(id,error); failures.Add(new OperationException(id,error,new Exception[0])); }
      catch(OperationException combined) { failures.Add(combined); }
     }
    }
    return failures.ToArray();
   }
  }
  public bool HasOrphaned { get { lock(gate) { return store.Read().Transactions.Any(t=>t.Phase!=Phase.Completed&&!live.ContainsKey(t.RequestId)); } } }
  public bool StoreHealthy { get { return store.Healthy; } }
  Status ToStatus(Transaction tx) {
   Live value; bool known=live.TryGetValue(tx.RequestId,out value);
   return new Status {
    RequestId=tx.RequestId,DeviceId=tx.Device.Identity.DeviceId,Model=tx.Device.Identity.Model,Serial=tx.Device.Identity.Serial,
    Letters=tx.Mounts.Select(m=>m.Letter).OrderBy(l=>l).ToArray(),
    Phase=tx.Phase,Access=known&&value.Locks.Count>0?AccessState.Locked:tx.Phase==Phase.Completed?AccessState.Released:AccessState.Unknown,
    Hardware=known?tx.Hardware:HardwareState.Unknown,
    LettersRestored=tx.Phase==Phase.Completed&&tx.Mounts.All(m=>m.Step==MountStep.Restored),
    LettersMissing=tx.Mounts.Where(m=>m.Step!=MountStep.Restored&&m.Step!=MountStep.Present).Select(m=>m.Letter).OrderBy(l=>l).ToArray(),
    Error=tx.LastError,UpdatedUtc=tx.UpdatedUtc,StandbyUtc=tx.StandbyUtc
   };
  }
  public Status[] GetStatuses() { lock(gate) { if(disposed)throw new ObjectDisposedException("Controller"); return store.Read().Transactions.Select(ToStatus).ToArray(); } }
  public Status[] GetOpenStatuses() { lock(gate) { if(disposed)throw new ObjectDisposedException("Controller"); return store.Read().Transactions.Where(t=>t.Phase!=Phase.Completed).Select(ToStatus).ToArray(); } }
  public bool HasOpenTransactions { get { lock(gate) { return store.Read().Transactions.Any(t=>t.Phase!=Phase.Completed); } } }
  public Guid? OpenRequestFor(string deviceId) {
   lock(gate) { var tx=store.Read().Transactions.SingleOrDefault(t=>t.Phase!=Phase.Completed&&t.Device.Identity.DeviceId==deviceId); return tx==null?(Guid?)null:Guid.Parse(tx.RequestId); }
  }
  // Explains why a new pause would currently be refused, without touching hardware.
  public string PolicyBlock(string deviceId) {
   lock(gate) { try { CheckPolicy(store.Read(),deviceId); return null; } catch(PolicyException ex) { return ex.Message; } }
  }
  // After resume: the motor state is unknown again. Locks that can no longer be
  // proven valid lead back to the original availability (concept section 8).
  public OperationException[] Revalidate() {
   lock(gate) {
    Ready();
    var failures=new List<OperationException>();
    var state=store.Read();
    foreach(var tx in state.Transactions.Where(t=>t.Phase!=Phase.Completed).ToArray()) {
     Live resources; bool valid=live.TryGetValue(tx.RequestId,out resources)&&resources.Device!=null&&resources.Locks.Count==tx.Device.Volumes.Length;
     if(valid) {
      try {
       foreach(var l in resources.Locks.OfType<IVolumeLock>()) l.Probe();
       var facts=resources.Device.Inspect(); facts.ValidateTopology();
       if(!tx.Device.SameTopology(facts)) valid=false;
      } catch { valid=false; }
     }
     if(valid) { if(tx.Hardware!=HardwareState.Unknown) { tx.Hardware=HardwareState.Unknown; Save(state,tx,tx.Phase); } continue; }
     try { var fresh=store.Read(); RecoverOne(fresh,fresh.Transactions.Single(t=>t.RequestId==tx.RequestId)); }
     catch(Exception error) {
      try { RecordRecoveryError(tx.RequestId,error); failures.Add(new OperationException(tx.RequestId,error,new Exception[0])); }
      catch(OperationException combined) { failures.Add(combined); }
     }
     state=store.Read();
    }
    return failures.ToArray();
   }
  }
  // Keeps the journal below its size limit. Only long-completed records are removed;
  // open transactions and recent history (policy, replay protection) stay.
  void Compact() {
   if(!store.Healthy) return;
   var state=store.Read(); DateTime cutoff=clock()-policy.RetainCompleted;
   int removed=state.Transactions.RemoveAll(t=>t.Phase==Phase.Completed&&(Parse(t.CompletedUtc)??Parse(t.UpdatedUtc)??DateTime.MaxValue)<cutoff);
   if(removed>0) store.Save(state);
  }
  public void Dispose() {
   lock(gate) {
    if(disposed)return;
    var errors=new List<Exception>();
    foreach(var resources in live.Values)errors.AddRange(ReleaseHandles(resources));
    for(int i=cleanupDevices.Count-1;i>=0;i--)try { cleanupDevices[i].Dispose(); cleanupDevices.RemoveAt(i); } catch(Exception error) { errors.Add(error); }
    // Disposal performs no mount mutations; persisted pending operations survive.
    // A service stop must explicitly Release/RecoverAll before disposing.
    if(errors.Count>0)throw new AggregateException("Offene Ressourcen beim Beenden.",errors);
    live.Clear(); disposed=true;
   }
  }
 }
}
