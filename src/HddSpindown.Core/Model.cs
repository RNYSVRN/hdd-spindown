using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HddSpindown.Core {
 public enum Phase { Prepared, Locking, Locked, Hiding, StandbyPending, Paused, Releasing, RecoveryRequired, Completed }
 public enum MountStep { Present, RemoveIntent, Removed, RestoreIntent, Restored }
 public enum AccessState { Released, Locked, Unknown }
 public enum HardwareState { Unknown, StandbyRequested }
 public sealed class Identity {
  public string DeviceId {get;set;}
  public string Serial {get;set;}
  public string Model {get;set;}
  public string Firmware {get;set;}
  public string DiskGuid {get;set;}
  public long Capacity {get;set;}
  public bool Same(Identity other) {
   return other!=null && DeviceId==other.DeviceId && Serial==other.Serial && Model==other.Model && Firmware==other.Firmware && DiskGuid==other.DiskGuid && Capacity==other.Capacity;
  }
  public void Validate() {
   Guid id;
   if(String.IsNullOrWhiteSpace(DeviceId)||String.IsNullOrWhiteSpace(Serial)||String.IsNullOrWhiteSpace(Model)||String.IsNullOrWhiteSpace(Firmware)||!Guid.TryParse(DiskGuid,out id)||id==Guid.Empty||Capacity<=0)
    throw new InvalidOperationException(L.T("Unvollständige Geräteidentität."));
  }
 }
 public sealed class Volume {
  public string VolumeGuid {get;set;}
  public string PartitionGuid {get;set;}
  public long Offset {get;set;}
  public long Length {get;set;}
  public string[] Letters {get;set;}
 }
 public sealed class Descriptor {
  public Identity Identity {get;set;}
  public Volume[] Volumes {get;set;}
  public bool QualifiedSataHdd {get;set;}
  public bool Protected {get;set;}
  public bool HealthyNtfs {get;set;}
  public bool Encrypted {get;set;}
  public void Validate() {
   ValidateTopology();
   if(!QualifiedSataHdd||Protected||!HealthyNtfs||Encrypted) throw new InvalidOperationException(L.T("Gerät ist nicht für diese Operation zugelassen."));
  }
  public void ValidateTopology() {
   if(Identity==null) throw new InvalidOperationException(L.T("Geräteidentität fehlt."));
   Identity.Validate();
   if(Volumes==null||Volumes.Length==0) throw new InvalidOperationException(L.T("Keine eindeutig zugeordneten Volumes."));
   var volumes=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var partitions=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var letters=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var v in Volumes) {
    Guid guid,partition;
    if(v==null||!Guid.TryParse(v.VolumeGuid,out guid)||guid==Guid.Empty||!Guid.TryParse(v.PartitionGuid,out partition)||partition==Guid.Empty||v.Offset<0||v.Length<=0||v.Offset>Identity.Capacity-v.Length||!volumes.Add(guid.ToString("D"))||!partitions.Add(partition.ToString("D"))||v.Letters==null)
     throw new InvalidOperationException(L.T("Ungültige Volume-/Partitionstopologie."));
    foreach(var letter in v.Letters) { ValidateLetter(letter); if(!letters.Add(letter)) throw new InvalidOperationException(L.T("Doppelte Buchstabenzuordnung.")); }
   }
   var ordered=Volumes.OrderBy(v=>v.Offset).ToArray();
   for(int i=1;i<ordered.Length;i++) if(ordered[i-1].Offset+ordered[i-1].Length>ordered[i].Offset) throw new InvalidOperationException(L.T("Überlappende Partitionen."));
  }
  public static void ValidateLetter(string letter) { if(letter==null||letter.Length!=1||letter[0]<'A'||letter[0]>'Z') throw new InvalidOperationException(L.T("Ungültiger Laufwerksbuchstabe.")); }
  public bool SameTopology(Descriptor other) {
   if(other==null||!Identity.Same(other.Identity)||Volumes.Length!=other.Volumes.Length) return false;
   return Volumes.All(v=>other.Volumes.Any(o=>String.Equals(v.VolumeGuid,o.VolumeGuid,StringComparison.OrdinalIgnoreCase)&&String.Equals(v.PartitionGuid,o.PartitionGuid,StringComparison.OrdinalIgnoreCase)&&v.Offset==o.Offset&&v.Length==o.Length));
  }
 }
 public sealed class MountRecord {
  public string Letter {get;set;}
  public string VolumeGuid {get;set;}
  public MountStep Step {get;set;}
 }
 public sealed class Transaction {
  public string RequestId {get;set;}
  public Descriptor Device {get;set;}
  public Phase Phase {get;set;}
  public HardwareState Hardware {get;set;}
  public List<MountRecord> Mounts {get;set;}
  public string LastError {get;set;}
  public string UpdatedUtc {get;set;}
  public string CreatedUtc {get;set;}
  public string StandbyUtc {get;set;}
  public string CompletedUtc {get;set;}
 }
 public sealed class State {
  public long Revision {get;set;}
  public List<Transaction> Transactions {get;set;}
  public State() { Transactions=new List<Transaction>(); }
  public void Validate() {
   if(Revision<0||Transactions==null) throw new InvalidOperationException(L.T("Ungültiger Speicherzustand."));
   var requests=new HashSet<string>(); var devices=new HashSet<string>(); var letters=new HashSet<string>();
   foreach(var tx in Transactions) {
    Guid request;
    if(tx==null||!Guid.TryParse(tx.RequestId,out request)||request==Guid.Empty||!requests.Add(request.ToString("D"))||tx.Device==null||tx.Mounts==null||!Enum.IsDefined(typeof(Phase),tx.Phase)||!Enum.IsDefined(typeof(HardwareState),tx.Hardware)) throw new InvalidOperationException(L.T("Ungültige Transaktion."));
    tx.Device.Validate();
    var expected=tx.Device.Volumes.SelectMany(v=>v.Letters.Select(l=>l+":"+Guid.Parse(v.VolumeGuid).ToString("D"))).OrderBy(x=>x).ToArray();
    var actual=tx.Mounts.Select(m=> { if(m==null||!Enum.IsDefined(typeof(MountStep),m.Step)) throw new InvalidOperationException(L.T("Ungültiger Mount-Zustand.")); Descriptor.ValidateLetter(m.Letter); return m.Letter+":"+Guid.Parse(m.VolumeGuid).ToString("D"); }).OrderBy(x=>x).ToArray();
    if(!expected.SequenceEqual(actual)) throw new InvalidOperationException(L.T("Mount-Daten passen nicht zur gespeicherten Topologie."));
    if(tx.Phase!=Phase.Completed) {
     if(!devices.Add(tx.Device.Identity.DeviceId)) throw new InvalidOperationException(L.T("Mehrere offene Vorgänge für ein Gerät."));
     foreach(var m in tx.Mounts) if(!letters.Add(m.Letter)) throw new InvalidOperationException(L.T("Buchstabe in mehreren offenen Vorgängen."));
    } else if(tx.Mounts.Any(m=>m.Step!=MountStep.Restored)) throw new InvalidOperationException(L.T("Unvollständiger abgeschlossener Vorgang."));
   }
  }
 }
 // Thin wrapper keeping the M1 call shape (Codec.Serializer().Serialize/Deserialize).
 public sealed class JsonCodec {
  public const int MaxLength=4*1024*1024;
  static readonly JsonSerializerOptions options=new JsonSerializerOptions {MaxDepth=64,WriteIndented=false};
  public string Serialize(object value) { return JsonSerializer.Serialize(value,value==null?typeof(object):value.GetType(),options); }
  public T Deserialize<T>(string text) {
   if(text==null||text.Length>MaxLength) throw new InvalidOperationException(L.T("JSON fehlt oder überschreitet das Größenlimit."));
   return JsonSerializer.Deserialize<T>(text,options);
  }
 }
 public static class Codec {
  static readonly JsonCodec shared=new JsonCodec();
  public static JsonCodec Serializer() { return shared; }
  public static T Copy<T>(T value) { var s=Serializer(); return s.Deserialize<T>(JsonSerializer.Serialize<T>(value)); }
 }
 public sealed class Status {
  public string RequestId {get;set;}
  public string DeviceId {get;set;}
  public string Model {get;set;}
  public string Serial {get;set;}
  public string[] Letters {get;set;}
  public Phase Phase {get;set;}
  public AccessState Access {get;set;}
  public HardwareState Hardware {get;set;}
  public bool LettersRestored {get;set;}
  public string[] LettersMissing {get;set;}
  public string Error {get;set;}
  public string UpdatedUtc {get;set;}
  public string StandbyUtc {get;set;}
 }
 // Conservative draft values from the production concept (section 9). They are
 // product rules, not manufacturer guarantees, and are applied per device.
 public sealed class PowerPolicy {
  public TimeSpan MinimumRuntime {get;set;}
  public int MaxStandbyPer24h {get;set;}
  public TimeSpan RetainCompleted {get;set;}
  public PowerPolicy() { MinimumRuntime=TimeSpan.FromMinutes(30); MaxStandbyPer24h=4; RetainCompleted=TimeSpan.FromDays(90); }
  public static PowerPolicy None() { return new PowerPolicy {MinimumRuntime=TimeSpan.Zero,MaxStandbyPer24h=int.MaxValue}; }
 }
 public sealed class PolicyException : InvalidOperationException {
  public TimeSpan? RetryAfter {get;private set;}
  public PolicyException(string text,TimeSpan? retry=null):base(text) { RetryAfter=retry; }
 }
 // A live lock that can be probed after resume without changing its state.
 public interface IVolumeLock : IDisposable { void Probe(); }
}
