using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using HddSpindown.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HddSpindown.Windows {
 public sealed class BackendOptions {
  // Integration tests only: accept file-backed virtual disks (VHD/VHDX) as test
  // devices and skip the ATA command. Never enabled by the service.
  public bool AllowVirtualTestDisks {get;set;}
  public string ProtectedDataPath {get;set;}
 }

 sealed class DeviceFacts {
  public string Vendor, Product, Revision, Serial; public int BusType; public bool? SeekPenalty; public bool RemovableMedia;
  public const int BusSata=11, BusFileBackedVirtual=15;
  public bool IsVirtual { get { return BusType==BusFileBackedVirtual; } }
 }
 sealed class PartitionFacts { public int Number; public long Offset, Length; public Guid Type, Id; }
 sealed class LayoutFacts { public int Style; public Guid DiskId; public List<PartitionFacts> Partitions=new List<PartitionFacts>(); }
 sealed class VolumeFacts {
  public string Guid; public int ExtentCount; public long Offset, Length; public Guid PartitionId;
  public List<string> Letters=new List<string>(); public bool FolderMounts;
 }
 sealed class FsFacts { public string FileSystem; public bool Dirty; public bool? Encrypted; }
 sealed class Inspection {
  public Descriptor Descriptor; public List<string> Reasons=new List<string>(); public bool Rotational; public int Number;
 }

 public sealed class WindowsBackend : IDeviceBackend, IDiskInventory {
  static readonly Guid BasicData=new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
  static readonly Guid Msr=new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae");
  static readonly Guid Efi=new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
  const int MaxDisks=128;
  readonly BackendOptions options;
  public WindowsBackend(BackendOptions backendOptions=null) { options=backendOptions??new BackendOptions(); }

  // ---------- device facts ----------
  static string AnsiAt(byte[] b,int offset) {
   if(offset<=0||offset>=b.Length) return "";
   int end=offset; while(end<b.Length&&b[end]!=0) end++;
   return Encoding.ASCII.GetString(b,offset,end-offset).Trim();
  }
  static DeviceFacts ReadDevice(SafeFileHandle h) {
   var q=new byte[12]; // StorageDeviceProperty, PropertyStandardQuery
   var b=Native.Query(h,Native.IOCTL_STORAGE_QUERY_PROPERTY,q,1024,"Geräteeigenschaften");
   if(b.Length<36) throw new InvalidOperationException("Unvollständige Geräteeigenschaften.");
   var f=new DeviceFacts {RemovableMedia=b[10]!=0,Vendor=AnsiAt(b,BitConverter.ToInt32(b,12)),Product=AnsiAt(b,BitConverter.ToInt32(b,16)),Revision=AnsiAt(b,BitConverter.ToInt32(b,20)),Serial=AnsiAt(b,BitConverter.ToInt32(b,24)),BusType=BitConverter.ToInt32(b,28)};
   try {
    q=new byte[12]; BitConverter.GetBytes(7).CopyTo(q,0); // StorageDeviceSeekPenaltyProperty
    var s=Native.Query(h,Native.IOCTL_STORAGE_QUERY_PROPERTY,q,64,"Seek-Penalty");
    if(s.Length>=9) f.SeekPenalty=s[8]!=0;
   } catch(Win32Exception) { f.SeekPenalty=null; }
   return f;
  }
  static int DeviceNumber(SafeFileHandle h) {
   var b=Native.Query(h,Native.IOCTL_STORAGE_GET_DEVICE_NUMBER,null,12,"Datenträgernummer");
   if(b.Length<12) throw new InvalidOperationException("Datenträgernummer unvollständig.");
   return BitConverter.ToInt32(b,4);
  }
  static long Capacity(SafeFileHandle h) {
   var b=Native.Query(h,Native.IOCTL_DISK_GET_LENGTH_INFO,null,8,"Kapazität");
   if(b.Length<8) throw new InvalidOperationException("Kapazität unbekannt.");
   return BitConverter.ToInt64(b,0);
  }
  static PartitionFacts ParsePartition(byte[] b,int o) {
   return new PartitionFacts {Offset=BitConverter.ToInt64(b,o+8),Length=BitConverter.ToInt64(b,o+16),Number=BitConverter.ToInt32(b,o+24),
    Type=BitConverter.ToInt32(b,o)==1?new Guid(b.Skip(o+32).Take(16).ToArray()):Guid.Empty,
    Id=BitConverter.ToInt32(b,o)==1?new Guid(b.Skip(o+48).Take(16).ToArray()):Guid.Empty};
  }
  static LayoutFacts ReadLayout(SafeFileHandle h) {
   var b=Native.Query(h,Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX,null,48+144*32,"Partitionstabelle");
   if(b.Length<48) throw new InvalidOperationException("Partitionstabelle unvollständig.");
   var l=new LayoutFacts {Style=BitConverter.ToInt32(b,0)};
   int count=BitConverter.ToInt32(b,4);
   if(l.Style==1) l.DiskId=new Guid(b.Skip(8).Take(16).ToArray());
   for(int i=0;i<count;i++) {
    int o=48+i*144; if(o+144>b.Length) throw new InvalidOperationException("Partitionstabelle unvollständig.");
    var p=ParsePartition(b,o);
    if(p.Length>0) l.Partitions.Add(p); // MBR layouts report unused slots with zero length.
   }
   return l;
  }

  // ---------- volumes ----------
  static IEnumerable<string> AllVolumes() {
   var name=new StringBuilder(1024);
   IntPtr find=Native.FindFirstVolume(name,(uint)name.Capacity);
   if(find==new IntPtr(-1)) throw Native.Error("Volumes auflisten");
   try {
    do { yield return name.ToString(); name.Clear(); } while(Native.FindNextVolume(find,name,(uint)name.Capacity));
    if(Marshal.GetLastWin32Error()!=Native.ERROR_NO_MORE_FILES) throw Native.Error("Volumes auflisten");
   } finally { Native.FindVolumeClose(find); }
  }
  static string GuidOf(string volumeName) {
   int a=volumeName.IndexOf('{'), z=volumeName.IndexOf('}');
   Guid g; if(a<0||z<a||!Guid.TryParse(volumeName.Substring(a,z-a+1),out g)) return null;
   return g.ToString("D");
  }
  static string VolumePath(string guid,bool trailingSlash) { return @"\\?\Volume{"+guid+"}"+(trailingSlash?@"\":""); }
  static string[] PathNames(string volumeWithSlash) {
   uint needed; var buffer=new char[1024];
   if(!Native.GetVolumePathNamesForVolumeName(volumeWithSlash,buffer,(uint)buffer.Length,out needed)) {
    if(Marshal.GetLastWin32Error()!=Native.ERROR_MORE_DATA) throw Native.Error("Laufwerksbuchstaben ermitteln");
    buffer=new char[needed];
    if(!Native.GetVolumePathNamesForVolumeName(volumeWithSlash,buffer,(uint)buffer.Length,out needed)) throw Native.Error("Laufwerksbuchstaben ermitteln");
   }
   return new string(buffer).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries);
  }
  // Extents and partition id via an existing handle (our lock) or a query-only open.
  static VolumeFacts ReadVolume(string guid,SafeFileHandle existing,int disk) {
   SafeFileHandle h=existing??Native.TryOpen(VolumePath(guid,false),0);
   if(h==null) return null;
   try {
    byte[] e;
    try { e=Native.Query(h,Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,null,8+24*4,"Volume-Zuordnung"); }
    catch(Win32Exception) { return null; } // CD-ROM, floppy or volumes without extents.
    int count=BitConverter.ToInt32(e,0);
    bool onDisk=false; for(int i=0;i<count;i++) if(BitConverter.ToInt32(e,8+i*24)==disk) onDisk=true;
    if(!onDisk) return null;
    var v=new VolumeFacts {Guid=guid,ExtentCount=count,Offset=BitConverter.ToInt64(e,16),Length=BitConverter.ToInt64(e,24)};
    try { var p=Native.Query(h,Native.IOCTL_DISK_GET_PARTITION_INFO_EX,null,144,"Partitionsdaten"); if(p.Length>=144) v.PartitionId=ParsePartition(p,0).Id; } catch(Win32Exception) {}
    foreach(string path in PathNames(VolumePath(guid,true))) {
     if(path.Length==3&&path[1]==':'&&path[2]=='\\'&&char.IsLetter(path[0])) v.Letters.Add(char.ToUpperInvariant(path[0]).ToString());
     else v.FolderMounts=true;
    }
    return v;
   } finally { if(existing==null) h.Dispose(); }
  }
  static FsFacts ReadFs(string guid) {
   var f=new FsFacts();
   var name=new StringBuilder(261); var fs=new StringBuilder(261); uint serial,max,flags;
   if(Native.GetVolumeInformation(VolumePath(guid,true),name,name.Capacity,out serial,out max,out flags,fs,fs.Capacity)) f.FileSystem=fs.ToString();
   using(var h=Native.TryOpen(VolumePath(guid,false),0)) {
    if(h!=null) try { var d=Native.Query(h,Native.FSCTL_IS_VOLUME_DIRTY,null,4,"Dirty-Bit"); f.Dirty=d.Length>=4&&(BitConverter.ToInt32(d,0)&1)!=0; } catch(Win32Exception) { f.Dirty=true; }
    else f.Dirty=true;
   }
   return f;
  }
  // BitLocker / device encryption. Unknown status fails closed.
  static Dictionary<string,bool> EncryptionStatus() {
   var map=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
   try {
    using(var searcher=new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption","SELECT DeviceID,ConversionStatus,ProtectionStatus FROM Win32_EncryptableVolume"))
    foreach(ManagementObject o in searcher.Get()) using(o) {
     string g=GuidOf(Convert.ToString(o["DeviceID"])??""); if(g==null) continue;
     uint conversion=Convert.ToUInt32(o["ConversionStatus"]??0u), protection=Convert.ToUInt32(o["ProtectionStatus"]??0u);
     map[g]=conversion!=0||protection!=0;
    }
    return map;
   } catch(Exception) { return null; }
  }

  // ---------- protected disks ----------
  static string LetterVolume(string letter) {
   var sb=new StringBuilder(1024);
   return Native.GetVolumeNameForVolumeMountPoint(letter.Substring(0,1)+@":\",sb,(uint)sb.Capacity)?GuidOf(sb.ToString()):null;
  }
  HashSet<string> ProtectedVolumes() {
   var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   void Add(string path) { if(String.IsNullOrEmpty(path)) return; string root=Path.GetPathRoot(path); if(root!=null&&root.Length>=2&&root[1]==':') { var g=LetterVolume(root); if(g!=null) set.Add(g); } }
   Add(Environment.SystemDirectory);
   Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
   Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
   Add(options.ProtectedDataPath);
   try {
    var files=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management","ExistingPageFiles",null) as string[];
    if(files!=null) foreach(string f in files) { string p=f.StartsWith(@"\??\")?f.Substring(4):f; Add(p); }
   } catch {}
   return set;
  }

  // ---------- inspection ----------
  static string Id(DeviceFacts d,LayoutFacts l) {
   if(d.IsVirtual) return "VHD:"+l.DiskId.ToString("D");
   return "SATA:"+Clean(d.Product)+":"+Clean(d.Serial);
  }
  static string Clean(string s) { var sb=new StringBuilder(); foreach(char c in s??"") if(c>32&&c<127&&c!=':') sb.Append(c); return sb.ToString(); }

  Inspection InspectDisk(SafeFileHandle h,IDictionary<string,SafeFileHandle> held,IDictionary<string,FsFacts> fsCache,Dictionary<string,bool> encryption,HashSet<string> protectedVolumes) {
   var r=new Inspection();
   var dev=ReadDevice(h); r.Number=DeviceNumber(h);
   r.Rotational=dev.SeekPenalty==true||(dev.IsVirtual&&options.AllowVirtualTestDisks);
   var layout=ReadLayout(h); long capacity=Capacity(h);
   bool virt=dev.IsVirtual&&options.AllowVirtualTestDisks;
   var id=new Identity {DeviceId=Id(dev,layout),Model=dev.Product,Serial=virt&&String.IsNullOrEmpty(dev.Serial)?layout.DiskId.ToString("N"):dev.Serial,Firmware=dev.Revision,DiskGuid=layout.DiskId==Guid.Empty?null:layout.DiskId.ToString("D"),Capacity=capacity};
   var d=new Descriptor {Identity=id,HealthyNtfs=true};
   if(virt) d.QualifiedSataHdd=true;
   else if(dev.BusType!=DeviceFacts.BusSata) r.Reasons.Add("Kein direkt angebundener SATA-Datenträger (Bustyp "+dev.BusType+").");
   else if(dev.SeekPenalty!=true) r.Reasons.Add(dev.SeekPenalty==null?"Rotierendes Laufwerk nicht eindeutig bestätigt.":"SSD – wird nicht unterstützt.");
   else d.QualifiedSataHdd=true;
   if(dev.RemovableMedia) { d.QualifiedSataHdd=false; r.Reasons.Add("Wechseldatenträger."); }
   if(String.IsNullOrEmpty(id.Serial)) r.Reasons.Add("Keine Seriennummer vom Gerät.");
   if(layout.Style!=1) { d.HealthyNtfs=false; r.Reasons.Add("Kein GPT-Datenträger (MBR/RAW wird in 1.0 nicht unterstützt)."); }

   var volumes=new List<VolumeFacts>();
   foreach(string name in AllVolumes()) {
    string g=GuidOf(name); if(g==null) continue;
    SafeFileHandle lockHandle=null; if(held!=null) held.TryGetValue(g,out lockHandle);
    var v=ReadVolume(g,lockHandle,r.Number); if(v!=null) volumes.Add(v);
   }
   var list=new List<Volume>();
   foreach(var p in layout.Partitions) {
    if(p.Type==Msr) continue;
    if(p.Type==Efi) { d.Protected=true; r.Reasons.Add("EFI-Systempartition vorhanden (Startdatenträger)."); continue; }
    if(layout.Style==1&&p.Type!=BasicData) { d.HealthyNtfs=false; r.Reasons.Add("Nicht unterstützter Partitionstyp "+p.Type.ToString("D")+" (dynamisch/Storage Spaces/Wiederherstellung)."); continue; }
    var match=volumes.Where(v=>v.Offset==p.Offset).ToArray();
    if(match.Length!=1) { d.HealthyNtfs=false; r.Reasons.Add("Partition "+p.Number+" hat kein eindeutiges Volume."); continue; }
    var vol=match[0];
    if(vol.ExtentCount!=1||vol.Length!=p.Length) { d.HealthyNtfs=false; r.Reasons.Add("Volume über mehrere Bereiche/Datenträger verteilt."); continue; }
    if(layout.Style==1&&vol.PartitionId!=Guid.Empty&&vol.PartitionId!=p.Id) { d.HealthyNtfs=false; r.Reasons.Add("Volume-/Partitionszuordnung widersprüchlich."); continue; }
    FsFacts fs=null;
    bool lockedByUs=held!=null&&held.ContainsKey(vol.Guid);
    if(fsCache!=null&&fsCache.TryGetValue(vol.Guid,out fs)&&lockedByUs) { /* locked by this session: cached facts stay valid */ }
    else { fs=ReadFs(vol.Guid); bool enc; fs.Encrypted=encryption==null?(bool?)null:(encryption.TryGetValue(vol.Guid,out enc)?enc:false); if(fsCache!=null) fsCache[vol.Guid]=fs; }
    if(!String.Equals(fs.FileSystem,"NTFS",StringComparison.OrdinalIgnoreCase)) { d.HealthyNtfs=false; r.Reasons.Add("Volume ist nicht NTFS ("+(fs.FileSystem??"unbekannt")+")."); }
    if(fs.Dirty) { d.HealthyNtfs=false; r.Reasons.Add("Dateisystem als fehlerhaft markiert (Dirty-Bit). Bitte zuerst prüfen."); }
    if(fs.Encrypted!=false) { d.Encrypted=true; r.Reasons.Add(fs.Encrypted==null?"Verschlüsselungsstatus nicht prüfbar.":"BitLocker-/Geräteverschlüsselung aktiv – in 1.0 nicht unterstützt."); }
    if(protectedVolumes!=null&&protectedVolumes.Contains(vol.Guid)) { d.Protected=true; r.Reasons.Add("Enthält Windows, Programme, Auslagerungsdatei oder Dienstdaten."); }
    list.Add(new Volume {VolumeGuid=vol.Guid,PartitionGuid=p.Id==Guid.Empty?null:p.Id.ToString("D"),Offset=p.Offset,Length=p.Length,Letters=vol.Letters.OrderBy(x=>x).ToArray()});
   }
   if(list.Count==0) r.Reasons.Add("Keine Datenvolumes.");
   d.Volumes=list.ToArray();
   r.Descriptor=d;
   return r;
  }

  static string DrivePath(int n) { return @"\\.\PhysicalDrive"+n; }

  // ---------- IDiskInventory ----------
  public DiskInfo[] List() { return List(null); }
  // skip: device ids that must not be inspected (paused HDDs). Only the storage
  // descriptor is read for them, which the driver answers without media access.
  public DiskInfo[] List(Func<string,bool> skip) {
   var result=new List<DiskInfo>();
   Dictionary<string,bool> encryption=null; HashSet<string> prot=null; bool prepared=false;
   for(int n=0;n<MaxDisks;n++) {
    DeviceFacts dev;
    using(var q=Native.TryOpen(DrivePath(n),0)) {
     if(q==null) continue;
     try { dev=ReadDevice(q); } catch { continue; }
     bool candidate=dev.SeekPenalty==true||(dev.IsVirtual&&options.AllowVirtualTestDisks);
     if(!candidate) continue; // SSD/NVMe are hidden from the list.
     if(skip!=null&&!dev.IsVirtual) {
      string quick="SATA:"+Clean(dev.Product)+":"+Clean(dev.Serial);
      if(skip(quick)) { result.Add(new DiskInfo {DeviceId=quick,Number=n,Model=dev.Product,Serial=dev.Serial,Firmware=dev.Revision,Letters=new string[0],Eligible=false,Reason="Pausiert."}); continue; }
     }
    }
    if(!prepared) { encryption=EncryptionStatus(); prot=ProtectedVolumes(); prepared=true; }
    using(var h=Native.TryOpen(DrivePath(n),Native.GENERIC_READ)) {
     if(h==null) continue;
     DiskInfo info;
     try {
      if(skip!=null&&dev.IsVirtual) { string vid=Id(dev,ReadLayout(h)); if(skip(vid)) { result.Add(new DiskInfo {DeviceId=vid,Number=n,Model=dev.Product,Serial=dev.Serial,Firmware=dev.Revision,Letters=new string[0],Eligible=false,Reason="Pausiert."}); continue; } }
      var r=InspectDisk(h,null,null,encryption,prot);
      var d=r.Descriptor;
      bool eligible=r.Reasons.Count==0;
      if(eligible) try { d.Validate(); } catch(Exception ex) { eligible=false; r.Reasons.Add(ex.Message); }
      info=new DiskInfo {DeviceId=d.Identity.DeviceId,Number=r.Number,Model=d.Identity.Model,Serial=d.Identity.Serial,Firmware=d.Identity.Firmware,Capacity=d.Identity.Capacity,
       Letters=d.Volumes.SelectMany(v=>v.Letters).OrderBy(x=>x).ToArray(),Eligible=eligible,Reason=eligible?null:String.Join(" ",r.Reasons.Distinct())};
     } catch(Exception ex) {
      info=new DiskInfo {DeviceId=null,Number=n,Model="Datenträger "+n,Serial="",Letters=new string[0],Eligible=false,Reason="Prüfung fehlgeschlagen: "+ex.Message};
     }
     result.Add(info);
    }
   }
   return result.ToArray();
  }

  // ---------- IDeviceBackend ----------
  public IDeviceSession Open(string trustedDeviceId) {
   if(String.IsNullOrWhiteSpace(trustedDeviceId)) throw new ArgumentException("Gerätekennung fehlt.");
   var matches=new List<int>();
   for(int n=0;n<MaxDisks;n++) {
    using(var q=Native.TryOpen(DrivePath(n),Native.GENERIC_READ)) {
     if(q==null) continue;
     try { var dev=ReadDevice(q); var l=ReadLayout(q); if(Id(dev,l)==trustedDeviceId) matches.Add(n); } catch { }
    }
   }
   if(matches.Count==0) throw new IOException("Gerät "+trustedDeviceId+" ist nicht angeschlossen oder nicht lesbar.");
   if(matches.Count>1) throw new InvalidOperationException("Gerätekennung ist nicht eindeutig ("+matches.Count+" Treffer). Abbruch.");
   var h=Native.Open(DrivePath(matches[0]),Native.GENERIC_READ|Native.GENERIC_WRITE,"Datenträger öffnen");
   try {
    // Re-confirm identity on the opened handle; numbers may have moved meanwhile.
    var dev=ReadDevice(h); var l=ReadLayout(h);
    if(Id(dev,l)!=trustedDeviceId) throw new InvalidOperationException("Geöffnetes Gerät passt nicht zur Kennung. Datenträgernummern haben sich geändert.");
    return new Session(this,h,dev.IsVirtual&&options.AllowVirtualTestDisks);
   } catch { h.Dispose(); throw; }
  }

  public string LookupLetter(string letter) {
   Descriptor.ValidateLetter(letter);
   string root=letter+@":\";
   var name=new StringBuilder(1024);
   if(Native.GetVolumeNameForVolumeMountPoint(root,name,(uint)name.Capacity)) {
    string g=GuidOf(name.ToString()); if(g==null) throw new InvalidOperationException(root+" ist belegt, Volume unbekannt.");
    return g;
   }
   // A failed volume lookup alone does not prove the letter is free (network,
   // SUBST, removable media). Only ERROR_FILE_NOT_FOUND from QueryDosDevice does.
   var target=new StringBuilder(32768);
   if(Native.QueryDosDevice(letter+":",target,target.Capacity)!=0) throw new InvalidOperationException(root+" ist belegt, aber keinem lokalen Volume zugeordnet.");
   if(Marshal.GetLastWin32Error()==Native.ERROR_FILE_NOT_FOUND) return null;
   throw Native.Error("Belegung von "+root+" prüfen");
  }
  public void RemoveLetter(string letter) {
   Descriptor.ValidateLetter(letter);
   if(!Native.DeleteVolumeMountPoint(letter+@":\")) throw Native.Error("Laufwerksbuchstabe "+letter+": entfernen");
  }
  public void RestoreLetter(string letter,string volumeGuid) {
   Descriptor.ValidateLetter(letter);
   Guid g; if(!Guid.TryParse(volumeGuid,out g)) throw new ArgumentException("Ungültige Volume-GUID.");
   // SetVolumeMountPoint refuses an occupied letter itself, closing the race window.
   if(!Native.SetVolumeMountPoint(letter+@":\",VolumePath(g.ToString("D"),true))) throw Native.Error("Laufwerksbuchstabe "+letter+": wiederherstellen");
  }
  public void NotifyExplorer(string letter,bool added) {
   // Only effective in an interactive session; the UI repeats this for the user's Explorer.
   try { Native.SHChangeNotify(added?0x100:0x80,0x0005|0x1000,letter+@":\",IntPtr.Zero); } catch {}
  }

  // ---------- session ----------
  sealed class VolumeLock : IVolumeLock {
   readonly Session owner; readonly string guid; SafeFileHandle handle;
   public VolumeLock(Session s,string g,SafeFileHandle h) { owner=s; guid=g; handle=h; }
   public SafeFileHandle Handle { get { return handle; } }
   public void Probe() {
    if(handle==null||handle.IsClosed) throw new InvalidOperationException("Sperr-Handle geschlossen.");
    Native.Query(handle,Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,null,8+24*4,"Sperre prüfen");
   }
   public void Dispose() {
    if(handle==null) return;
    try { uint r; Native.DeviceIoControl(handle,Native.FSCTL_UNLOCK_VOLUME,IntPtr.Zero,0,IntPtr.Zero,0,out r,IntPtr.Zero); }
    finally { handle.Dispose(); handle=null; owner.Forget(guid); } // Closing the handle always releases the lock.
   }
  }
  sealed class Session : IDeviceSession {
   readonly WindowsBackend owner; SafeFileHandle disk; readonly bool virtualTest;
   readonly Dictionary<string,SafeFileHandle> locks=new Dictionary<string,SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
   readonly Dictionary<string,FsFacts> fsCache=new Dictionary<string,FsFacts>(StringComparer.OrdinalIgnoreCase);
   Dictionary<string,bool> encryption; bool encryptionRead;
   public Session(WindowsBackend b,SafeFileHandle h,bool test) { owner=b; disk=h; virtualTest=test; }
   SafeFileHandle Disk { get { if(disk==null) throw new ObjectDisposedException("Datenträger"); return disk; } }
   public void Forget(string guid) { lock(locks) locks.Remove(guid); }
   public Descriptor Inspect() {
    if(!encryptionRead) { encryption=EncryptionStatus(); encryptionRead=true; }
    Dictionary<string,SafeFileHandle> held; lock(locks) held=new Dictionary<string,SafeFileHandle>(locks,StringComparer.OrdinalIgnoreCase);
    var r=owner.InspectDisk(Disk,held,fsCache,encryption,owner.ProtectedVolumes());
    return r.Descriptor;
   }
   public IDisposable LockVolume(string volumeGuid) {
    Guid g; if(!Guid.TryParse(volumeGuid,out g)) throw new ArgumentException("Ungültige Volume-GUID.");
    string guid=g.ToString("D");
    int number=DeviceNumber(Disk);
    var h=Native.Open(VolumePath(guid,false),Native.GENERIC_READ|Native.GENERIC_WRITE,"Volume öffnen");
    try {
     var e=Native.Query(h,Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,null,8+24*4,"Volume-Zuordnung");
     if(BitConverter.ToInt32(e,0)!=1||BitConverter.ToInt32(e,8)!=number) throw new InvalidOperationException("Volume gehört nicht eindeutig zu diesem Datenträger.");
     // FSCTL_LOCK_VOLUME fails while files are open and flushes caches on success.
     Native.Control(h,Native.FSCTL_LOCK_VOLUME,"Sperren – Dateien oder Programme sind möglicherweise noch geöffnet");
     lock(locks) locks[guid]=h;
     return new VolumeLock(this,guid,h);
    } catch { h.Dispose(); throw; }
   }
   public void RequestStandby() {
    if(virtualTest) return; // VHDs have no motor; the integration test only checks ordering.
    var ata=new Native.AtaPassThroughEx {Length=(ushort)Marshal.SizeOf(typeof(Native.AtaPassThroughEx)),AtaFlags=Native.ATA_FLAGS_DRDY_REQUIRED,TimeOutValue=15,PreviousTaskFile=new byte[8],CurrentTaskFile=new byte[8]};
    ata.CurrentTaskFile[6]=Native.ATA_STANDBY_IMMEDIATE;
    int length=Marshal.SizeOf(typeof(Native.AtaPassThroughEx));
    IntPtr buffer=Marshal.AllocHGlobal(length);
    try {
     Marshal.StructureToPtr(ata,buffer,false);
     uint size;
     if(!Native.DeviceIoControl(Disk,Native.IOCTL_ATA_PASS_THROUGH,buffer,(uint)length,buffer,(uint)length,out size,IntPtr.Zero)) throw Native.Error("ATA-Standby");
     if(size<length) throw new InvalidOperationException("Unvollständige ATA-Antwort.");
     ata=(Native.AtaPassThroughEx)Marshal.PtrToStructure(buffer,typeof(Native.AtaPassThroughEx));
     // ERR, DRQ, DF, BSY must be clear after a completed STANDBY IMMEDIATE.
     if((ata.CurrentTaskFile[6]&0xA9)!=0) throw new InvalidOperationException("HDD meldet ATA-Fehler (Status 0x"+ata.CurrentTaskFile[6].ToString("X2")+").");
    } finally { Marshal.FreeHGlobal(buffer); }
   }
   public void Dispose() { if(disk!=null) { disk.Dispose(); disk=null; } }
  }
 }
}
