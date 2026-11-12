using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HddSpindown.Windows {
 // Raw Win32 surface. Only query, lock, mount-point and ATA STANDBY IMMEDIATE
 // operations are declared. No format, partition, dismount or controller reset.
 static class Native {
  public const uint GENERIC_READ=0x80000000, GENERIC_WRITE=0x40000000;
  public const uint FILE_SHARE_READ=1, FILE_SHARE_WRITE=2, OPEN_EXISTING=3;

  public const uint IOCTL_STORAGE_QUERY_PROPERTY=0x002D1400;
  public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER=0x002D1080;
  public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX=0x00070050;
  public const uint IOCTL_DISK_GET_LENGTH_INFO=0x0007405C;
  public const uint IOCTL_DISK_GET_PARTITION_INFO_EX=0x00070048;
  public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS=0x00560000;
  public const uint FSCTL_LOCK_VOLUME=0x00090018;
  public const uint FSCTL_UNLOCK_VOLUME=0x0009001C;
  public const uint FSCTL_IS_VOLUME_DIRTY=0x00090078;
  public const uint IOCTL_ATA_PASS_THROUGH=0x0004D02C;

  public const int ERROR_FILE_NOT_FOUND=2, ERROR_MORE_DATA=234, ERROR_NO_MORE_FILES=18, ERROR_INSUFFICIENT_BUFFER=122;

  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true)]
  public static extern bool DeviceIoControl(SafeFileHandle handle,uint code,IntPtr input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlapped);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern IntPtr FindFirstVolume(StringBuilder name,uint length);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool FindNextVolume(IntPtr handle,StringBuilder name,uint length);
  [DllImport("kernel32.dll",SetLastError=true)]
  public static extern bool FindVolumeClose(IntPtr handle);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool GetVolumePathNamesForVolumeName(string volume,[Out] char[] paths,uint size,out uint needed);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool GetVolumeNameForVolumeMountPoint(string root,StringBuilder volume,uint size);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool DeleteVolumeMountPoint(string root);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool SetVolumeMountPoint(string root,string volume);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern uint QueryDosDevice(string device,StringBuilder target,int length);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  public static extern bool GetVolumeInformation(string root,StringBuilder name,int nameSize,out uint serial,out uint maxComponent,out uint flags,StringBuilder fileSystem,int fileSystemSize);
  [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
  public static extern void SHChangeNotify(int code,uint flags,string item,IntPtr unused);

  [StructLayout(LayoutKind.Sequential)]
  public struct AtaPassThroughEx {
   public ushort Length, AtaFlags;
   public byte PathId, TargetId, Lun, ReservedAsUchar;
   public uint DataTransferLength, TimeOutValue, ReservedAsUlong;
   public UIntPtr DataBufferOffset;
   [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)] public byte[] PreviousTaskFile;
   [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)] public byte[] CurrentTaskFile;
  }
  public const byte ATA_STANDBY_IMMEDIATE=0xE0;
  public const ushort ATA_FLAGS_DRDY_REQUIRED=0x01;

  public static Win32Exception Error(string action) { int e=Marshal.GetLastWin32Error(); return new Win32Exception(e,action+" (Windows "+e+"): "+new Win32Exception(e).Message); }

  // Runs an IOCTL into a growing buffer and returns the filled bytes.
  public static byte[] Query(SafeFileHandle handle,uint code,byte[] input,int initial,string action) {
   int size=initial;
   for(int attempt=0;attempt<6;attempt++) {
    IntPtr inBuf=IntPtr.Zero, outBuf=Marshal.AllocHGlobal(size);
    try {
     if(input!=null) { inBuf=Marshal.AllocHGlobal(input.Length); Marshal.Copy(input,0,inBuf,input.Length); }
     uint returned;
     if(DeviceIoControl(handle,code,inBuf,input==null?0u:(uint)input.Length,outBuf,(uint)size,out returned,IntPtr.Zero)) {
      var result=new byte[returned]; Marshal.Copy(outBuf,result,0,(int)returned); return result;
     }
     int error=Marshal.GetLastWin32Error();
     if(error!=ERROR_INSUFFICIENT_BUFFER&&error!=ERROR_MORE_DATA) throw new Win32Exception(error,action+" (Windows "+error+"): "+new Win32Exception(error).Message);
     size*=4;
    } finally { Marshal.FreeHGlobal(outBuf); if(inBuf!=IntPtr.Zero) Marshal.FreeHGlobal(inBuf); }
   }
   throw new InvalidOperationException(HddSpindown.Core.L.F("{0}: Antwort zu groß.",action));
  }
  public static void Control(SafeFileHandle handle,uint code,string action) {
   uint returned;
   if(!DeviceIoControl(handle,code,IntPtr.Zero,0,IntPtr.Zero,0,out returned,IntPtr.Zero)) throw Error(action);
  }
  public static SafeFileHandle Open(string path,uint access,string action) {
   var h=CreateFile(path,access,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
   if(h.IsInvalid) { var ex=Error(action+" "+path); h.Dispose(); throw ex; }
   return h;
  }
  public static SafeFileHandle TryOpen(string path,uint access) {
   var h=CreateFile(path,access,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
   if(h.IsInvalid) { h.Dispose(); return null; }
   return h;
  }
 }
}
