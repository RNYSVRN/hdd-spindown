using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HddSpindown.Core;

namespace HddSpindown.Tests {
 public static class CoreTests {
  static int assertions,cases;
  static string root;
  static void Check(bool value,string label) { assertions++; if(!value)throw new Exception(label); }
  static T Throws<T>(Action action,string label) where T:Exception {
   try { action(); } catch(T ex) { assertions++; return ex; }
   throw new Exception("Expected "+typeof(T).Name+": "+label);
  }
  static string Dir(string name) { string dir=Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); cases++; return dir; }
  static void Original(FakeBackend b) { Check(b.Maps.Count==2&&b.Maps["D"]==FakeBackend.V1&&b.Maps["E"]==FakeBackend.V2,"Original mappings"); }
  static void Normal() {
   var b=new FakeBackend(); Guid id=Guid.NewGuid();
   using(var store=new DurableStore(Dir("normal"))) using(var core=new Controller(store,b)) {
    Check(core.Initialize().Length==0,"Clean init");
    b.BeforeRemove=letter=> {
     var record=store.Read().Transactions.Single().Mounts.Single(m=>m.Letter==letter);
     Check(record.Step==MountStep.RemoveIntent,"Removal intention must be durable");
    };
    var status=core.Pause(FakeBackend.DeviceKey,id);
    Check(status.Phase==Phase.Paused&&status.Access==AccessState.Locked&&status.Hardware==HardwareState.StandbyRequested,"Pause facts separated");
    Check(b.Maps.Count==0&&b.OpenLocks==2&&b.StandbyCount==1,"Pause holds all locks");
    core.Pause(FakeBackend.DeviceKey,id); Check(b.StandbyCount==1,"Duplicate request does not repeat standby");
    Throws<InvalidOperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"No second active transaction");
    var released=core.Release(id); Original(b);
    Check(released.LettersRestored&&released.Access==AccessState.Released&&released.Hardware==HardwareState.Unknown,"Release verified");
    Check(b.OpenLocks==0&&b.OpenHandles==0,"No handles after release");
    core.Release(id); Check(b.StandbyCount==1,"Release idempotent");
    var copy=store.Read(); copy.Transactions.Clear(); Check(store.Read().Transactions.Count==1,"Store does not leak mutable state");
   }
  }
  static void Failures() {
   foreach(string point in new[]{"open","before-lock:"+FakeBackend.V2,"before-remove:D","after-remove:D","after-remove:E","before-standby","after-standby","notify:D:False"}) {
    var b=new FakeBackend {FailAt=point};
    using(var store=new DurableStore(Dir("failure"))) using(var core=new Controller(store,b)) {
     core.Initialize(); var error=Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),point);
     Check(error.OriginalError.Message.Contains(point),"Preserve primary error");
     Original(b); Check(b.OpenLocks==0&&b.OpenHandles==0,"Rollback resources: "+point);
     if(point!="after-standby")Check(b.StandbyCount==0,"No premature standby: "+point);
    }
   }
   var failure=new FakeBackend {FailAt="after-remove:D"};
   using(var store=new DurableStore(Dir("double-error"))) using(var core=new Controller(store,failure)) {
    core.Initialize();
    failure.Checkpoint=p=> { if(p=="before-restore:D")throw new IOException("secondary restore failure"); };
    var ex=Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Both failures");
    Check(ex.OriginalError.Message.Contains("after-remove:D")&&ex.CleanupErrors.Length>0,"Primary and rollback errors both retained");
    Check(core.GetStatuses().Single().Phase==Phase.RecoveryRequired&&!core.GetStatuses().Single().LettersRestored,"No false released status");
    failure.FailAt=null; failure.Checkpoint=delegate {};
    Check(core.RecoverAll().Length==0,"Retry recovery"); Original(failure);
   }
  }
  static void IdentityAndConflict() {
   foreach(string kind in new[]{"protected","ssd","encrypted","filesystem","topology"}) {
    var b=new FakeBackend();
    if(kind=="protected")b.Facts.Protected=true;
    if(kind=="ssd")b.Facts.QualifiedSataHdd=false;
    if(kind=="encrypted")b.Facts.Encrypted=true;
    if(kind=="filesystem")b.Facts.HealthyNtfs=false;
    if(kind=="topology")b.Facts.Volumes[1].Offset=2000;
    using(var store=new DurableStore(Dir(kind))) using(var core=new Controller(store,b)) {
     core.Initialize(); Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),kind);
     Check(!b.Trace.Any(p=>p.StartsWith("before-remove:"))&&b.StandbyCount==0,"Rejected descriptor has no mutations");
    }
   }
   var device=new FakeBackend(); Guid id=Guid.NewGuid();
   using(var store=new DurableStore(Dir("identity-change"))) using(var core=new Controller(store,device)) {
    core.Initialize(); core.Pause(FakeBackend.DeviceKey,id);
    device.Facts.Identity.Serial="WRONG";
    Throws<OperationException>(()=>core.Release(id),"Device changed");
    Check(device.Maps.Count==0&&device.OpenLocks==2,"Identity mismatch causes no mount or unlock");
    device.Facts.Identity.Serial="TEST-SERIAL"; device.Maps.Add("D",FakeBackend.V2);
    Throws<OperationException>(()=>core.Release(id),"Letter occupied");
    Check(device.Maps["D"]==FakeBackend.V2&&device.OpenLocks==2,"No overwrite / retain locks during conflict");
    device.Maps.Remove("D"); core.Release(id); Original(device);
   }
   var changing=new FakeBackend(); int inspect=0;
   changing.BeforeInspect=()=> { if(++inspect==2)changing.Facts.Volumes[1].Length--; };
   using(var store=new DurableStore(Dir("topology-after-lock"))) using(var core=new Controller(store,changing)) {
    core.Initialize(); Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Revalidate after locking");
    Check(!changing.Trace.Any(p=>p.StartsWith("before-remove:")),"Changed topology never hidden");
   }
   var mountsChanged=new FakeBackend(); inspect=0;
   mountsChanged.BeforeInspect=()=> { if(++inspect==2)mountsChanged.Facts.Volumes[1].Letters=new[]{"F"}; };
   using(var store=new DurableStore(Dir("mounts-after-lock")))using(var core=new Controller(store,mountsChanged)) {
    core.Initialize(); Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Letter changed while acquiring locks");
    Check(!mountsChanged.Trace.Any(p=>p.StartsWith("before-remove:")),"Concurrent mount changes prevent hiding");
   }
  }
  static void StoreFailures() {
   string dir=Dir("store-partial"); bool armed=false;
   using(var store=new DurableStore(dir,p=> {if(armed&&p=="after-replace:state-A.json")throw new IOException("write failure");})) {
    Throws<IOException>(()=> { using(var other=new DurableStore(dir)) {} },"Single writer across instances");
    armed=true; Throws<StoreException>(()=>store.Save(store.Read()),"Partial state write");
    Check(!store.Healthy,"Poison after failed persistence");
    Throws<StoreException>(()=>store.Save(store.Read()),"No later writes");
    Throws<StoreException>(()=>store.Repair(),"Must reopen after I/O fault");
   }
   using(var reopened=new DurableStore(dir)) {
    Check(!reopened.Healthy&&reopened.Read().Revision==1,"Keep latest valid intent after torn mirror update");
    reopened.Repair(); Check(reopened.Healthy,"Repair mirror before mutations");
   }
   File.WriteAllText(Path.Combine(dir,"state-B.json"),"corrupt");
   using(var recovered=new DurableStore(dir)) { Check(!recovered.Healthy,"Detect corrupt copy"); recovered.Repair(); Check(recovered.Healthy,"Repair from valid current copy"); }
   File.WriteAllText(Path.Combine(dir,"state-A.json"),"broken A"); File.WriteAllText(Path.Combine(dir,"state-B.json"),"broken B");
   Throws<StoreException>(()=> { using(var invalid=new DurableStore(dir)) {} },"No guessing from older backups");
   Check(File.ReadAllText(Path.Combine(dir,"state-A.json"))=="broken A","Preserve damaged evidence");
   dir=Dir("stale-writer");
   using(var store=new DurableStore(dir)) {
    var a=store.Read(); var b=store.Read(); store.Save(a);
    Throws<StoreException>(()=>store.Save(b),"Revision check"); Check(store.Healthy,"Stale DTO does not poison healthy store");
   }
   string first=Dir("fork-A"),second=Dir("fork-B");
   using(var a=new DurableStore(first))a.Save(a.Read());
   using(var b=new DurableStore(second)) {
    var s=b.Read(); s.Transactions.Add(new Transaction {RequestId=Guid.NewGuid().ToString("D"),Device=FakeBackend.MakeDescriptor(),Phase=Phase.Prepared,Mounts=new System.Collections.Generic.List<MountRecord> {new MountRecord {Letter="D",VolumeGuid=FakeBackend.V1},new MountRecord {Letter="E",VolumeGuid=FakeBackend.V2}}}); b.Save(s);
   }
   File.Copy(Path.Combine(second,"state-A.json"),Path.Combine(first,"state-B.json"),true);
   Throws<StoreException>(()=> { using(var fork=new DurableStore(first)) {} },"Two valid conflicting revisions fail closed");
  }
  static void PersistenceDuringPause() {
   foreach(int target in Enumerable.Range(1,9)) {
    string dir=Dir("pause-store-fault"); bool armed=false; int writes=0; var b=new FakeBackend();
    using(var store=new DurableStore(dir,p=> {if(armed&&p=="after-replace:state-A.json"&&++writes==target)throw new IOException("disk full / interrupted write");})) {
     using(var core=new Controller(store,b)) {
      core.Initialize(); armed=true;
      Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Persistence boundary "+target);
      Check(!store.Healthy,"No further hardware work after store fault");
     }
    }
    Check(b.OpenLocks==0&&b.OpenHandles==0,"Release process resources after unconfirmed write");
    using(var store=new DurableStore(dir)) using(var core=new Controller(store,b)) {
     Check(core.GetStatuses().All(s=>s.Access!=AccessState.Locked&&s.Hardware==HardwareState.Unknown),"Restart never claims inherited lock or motor state");
     Check(core.Initialize().Length==0,"Recover partial persistent operation"); Original(b);
    }
   }
  }
  static void ConcurrentRequests() {
   var b=new FakeBackend();
   using(var store=new DurableStore(Dir("concurrent"))) using(var core=new Controller(store,b)) {
    core.Initialize(); Guid id=Guid.NewGuid();
    Task.WaitAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>core.Pause(FakeBackend.DeviceKey,id))).ToArray());
    Check(b.StandbyCount==1&&store.Read().Transactions.Count==1,"Concurrent repeated request executes once");
    core.Release(id); Original(b);
   }
  }
  static void HealthRecovery() {
   var b=new FakeBackend(); Guid id=Guid.NewGuid();
   using(var store=new DurableStore(Dir("degraded-health-recovery"))) using(var core=new Controller(store,b)) {
    core.Initialize(); core.Pause(FakeBackend.DeviceKey,id);
    b.Facts.HealthyNtfs=false;
    core.Release(id); Original(b);
    Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Unhealthy disk can be recovered but not paused again");
    Check(b.StandbyCount==1,"Recovery does not issue new hardware power requests");
   }
  }
  static void ReleaseWriteFailures() {
   foreach(int boundary in Enumerable.Range(1,6)) {
    string dir=Dir("release-write-fault"); bool armed=false; int writes=0; var b=new FakeBackend(); Guid id=Guid.NewGuid();
    using(var store=new DurableStore(dir,p=> { if(armed&&p=="after-replace:state-A.json"&&++writes==boundary)throw new IOException("release journal failure"); }))
    using(var core=new Controller(store,b)) {
     core.Initialize(); core.Pause(FakeBackend.DeviceKey,id); armed=true;
     Throws<OperationException>(()=>core.Release(id),"Release boundary "+boundary);
     Check(!store.Healthy,"Release persistence failure blocks next request");
     int mutations=b.Trace.Count(p=>p.StartsWith("after-restore:")||p.StartsWith("after-remove:"));
     Throws<StoreException>(()=>core.Release(id),"No retry without rereading partial store");
     Check(mutations==b.Trace.Count(p=>p.StartsWith("after-restore:")||p.StartsWith("after-remove:")),"No mutations after poisoned store");
    }
    using(var store=new DurableStore(dir))using(var core=new Controller(store,b)) { Check(core.Initialize().Length==0,"Recovery after release write fault"); Original(b); }
   }
  }
  static void CleanupOwnership() {
   bool armed=false; var b=new FakeBackend {FailAt="before-close"};
   using(var store=new DurableStore(Dir("preparation-cleanup-failure"),p=> { if(armed&&p=="before-write:state-A.json")throw new IOException("prepare write failed"); })) {
    var core=new Controller(store,b); core.Initialize(); armed=true;
    var failure=Throws<OperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Preparation and close both fail");
    Check(failure.OriginalError is StoreException&&failure.CleanupErrors.Length==1,"Keep original write error and close error");
    Check(b.OpenHandles==1,"Failed close remains owned");
    b.FailAt=null; core.Dispose(); Check(b.OpenHandles==0,"Dispose retries owned resource from failed preparation");
   }
   string dir=Dir("recovery-cleanup-failure"); b=new FakeBackend();
   using(var store=new DurableStore(dir))using(var core=new Controller(store,b)) { core.Initialize(); core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()); }
   b.Facts.Identity.Serial="WRONG"; b.FailAt="before-close";
   using(var store=new DurableStore(dir))using(var core=new Controller(store,b)) {
    var failures=core.Initialize();
    Check(failures.Length==1&&failures[0].OriginalError is AggregateException,"Recovery preserves validation and close failures");
    var both=(AggregateException)failures[0].OriginalError;
    Check(both.InnerExceptions.Count==2&&both.InnerExceptions[0].Message.Contains("Geräteidentität"),"Original identity error is not masked by cleanup");
    b.FailAt=null;
   }
   Check(b.OpenHandles==0,"Failed recovery inspection close remains owned for retry");
  }
  sealed class Router : IDeviceBackend {
   public FakeBackend A,B;
   FakeBackend ForLetter(string l) { return l=="D"||l=="E"?A:B; }
   public IDeviceSession Open(string key) { return (key==A.Facts.Identity.DeviceId?A:B).Open(key); }
   public string LookupLetter(string l) { return ForLetter(l).LookupLetter(l); }
   public void RemoveLetter(string l) { ForLetter(l).RemoveLetter(l); }
   public void RestoreLetter(string l,string v) { ForLetter(l).RestoreLetter(l,v); }
   public void NotifyExplorer(string l,bool add) { ForLetter(l).NotifyExplorer(l,add); }
  }
  static void IndependentDevices() {
   var a=new FakeBackend(); var b=new FakeBackend(); b.Facts.Identity.DeviceId="TEST-DEVICE-2"; b.Facts.Identity.Serial="SECOND"; b.Facts.Identity.DiskGuid=Guid.NewGuid().ToString("D");
   b.Maps.Clear();
   for(int i=0;i<2;i++) { string letter=i==0?"F":"G"; string volume=Guid.NewGuid().ToString("D"); b.Facts.Volumes[i].VolumeGuid=volume; b.Facts.Volumes[i].Letters=new[]{letter}; b.Maps.Add(letter,volume); }
   var router=new Router {A=a,B=b}; string dir=Dir("independent-devices"); Guid aid=Guid.NewGuid(),bid=Guid.NewGuid();
   using(var store=new DurableStore(dir))using(var core=new Controller(store,router)) { core.Initialize(); core.Pause(a.Facts.Identity.DeviceId,aid); core.Pause(b.Facts.Identity.DeviceId,bid); }
   b.FailAt="open";
   using(var store=new DurableStore(dir))using(var core=new Controller(store,router)) {
    Check(core.Initialize().Length==1,"One missing device must not block other recovery"); Original(a);
    Check(b.Maps.Count==0,"Missing device stays pending");
    core.Pause(a.Facts.Identity.DeviceId,Guid.NewGuid());
    Check(a.StandbyCount==2,"Unrelated device remains available for new operations");
    b.FailAt=null; Check(core.RecoverAll().Length==0,"Reconnected device can recover");
    Original(a); Check(b.Maps.Count==2&&b.StandbyCount==1,"Recovery never repeats old sleep request");
   }
  }
  static void Policy() {
   var b=new FakeBackend(); DateTime now=new DateTime(2026,11,12,8,0,0,DateTimeKind.Utc);
   var policy=new PowerPolicy {MinimumRuntime=TimeSpan.FromMinutes(30),MaxStandbyPer24h=2};
   using(var store=new DurableStore(Dir("policy"))) using(var core=new Controller(store,b,policy,()=>now)) {
    core.Initialize(); Guid first=Guid.NewGuid();
    core.Pause(FakeBackend.DeviceKey,first); now=now.AddMinutes(5); core.Release(first);
    Check(core.PolicyBlock(FakeBackend.DeviceKey)!=null,"Minimum runtime reported");
    var tooEarly=Throws<PolicyException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Minimum runtime enforced");
    Check(tooEarly.RetryAfter.HasValue&&tooEarly.RetryAfter.Value<=TimeSpan.FromMinutes(30),"Retry time explained");
    Check(b.StandbyCount==1&&b.Maps.Count==2,"Policy rejection causes no hardware action");
    now=now.AddMinutes(31); Guid second=Guid.NewGuid(); core.Pause(FakeBackend.DeviceKey,second); now=now.AddMinutes(1); core.Release(second);
    now=now.AddMinutes(31);
    Throws<PolicyException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"Daily standby limit");
    now=now.AddHours(24); Check(core.PolicyBlock(FakeBackend.DeviceKey)==null,"Limit window slides");
    core.SetSuspended(true);
    Throws<InvalidOperationException>(()=>core.Pause(FakeBackend.DeviceKey,Guid.NewGuid()),"No new operation while suspending");
    core.SetSuspended(false);
    Check(core.OpenRequestFor(FakeBackend.DeviceKey)==null&&!core.HasOpenTransactions,"No open transaction after release");
   }
   string dir=Dir("compaction"); now=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc); b=new FakeBackend();
   using(var store=new DurableStore(dir)) using(var core=new Controller(store,b,new PowerPolicy(),()=>now)) { core.Initialize(); Guid id=Guid.NewGuid(); core.Pause(FakeBackend.DeviceKey,id); core.Release(id); }
   now=now.AddDays(91);
   using(var store=new DurableStore(dir)) using(var core=new Controller(store,b,new PowerPolicy(),()=>now)) { core.Initialize(); Check(store.Read().Transactions.Count==0,"Old completed records compacted"); }
  }
  static void Resume() {
   var b=new FakeBackend(); Guid id=Guid.NewGuid();
   using(var store=new DurableStore(Dir("resume"))) using(var core=new Controller(store,b)) {
    core.Initialize(); core.Pause(FakeBackend.DeviceKey,id);
    Check(core.Revalidate().Length==0,"Valid locks survive resume");
    var s=core.GetStatuses().Single();
    Check(s.Access==AccessState.Locked&&s.Hardware==HardwareState.Unknown&&b.Maps.Count==0,"Resume keeps manual sleep wish, motor state unknown");
    Check(core.OpenRequestFor(FakeBackend.DeviceKey)==id&&core.HasOpenTransactions,"Open request discoverable by device");
    b.Facts.Volumes[1].Length--; // Topology no longer provable -> restore availability.
    var failures=core.Revalidate();
    Check(failures.Length==1&&b.OpenLocks==2,"Changed device after resume is not blindly unlocked");
    b.Facts.Volumes[1].Length++;
    Check(core.Revalidate().Length==0,"Valid again after correction");
    core.Release(id); Original(b);
   }
  }
  static void Localization() {
   cases++;
   Check(L.T("Pausiert.")=="Pausiert.","German is the source language");
   using(L.Use(Lang.En)) {
    Check(L.T("Pausiert.")=="Paused.","English catalog");
    Check(L.F("Buchstabe nicht entfernt: {0}","D")=="Drive letter not removed: D","Formatted translation");
    Check(L.T("Nicht im Katalog")=="Nicht im Katalog","Unknown text falls back to source");
   }
   string token=L.Tok("Mindestlaufzeit nach Freigabe noch nicht erreicht. Erneut möglich in {0} min.",18)+" "+L.Tok("Pausiert.");
   Check(L.Expand(token).StartsWith("Mindestlaufzeit")&&L.Expand(token).Contains("18 min")&&L.Expand(token).EndsWith("Pausiert."),"Token expands in German");
   using(L.Use(Lang.En)) Check(L.Expand(token)=="Minimum runtime after release not reached yet. Possible again in 18 min. Paused.","Token expands in the client's language");
   Check(L.Expand("ohne Token")=="ohne Token","Plain text unchanged");
   Check(L.Parse("en-US")==Lang.En&&L.Parse("de")==Lang.De&&L.Parse("fr")==null&&L.Parse(null)==null,"Language codes");
  }
  static void Child(string dir,string cut) {
   Directory.CreateDirectory(dir); int writes=0;
   var backend=new FakeBackend(Path.Combine(dir,"fake-maps.json"));
   backend.Checkpoint=p=> { if(p==cut)Environment.Exit(73); };
   using(var store=new DurableStore(Path.Combine(dir,"state"),p=> { if(p=="after-replace:state-A.json"&&cut=="write-"+(++writes))Environment.Exit(73); }))
   using(var controller=new Controller(store,backend)) {
    controller.Initialize(); Guid id=Guid.NewGuid(); controller.Pause(FakeBackend.DeviceKey,id);
    if(cut.StartsWith("before-restore:")||cut.StartsWith("after-restore:")||cut.StartsWith("after-unlock:"))controller.Release(id);
   }
   Environment.Exit(74);
  }
  static void CrashRecovery() {
   var cuts=new[]{"after-lock:"+FakeBackend.V1,"after-lock:"+FakeBackend.V2,"before-remove:D","after-remove:D","after-remove:E","before-standby","after-standby","after-unlock:"+FakeBackend.V1,"before-restore:D","after-restore:D","after-restore:E"}.Concat(Enumerable.Range(2,9).Select(n=>"write-"+n));
   foreach(string cut in cuts) {
    string dir=Dir("process-crash"); string exe=Environment.ProcessPath;
    using(var child=Process.Start(new ProcessStartInfo(exe,"--crash \""+dir+"\" \""+cut+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
     if(!child.WaitForExit(20000)) { child.Kill(); throw new Exception("Child timed out: "+cut); }
     Check(child.ExitCode==73,"Child must terminate at intended crash boundary: "+cut+" ("+child.ExitCode+")");
    }
    var backend=new FakeBackend(Path.Combine(dir,"fake-maps.json"));
    using(var store=new DurableStore(Path.Combine(dir,"state")))using(var core=new Controller(store,backend)) {
     Check(core.GetStatuses().All(s=>s.Access!=AccessState.Locked),"Persistent state does not prove live lock");
     var errors=core.Initialize();
     if(errors.Length>0) File.WriteAllText(Path.Combine(dir,"recovery-error.txt"),String.Join("\r\n",errors.Select(e=>e.ToString())));
     Check(errors.Length==0,"Recovery after real child process termination: "+cut);
     Original(backend); Check(backend.StandbyCount==0,"Recovery never reissues standby");
    }
   }
  }
  public static int Main(string[] args) {
   L.Default=Lang.De; // assertions check German source messages
   if(args.Length==3&&args[0]=="--crash") { Child(args[1],args[2]); return 74; }
   if(args.Length==2&&args[0]=="--recover-simulation") {
    var backend=new FakeBackend(Path.Combine(args[1],"fake-maps.json"));
    using(var store=new DurableStore(Path.Combine(args[1],"state")))using(var core=new Controller(store,backend)) {
     var failures=core.Initialize(); foreach(var failure in failures)Console.WriteLine(failure);
     Console.WriteLine("Simulated recovery failures: "+failures.Length); return failures.Length==0?0:1;
    }
   }
   if(args.Length==2&&args[0]=="--crash-suite") {
    try { root=Path.GetFullPath(args[1]); Directory.CreateDirectory(root); for(int i=0;i<3;i++)CrashRecovery(); Console.WriteLine(assertions+" crash assertions passed across "+cases+" scenarios."); return 0; }
    catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
   }
   try {
    root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
    Normal(); Failures(); IdentityAndConflict(); StoreFailures(); PersistenceDuringPause(); ReleaseWriteFailures(); CleanupOwnership(); ConcurrentRequests(); HealthRecovery(); IndependentDevices(); Policy(); Resume(); Localization(); CrashRecovery();
    string report=assertions+" assertions passed across "+cases+" isolated scenarios.\r\n20 child-process crash boundaries exercised.\r\nOnly simulated devices/mounts; no native storage calls.\r\nArtifacts: "+root;
    Console.WriteLine(report); File.WriteAllText(Path.Combine(root,"results.txt"),report); return 0;
   } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
  }
 }
}
