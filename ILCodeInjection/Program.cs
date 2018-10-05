using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ILCodeInjection.Cor;

namespace ILCodeInjection
{
    class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr GetJitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetVersionIdentifierDelegate(IntPtr thisPtr, out Guid versionIdentifier);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CompileMethodDelegate(
            IntPtr thisPtr,
            IntPtr comp, // ICorJitInfo* comp, /* IN */
            ref CORINFO_METHOD_INFO info, // struct CORINFO_METHOD_INFO  *info,               /* IN */
            uint flags, // unsigned /* code:CorJitFlag */   flags,          /* IN */
            out IntPtr nativeEntry, // BYTE                        **nativeEntry,       /* OUT */
            out int nativeSizeOfCode // ULONG* nativeSizeOfCode    /* OUT */
        );

        [DllImport("kernel32", EntryPoint = "VirtualProtect")]
        private static extern int VirtualProtect(IntPtr lpAddress, IntPtr dwSize, MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);

        [DllImport("kernel32", EntryPoint = "VirtualAlloc")]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, int dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

        [DllImport("kernel32", EntryPoint = "VirtualFree")]
        private static extern int VirtualFree(IntPtr lpAddress, IntPtr dwSize, FreeType freeType);

        [Flags]
        private enum FreeType
        {
            Decommit = 0x4000,
            Release = 0x8000,
        }

        [Flags]
        private enum AllocationType
        {
            Commit = 0x1000,
            Reserve = 0x2000,
            Decommit = 0x4000,
            Release = 0x8000,
            Reset = 0x80000,
            Physical = 0x400000,
            TopDown = 0x100000,
            WriteWatch = 0x200000,
            LargePages = 0x20000000
        }

        [Flags]
        private enum MemoryProtection
        {
            Execute = 0x10,
            ExecuteRead = 0x20,
            ExecuteReadWrite = 0x40,
            ExecuteWriteCopy = 0x80,
            NoAccess = 0x01,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
            WriteCopy = 0x08,
            GuardModifierflag = 0x100,
            NoCacheModifierflag = 0x200,
            WriteCombineModifierflag = 0x400
        }

        static readonly Guid ExpectedJitVersion = new Guid("0ba106c8-81a0-407f-99a1-928448c1eb62");

        private static readonly byte[] DelegateTrampolineCode = {
            // mov rax, 0000000000000000h ;
            0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            // jmp rax
            0xFF, 0xE0
        };

        static void Main(string[] args)
        {
            Hook();
            Console.WriteLine(One());

            Console.Read();
        }

        static int One()
        {
            return 1;
        }

        static void Dummy() => InjectHere();
        
        delegate void InjectDelegate();
        static InjectDelegate InjectHereInstance;
        static IntPtr InjectHerePtr;
        static CompileMethodDelegate DefaultCompileMethod;
        static CompileMethodDelegate NewCompileMethod;
        static bool isHooked = false;
        static void Hook()
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (Path.GetFileName(module.FileName) == "clrjit.dll")
                {
                    var jitaddr = GetProcAddress(module.BaseAddress, "getJit");
                    var getJit = Marshal.GetDelegateForFunctionPointer<GetJitDelegate>(jitaddr);
                    var jit = getJit();
                    var jitTable = Marshal.ReadIntPtr(jit);
                    var getVerIdPtr = Marshal.ReadIntPtr(jitTable, IntPtr.Size * 4);
                    var getVerId = Marshal.GetDelegateForFunctionPointer<GetVersionIdentifierDelegate>(getVerIdPtr);
                    getVerId(jitaddr, out var version);

                    if (version != ExpectedJitVersion)
                        throw new Exception("Unexpected Jit Version xD");

                    var compileMethodPtr = Marshal.ReadIntPtr(jitTable, 0);
                    DefaultCompileMethod = Marshal.GetDelegateForFunctionPointer<CompileMethodDelegate>(compileMethodPtr);
                    NewCompileMethod = CompileMethod;
                    var newCompileMethodPtr = Marshal.GetFunctionPointerForDelegate(NewCompileMethod);

                    var trampolinePtr = AllocateTrampoline(newCompileMethodPtr);
                    var trampoline = Marshal.GetDelegateForFunctionPointer<CompileMethodDelegate>(trampolinePtr);
                    var emptyInfo = default(CORINFO_METHOD_INFO);
                    
                    trampoline(IntPtr.Zero, IntPtr.Zero, ref emptyInfo, 0, out var entry, out var size);
                    FreeTrampoline(trampolinePtr);

                    VirtualProtect(jitTable, new IntPtr(IntPtr.Size), MemoryProtection.ReadWrite, out var oldFlags);
                    Marshal.WriteIntPtr(jitTable, 0, newCompileMethodPtr);
                    VirtualProtect(jitTable, new IntPtr(IntPtr.Size), oldFlags, out oldFlags);
                    isHooked = true;

                    InjectHereInstance = InjectHere;
                    InjectHerePtr = Marshal.GetFunctionPointerForDelegate<InjectDelegate>(InjectHere);
                    
                    // NewCompileMethod(IntPtr.Zero, IntPtr.Zero, ref emptyInfo, 0, out var _, out var _);
                    
                    break;
                }
            }
        }

        static bool isFindingInjectHereAddress = false;
        static int CompileMethod(
            IntPtr thisPtr,
            IntPtr comp, // ICorJitInfo* comp, /* IN */
            ref CORINFO_METHOD_INFO info, // struct CORINFO_METHOD_INFO  *info,               /* IN */
            uint flags, // unsigned /* code:CorJitFlag */   flags,          /* IN */
            out IntPtr nativeEntry, // BYTE                        **nativeEntry,       /* OUT */
            out int nativeSizeOfCode // ULONG* nativeSizeOfCode    /* OUT */
        )
        {
            if (!isHooked)
            {
                nativeEntry = IntPtr.Zero;
                nativeSizeOfCode = 0;
                return 0;
            }
            var res = DefaultCompileMethod(thisPtr, comp, ref info, flags, out nativeEntry, out nativeSizeOfCode);
            byte[] ilcodes = new byte[info.ILCodeSize];
            Marshal.Copy(info.ILCode, ilcodes, 0, info.ILCodeSize);
            
            byte[] nativecodes = new byte[nativeSizeOfCode];
            Marshal.Copy(nativeEntry, nativecodes, 0, nativeSizeOfCode);

            if (isFindingInjectHereAddress)
            {

                isFindingInjectHereAddress = false;
                return res;
            }

            if (i++ == 0)
            {
                Marshal.WriteByte(nativeEntry, 37, 2);
            }
            return res;
        }

        static int i = 0;

        static IntPtr AllocateTrampoline(IntPtr dest)
        {
            var jmp = VirtualAlloc(IntPtr.Zero, DelegateTrampolineCode.Length, AllocationType.Commit, MemoryProtection.ExecuteReadWrite);
            Marshal.Copy(DelegateTrampolineCode, 0, jmp, DelegateTrampolineCode.Length);
            Marshal.WriteIntPtr(jmp, 2, dest);
            return jmp;
        }

        static void FreeTrampoline(IntPtr trampoline)
        {
            VirtualFree(trampoline, new IntPtr(DelegateTrampolineCode.Length), FreeType.Release);
        }
         
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void InjectHere() { }
    }
}
