using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HddSpindown.Windows {
 // Determines whether a (possibly UAC-filtered) token belongs to a local administrator.
 // WindowsIdentity.Groups hides deny-only groups, so the token is read directly.
 public static class TokenCheck {
  [DllImport("advapi32.dll",SetLastError=true)]
  static extern bool GetTokenInformation(IntPtr token,int cls,IntPtr info,int length,out int needed);
  [DllImport("advapi32.dll",SetLastError=true)]
  static extern bool IsValidSid(IntPtr sid);
  const int TokenGroups=2;
  const uint SE_GROUP_ENABLED=0x4, SE_GROUP_USE_FOR_DENY_ONLY=0x10;

  public static bool IsAdministratorAccount(WindowsIdentity identity) {
   if(identity==null) return false;
   var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
   if(identity.User!=null&&identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
   IntPtr token=identity.Token; int needed;
   GetTokenInformation(token,TokenGroups,IntPtr.Zero,0,out needed);
   if(needed<=0) return false;
   IntPtr buffer=Marshal.AllocHGlobal(needed);
   try {
    if(!GetTokenInformation(token,TokenGroups,buffer,needed,out needed)) return false;
    int count=Marshal.ReadInt32(buffer);
    int size=IntPtr.Size*2; // SID_AND_ATTRIBUTES { PSID; DWORD } padded
    IntPtr first=buffer+IntPtr.Size; // GroupCount (DWORD) padded to pointer alignment
    for(int i=0;i<count;i++) {
     IntPtr entry=first+i*size;
     IntPtr sid=Marshal.ReadIntPtr(entry); uint attributes=(uint)Marshal.ReadInt32(entry+IntPtr.Size);
     if(sid==IntPtr.Zero||!IsValidSid(sid)) continue;
     if(new SecurityIdentifier(sid)==admins&&(attributes&(SE_GROUP_ENABLED|SE_GROUP_USE_FOR_DENY_ONLY))!=0) return true;
    }
    return false;
   } finally { Marshal.FreeHGlobal(buffer); }
  }
 }
}
