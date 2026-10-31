using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RoutePilot.Agent;

internal sealed record NativeWinDivertReflectionEvent(int Pid,int Layer,ulong Flags,long OpenedTimestamp,int Event,string Filter);

// REFLECT observes kernel handle metadata, not application traffic. Open with
// NO_INSTALL: readiness observation must never install/replace a driver itself.
internal static class NativeWinDivertReflection
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi,SetLastError=true)]delegate IntPtr Open([MarshalAs(UnmanagedType.LPStr)]string filter,int layer,short priority,ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi,SetLastError=true)]delegate bool Receive(IntPtr handle,IntPtr packet,uint capacity,out uint length,IntPtr address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi,SetLastError=true)]delegate bool Shutdown(IntPtr handle,int how);
    [UnmanagedFunctionPointer(CallingConvention.Winapi,SetLastError=true)]delegate bool Close(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi,SetLastError=true)]delegate bool Format([MarshalAs(UnmanagedType.LPStr)]string filter,int layer,[Out]byte[] output,uint size);
    internal static bool Matches(NativeWinDivertReflectionEvent observed,int pid,long started,string canonicalFilter)=>
        observed.Pid==pid&&observed.Layer==0&&observed.Event==8&&observed.OpenedTimestamp>=started&&
        (observed.Flags&~0x30UL)==0&&observed.Filter==canonicalFilter;

    internal static async Task WaitAsync(string root,string expectedFilter,int pid,long started,Func<bool> current,CancellationToken stop)
    {
        if(!OperatingSystem.IsWindows()||pid<=0||started<=0||expectedFilter.Length is <1 or >4096)throw new InvalidDataException("Invalid WinDivert readiness identity.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        IntPtr library=NativeLibrary.Load(Path.Combine(root,"WinDivert.dll"));
        IntPtr handle=new(-1);
        try
        {
            T Function<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));
            var open=Function<Open>("WinDivertOpen");var receive=Function<Receive>("WinDivertRecv");
            var shutdown=Function<Shutdown>("WinDivertShutdown");var close=Function<Close>("WinDivertClose");var format=Function<Format>("WinDivertHelperFormatFilter");
            string Canonical(string filter)
            {
                byte[] buffer=new byte[32768];
                if(!format(filter,0,buffer,(uint)buffer.Length))throw new Win32Exception(Marshal.GetLastPInvokeError());
                int length=Array.IndexOf(buffer,(byte)0);if(length<0)throw new InvalidDataException("Unbounded WinDivert filter.");
                return Encoding.ASCII.GetString(buffer,0,length);
            }
            string expected=Canonical(expectedFilter);
            try
            {
                while(handle==new IntPtr(-1))
                {
                    timeout.Token.ThrowIfCancellationRequested();if(!current())throw new IOException("Zapret worker changed before kernel readiness.");
                    handle=open($"processId == {pid} and layer == NETWORK and event == OPEN",4,0,0x15);
                    if(handle==new IntPtr(-1))
                    {
                        int error=Marshal.GetLastPInvokeError();
                        if(error is not (2 or 1060))throw new Win32Exception(error);
                        await Task.Delay(25,timeout.Token);
                    }
                }
                // Shutdown wakes a blocking Recv. Join that receive before
                // releasing unmanaged buffers or unloading the native library.
                using var cancellation=timeout.Token.Register(()=>shutdown(handle,1));
                await Task.Run(()=>
                {
                    IntPtr packet=IntPtr.Zero,address=IntPtr.Zero;
                    try
                    {
                        packet=Marshal.AllocHGlobal(65536);address=Marshal.AllocHGlobal(80);
                        for(int budget=0;budget<32;budget++)
                        {
                            timeout.Token.ThrowIfCancellationRequested();if(!current())throw new IOException("Zapret worker changed during kernel readiness.");
                            if(!receive(handle,packet,65536,out uint length,address))
                            {
                                timeout.Token.ThrowIfCancellationRequested();throw new Win32Exception(Marshal.GetLastPInvokeError());
                            }
                            if(length is <1 or >65536)throw new InvalidDataException("Invalid reflected filter size.");
                            byte[] bytes=new byte[length+1];Marshal.Copy(packet,bytes,0,(int)length);
                            int zero=Array.IndexOf(bytes,(byte)0);string observed=Canonical(Encoding.ASCII.GetString(bytes,0,zero));
                            uint metadata=unchecked((uint)Marshal.ReadInt32(address,8));
                            var item=new NativeWinDivertReflectionEvent(Marshal.ReadInt32(address,24),Marshal.ReadInt32(address,28),
                                unchecked((ulong)Marshal.ReadInt64(address,32)),Marshal.ReadInt64(address,16),(int)((metadata>>8)&255),observed);
                            if((metadata&255)==4&&Matches(item,pid,started,expected)&&current())return;
                        }
                        throw new IOException("WinDivert readiness event budget exceeded.");
                    }
                    finally{if(packet!=IntPtr.Zero)Marshal.FreeHGlobal(packet);if(address!=IntPtr.Zero)Marshal.FreeHGlobal(address);}
                });
                timeout.Token.ThrowIfCancellationRequested();if(!current())throw new IOException("Zapret worker retired before readiness acknowledgement.");
            }
            finally{if(handle!=new IntPtr(-1)&&!close(handle))throw new Win32Exception(Marshal.GetLastPInvokeError(),"WinDivert reflection handle close failed.");}
        }
        finally{NativeLibrary.Free(library);}
    }
}
