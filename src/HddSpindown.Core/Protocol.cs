using System;
using System.Collections.Generic;

namespace HddSpindown.Core {
 // Facts about one physical disk as determined by the service from the opened device.
 // Clients never send these back as trusted input; only DeviceId is used as a key.
 public sealed class DiskInfo {
  public string DeviceId {get;set;}
  public int Number {get;set;}
  public string Model {get;set;}
  public string Serial {get;set;}
  public string Firmware {get;set;}
  public long Capacity {get;set;}
  public string[] Letters {get;set;}
  public bool Eligible {get;set;}
  public string Reason {get;set;}
 }
 public interface IDiskInventory { DiskInfo[] List(); }

 public static class Ops {
  public const string Hello="hello", List="list", Refresh="refresh", Pause="pause", Release="release", ReleaseAll="releaseAll", ImportLegacy="importLegacy", Diagnostics="diagnostics";
  public static readonly HashSet<string> All=new HashSet<string>(StringComparer.Ordinal) {Hello,List,Refresh,Pause,Release,ReleaseAll,ImportLegacy,Diagnostics};
 }
 // Fixed, small vocabulary. The service rejects anything else; no paths, ATA
 // commands or scripts can be passed to the privileged process.
 public sealed class Request {
  public int Version {get;set;}
  public string Op {get;set;}
  public string DeviceId {get;set;}
  public string RequestId {get;set;}
  public LegacyEntry[] Legacy {get;set;}
  public string Language {get;set;} // "de" / "en"; answers use this language
 }
 public sealed class LegacyEntry {
  public string Root {get;set;}
  public string Volume {get;set;}
 }
 public sealed class DeviceView {
  public DiskInfo Disk {get;set;}
  public Status Operation {get;set;}
  public string PolicyBlock {get;set;}
  public bool Present {get;set;}
 }
 public sealed class Response {
  public bool Ok {get;set;}
  public string Error {get;set;}
  public string ErrorKind {get;set;}
  public string ServiceVersion {get;set;}
  public bool StoreHealthy {get;set;}
  public bool Suspended {get;set;}
  public bool RecoveryPending {get;set;}
  public string LastInventoryUtc {get;set;}
  public DeviceView[] Devices {get;set;}
  public string[] Messages {get;set;}
  public string Text {get;set;}
 }
 public static class PipeNames {
  public const string Service="HddSpindown.Service.v1";
  public const int ProtocolVersion=1;
  public const int MaxMessageBytes=256*1024;
 }
}
