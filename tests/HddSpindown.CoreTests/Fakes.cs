using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HddSpindown.Core;

namespace HddSpindown.Tests {
 public sealed class FakeBackend : IDeviceBackend {
  public const string DeviceKey="TEST-DEVICE-1";
  public const string V1="11111111-1111-1111-1111-111111111111",V2="22222222-2222-2222-2222-222222222222";
  public Descriptor Facts=MakeDescriptor();
  public readonly Dictionary<string,string> Maps=new Dictionary<string,string>();
  public readonly List<string> Trace=new List<string>();
  public Action<string> Checkpoint=delegate {};
  public Action BeforeInspect=delegate {};
  public Action<string> BeforeRemove=delegate {};
  public string FailAt;
  public int StandbyCount,OpenHandles,OpenLocks;
  readonly string persistedMaps;
  public FakeBackend(string mapFile=null) {
   persistedMaps=mapFile;
   if(mapFile!=null&&File.Exists(mapFile)) foreach(var pair in Codec.Serializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(mapFile))) Maps.Add(pair.Key,pair.Value);
   else { Maps.Add("D",V1); Maps.Add("E",V2); Persist(); }
  }
  public static Descriptor MakeDescriptor() {
   return new Descriptor {
    Identity=new Identity {DeviceId=DeviceKey,Serial="TEST-SERIAL",Model="TEST-MODEL",Firmware="TEST-FIRMWARE",DiskGuid="33333333-3333-3333-3333-333333333333",Capacity=1000000},
    QualifiedSataHdd=true,HealthyNtfs=true,
    Volumes=new[]{new Volume {VolumeGuid=V1,PartitionGuid="44444444-4444-4444-4444-444444444444",Offset=1000,Length=300000,Letters=new[]{"D"}},new Volume {VolumeGuid=V2,PartitionGuid="55555555-5555-5555-5555-555555555555",Offset=400000,Length=300000,Letters=new[]{"E"}}}
   };
  }
  void Persist() {
   if(persistedMaps==null)return;
   byte[] bytes=System.Text.Encoding.UTF8.GetBytes(Codec.Serializer().Serialize(Maps));
   using(var stream=new FileStream(persistedMaps,FileMode.Create,FileAccess.Write,FileShare.Read)) { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
  }
  public void Hit(string point) { Trace.Add(point); Checkpoint(point); if(FailAt==point)throw new IOException("Injected: "+point); }
  public IDeviceSession Open(string key) { Hit("open"); if(key!=Facts.Identity.DeviceId)throw new IOException("Device missing"); OpenHandles++; return new Device(this); }
  public string LookupLetter(string letter) { Hit("lookup:"+letter); string v; return Maps.TryGetValue(letter,out v)?v:null; }
  public void RemoveLetter(string letter) {
   BeforeRemove(letter); Hit("before-remove:"+letter);
   if(OpenLocks!=Facts.Volumes.Length)throw new Exception("SAFETY: remove without all locks");
   Maps.Remove(letter); Persist(); Hit("after-remove:"+letter);
  }
  public void RestoreLetter(string letter,string volume) {
   Hit("before-restore:"+letter);
   if(Maps.ContainsKey(letter))throw new Exception("SAFETY: overwrite occupied letter");
   Maps.Add(letter,volume); Persist(); Hit("after-restore:"+letter);
  }
  public void NotifyExplorer(string letter,bool added) { Hit("notify:"+letter+":"+added); }
  sealed class Device : IDeviceSession {
   readonly FakeBackend owner; bool disposed;
   public Device(FakeBackend parent) { owner=parent; }
   public Descriptor Inspect() { if(disposed)throw new ObjectDisposedException("fake device"); owner.Hit("inspect"); owner.BeforeInspect(); return Codec.Copy(owner.Facts); }
   public IDisposable LockVolume(string volume) { owner.Hit("before-lock:"+volume); owner.OpenLocks++; var result=new VolumeLock(owner,volume); owner.Hit("after-lock:"+volume); return result; }
   public void RequestStandby() {
    owner.Hit("before-standby");
    if(owner.OpenLocks!=owner.Facts.Volumes.Length||owner.Maps.Count!=0)throw new Exception("SAFETY: standby before all locks/removals");
    owner.StandbyCount++; owner.Hit("after-standby");
   }
   public void Dispose() { if(disposed)return; owner.Hit("before-close"); disposed=true; owner.OpenHandles--; owner.Hit("after-close"); }
  }
  sealed class VolumeLock : IDisposable {
   readonly FakeBackend owner; readonly string volume; bool disposed;
   public VolumeLock(FakeBackend parent,string id) { owner=parent; volume=id; }
   public void Dispose() { if(disposed)return; owner.Hit("before-unlock:"+volume); disposed=true; owner.OpenLocks--; owner.Hit("after-unlock:"+volume); }
  }
 }
}
