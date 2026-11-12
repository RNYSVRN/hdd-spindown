using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HddSpindown.Core {
 public sealed class StoreException : IOException { public StoreException(string text,Exception inner=null):base(text,inner) {} }
 public interface IStateStore : IDisposable {
  State Read();
  bool Healthy {get;}
  void Save(State next);
  void Repair();
 }
 public sealed class Envelope {
  public int Schema {get;set;}
  public long Revision {get;set;}
  public string PreviousHash {get;set;}
  public string Payload {get;set;}
  public string Hash {get;set;}
 }
 // Managed file operations only. No registry, service or storage-device access.
 public sealed class DurableStore : IStateStore {
  readonly string dir;
  readonly FileStream ownership;
  readonly Action<string> checkpoint;
  readonly object gate=new object();
  Envelope current;
  bool healthy,disposed,poisoned;
  public bool Healthy { get { return healthy&&!disposed; } }
  public DurableStore(string directory,Action<string> testCheckpoint=null) {
   dir=Path.GetFullPath(directory); checkpoint=testCheckpoint??delegate {};
   Directory.CreateDirectory(dir);
   ownership=new FileStream(Path.Combine(dir,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
   try { Load(); } catch { ownership.Dispose(); throw; }
  }
  string PathFor(string name) { return Path.Combine(dir,name); }
  static string Digest(Envelope e) {
   string value=e.Schema+"\n"+e.Revision+"\n"+e.PreviousHash+"\n"+e.Payload;
   using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","");
  }
  static Envelope Make(State state,string previous) {
   state.Validate();
   var e=new Envelope {Schema=1,Revision=state.Revision,PreviousHash=previous,Payload=Codec.Serializer().Serialize(state)}; e.Hash=Digest(e); return e;
  }
  Envelope TryRead(string name,out Exception error) {
   error=null;
   try {
    string path=PathFor(name); if(!File.Exists(path)) return null;
    if(new FileInfo(path).Length>4*1024*1024) throw new StoreException(L.T("Zustandsdatei überschreitet das Größenlimit."));
    var e=Codec.Serializer().Deserialize<Envelope>(File.ReadAllText(path,Encoding.UTF8));
    if(e==null||e.Schema!=1||e.Hash!=Digest(e)||e.Revision<0) throw new StoreException(L.T("Prüfsumme oder Schema ungültig."));
    var state=Codec.Serializer().Deserialize<State>(e.Payload); state.Validate();
    if(state.Revision!=e.Revision) throw new StoreException(L.T("Widersprüchliche Revision."));
    return e;
   } catch(Exception ex) { error=ex; return null; }
  }
  void Load() {
   Exception ea,eb; var a=TryRead("state-A.json",out ea); var b=TryRead("state-B.json",out eb);
   if(a==null&&b==null) {
    if(ea!=null||eb!=null||Directory.EnumerateFiles(dir).Any(p=>Path.GetFileName(p)!="owner.lock")) throw new StoreException(L.T("Kein eindeutig gültiger aktueller Zustand. Nur Diagnose; keine automatische Rekonstruktion aus alten Sicherungen."));
    WriteFile("initialized","schema=1",false);
    current=Make(new State(),""); healthy=false; Repair(); return;
   }
   if(a!=null&&b!=null) {
    var high=a.Revision>=b.Revision?a:b; var low=a.Revision>=b.Revision?b:a;
    if(high.Revision==low.Revision && high.Hash!=low.Hash) throw new StoreException(L.T("Widersprüchliche Zustandskopien derselben Revision."));
    if(high.Revision!=low.Revision && (high.Revision!=low.Revision+1||high.PreviousHash!=low.Hash)) throw new StoreException(L.T("Zustandskopien bilden keine eindeutige Folge."));
    current=high; healthy=a.Hash==b.Hash;
   } else { current=a??b; healthy=false; }
  }
  public State Read() { lock(gate) { CheckDisposed(); return Codec.Copy(Codec.Serializer().Deserialize<State>(current.Payload)); } }
  void CheckDisposed() { if(disposed)throw new ObjectDisposedException("DurableStore"); }
  void WriteFile(string name,string content,bool backup) {
   string target=PathFor(name),tmp=PathFor(name+".tmp");
   checkpoint("before-write:"+name);
   using(var stream=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)) {
    byte[] bytes=Encoding.UTF8.GetBytes(content); stream.Write(bytes,0,bytes.Length); stream.Flush(true);
   }
   checkpoint("after-flush:"+name);
   if(File.Exists(target)) File.Replace(tmp,target,backup?PathFor(name+".bak"):null); else File.Move(tmp,target);
   checkpoint("after-replace:"+name);
  }
  void WriteBoth(Envelope envelope) {
   string text=Codec.Serializer().Serialize(envelope);
   if(Encoding.UTF8.GetByteCount(text)>4*1024*1024)throw new StoreException(L.T("Zustandsspeicher benötigt eine geprüfte Archivierung, bevor weitere Vorgänge möglich sind."));
   WriteFile("state-A.json",text,true); WriteFile("state-B.json",text,true);
  }
  public void Save(State next) {
   lock(gate) {
   CheckDisposed();
   if(!healthy)throw new StoreException(L.T("Zustandsspeicher nicht schreibbereit; Wiederherstellung erforderlich."));
   if(next.Revision!=current.Revision)throw new StoreException(L.T("Veralteter Schreibauftrag."));
   var copy=Codec.Copy(next); copy.Revision++; var envelope=Make(copy,current.Hash);
   // Poison before starting I/O. A partial write must prevent all later effects.
   healthy=false;
   try { WriteBoth(envelope); current=envelope; healthy=true; next.Revision=copy.Revision; }
   catch(Exception ex) { poisoned=true; throw new StoreException(L.T("Zustandsänderung nicht vollständig bestätigt. Keine weitere Geräteänderung in diesem Prozess."),ex); }
   }
  }
  public void Repair() {
   lock(gate) {
   if(poisoned)throw new StoreException(L.T("Nach einem Schreibfehler Speicher schließen und neu einlesen."));
   CheckDisposed(); healthy=false;
   try { WriteBoth(current); healthy=true; }
   catch(Exception ex) { poisoned=true; throw new StoreException(L.T("Zustandskopien konnten nicht abgeglichen werden."),ex); }
   }
  }
  public void Dispose() { lock(gate) { if(disposed)return; disposed=true; healthy=false; ownership.Dispose(); } }
 }
}
