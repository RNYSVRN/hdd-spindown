using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace HddSpindown.Core {
 public enum Lang { De, En }

 // German source text doubles as the key; the catalog supplies English.
 // L.T("…") translates now; L.Tok("…",args) produces a token for text that is
 // cached by the service and translated later by the client (L.Expand).
 public static partial class L {
  public const string RegistryKey=@"Software\HDD Spindown";
  public const string RegistryValue="Language";
  [ThreadStatic] static Lang? scoped;
  static Lang? fixedDefault;
  public static Lang Default { get { return fixedDefault??(fixedDefault=Detect()).Value; } set { fixedDefault=value; } }
  public static Lang Current { get { return scoped??Default; } }
  public static string CodeOf(Lang l) { return l==Lang.En?"en":"de"; }
  public static Lang? Parse(string code) {
   if(String.IsNullOrWhiteSpace(code)) return null;
   code=code.Trim().ToLowerInvariant();
   if(code.StartsWith("de")) return Lang.De;
   if(code.StartsWith("en")) return Lang.En;
   return null;
  }
  // User choice (HKCU) > installer choice (HKLM) > Windows display language.
  public static Lang Detect() {
   foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine}) {
    try { using(var k=hive.OpenSubKey(RegistryKey)) { var l=Parse(k?.GetValue(RegistryValue) as string); if(l.HasValue) return l.Value; } } catch {}
   }
   return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="de"?Lang.De:Lang.En;
  }
  public static Lang? UserChoice() {
   try { using(var k=Registry.CurrentUser.OpenSubKey(RegistryKey)) return Parse(k?.GetValue(RegistryValue) as string); } catch { return null; }
  }
  public static void SaveUserChoice(Lang? lang) {
   using(var k=Registry.CurrentUser.CreateSubKey(RegistryKey)) {
    if(lang.HasValue) k.SetValue(RegistryValue,CodeOf(lang.Value)); else k.DeleteValue(RegistryValue,false);
   }
   fixedDefault=lang??Detect();
  }

  sealed class Scope : IDisposable { readonly Lang? previous; public Scope(Lang? l) { previous=scoped; scoped=l; } public void Dispose() { scoped=previous; } }
  public static IDisposable Use(Lang? lang) { return new Scope(lang); }

  public static string T(string german) {
   if(german==null) return null;
   if(Current==Lang.De) return german;
   string en; return En.TryGetValue(german,out en)?en:german;
  }
  public static string F(string german,params object[] args) { return String.Format(CultureInfo.CurrentCulture,T(german),args); }

  const char Mark='§'; // §
  public static string Tok(string german,params object[] args) {
   var sb=new StringBuilder().Append(Mark).Append(german);
   foreach(var a in args) sb.Append('\u001F').Append(Convert.ToString(a,CultureInfo.InvariantCulture));
   return sb.Append(Mark).ToString();
  }
  // Translates all tokens in a text; plain text passes unchanged.
  public static string Expand(string text) {
   if(String.IsNullOrEmpty(text)||text.IndexOf(Mark)<0) return text;
   var sb=new StringBuilder(); int i=0;
   while(i<text.Length) {
    int a=text.IndexOf(Mark,i); if(a<0) { sb.Append(text,i,text.Length-i); break; }
    int z=text.IndexOf(Mark,a+1); if(z<0) { sb.Append(text,i,text.Length-i); break; }
    sb.Append(text,i,a-i);
    var parts=text.Substring(a+1,z-a-1).Split('\u001F');
    var args=new object[parts.Length-1]; Array.Copy(parts,1,args,0,args.Length);
    sb.Append(args.Length==0?T(parts[0]):F(parts[0],args));
    i=z+1;
   }
   return sb.ToString();
  }
 }
}
