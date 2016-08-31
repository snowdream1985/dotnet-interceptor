﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace NETInterceptor
{
    /*
    *    0:   e8 8b eb 0a 62          call   0x620aeb90 ; call PrecodeFixupThunk or jmp CompiledMethod or call CompiledMethod
    *    5:   5e                      db 0x5e ; or 0xCC (no info), 0x5F (jitted)
    *    6:   04                      db 0x04 ; MethodDescChunkIndex
    *    7:   01                      db 0x01 ; PrecodeChunkIndex
    */
    public abstract unsafe class FixupPrecode : Precode
    {
        protected static Lazy<IntPtr> _precodeFixupThunk = new Lazy<IntPtr>(GetPrecodeFixupThunk);

        protected FixupPrecode(IntPtr methodPtr)
            : base(methodPtr)
        {
        }

        public new static FixupPrecode Create(IntPtr methodPtr)
        {
            switch (Utils.CurrentArchitecture) {
                case Architecture.X86:
                    return new FixupPrecodeX86(methodPtr);
                case Architecture.X64:
                    return new FixupPrecodeX64(methodPtr);
                default:
                    throw new NotSupportedException("Unsupported architecture.");
            }
        }

        public override IntPtr JmpToTargetPtr
        {
            get { return _methodPtr; }
        }

        public bool IsJitted
        {
            get
            {
                var ptr = (byte *)_methodPtr.ToPointer();
                var operand = new IntPtr(*(int*)(ptr + 1));
                return operand != PrecodeFixupThunk &&
                    (*(ptr + 5) == 0xCC || *(ptr + 5) == 0x5F);
            }
        }

        public override IntPtr TargetPtr
        {
            get
            {
                if (IsJitted) {
                    // return CompiledMethod addr
                    var ptr = (byte *)_methodPtr.ToPointer();
                    var offset = *(int *)(ptr + 1);
                    return new IntPtr(ptr + offset + 5);
                } else {
                    // return ThePreStub addr via PrecodeFixupThunk
                    // e8 xx xx xx xx ; call PrecodeFixupThunk
                    return new IntPtr(GetThePreStubPtr((byte*)PrecodeFixupThunk.ToPointer()));
                }
            }
        }

        public byte PrecodeChunkIndex
        {
            get
            {
                var p = (byte*)_methodPtr.ToPointer();
                return *(p + 7);
            }
        }

        public byte MethodDescChunkIndex
        {
            get
            {
                var p = (byte*)_methodPtr.ToPointer();
                return *(p + 6);
            }
        }

        protected abstract byte* GetThePreStubPtr(byte* precodeFixupThunkPtr);

        private byte* GetMethodDescChunkBasePtr()
        {
            return (byte*)_methodPtr.ToPointer() + 8 + PrecodeChunkIndex * 8;
        }

        public static bool IsFixupPrecode(IntPtr methodPtr)
        {
            var ptr = (byte *)methodPtr.ToPointer();
            var b1 = *ptr;
            var b2 = *(ptr + 5);

            return (b1 == 0xE8 || b1 == 0xE9) &&
                (b2 == 0xCC || b2 == 0x5E || b2 == 0x5F);
        }

        public static IntPtr PrecodeFixupThunk { get { return _precodeFixupThunk.Value; } }

        private static IntPtr GetPrecodeFixupThunk()
        {
            var ptr = DetectPrecode.GetPrecodePtr();

            Debug.Assert(FixupPrecode.HasPrecode(ptr));

            var fixupThunkPtr = (byte*)ptr.ToPointer();
            var fixupThunkOffset = *(int*)(fixupThunkPtr + 1);

            return new IntPtr(fixupThunkPtr + fixupThunkOffset + 5);
        }

        private class FixupPrecodeX86 : FixupPrecode
        {
            public FixupPrecodeX86(IntPtr methodPtr)
                : base(methodPtr)
            {
            }

            public override IntPtr MethodDescPtr
            {
                get
                {
                    var methodDescChunkBase = *(int*)GetMethodDescChunkBasePtr();
                    return new IntPtr(methodDescChunkBase + MethodDescChunkIndex * sizeof(void*));
                }
            }

            protected override byte* GetThePreStubPtr(byte* precodeFixupThunkPtr)
            {
                if (Utils.CurrentRuntime == Runtime.CLR2) {
                    throw new NotImplementedException();
                }
                
                if (Utils.CurrentRuntime >= Runtime.CLR4) {
                    /*
                       0:   58                      pop    eax
                       1:   56                      push   esi
                       2:   57                      push   edi
                       3:   0f b6 70 02             movzx  esi,BYTE PTR [eax+0x2]
                       7:   0f b6 78 01             movzx  edi,BYTE PTR [eax+0x1]
                       b:   8b 44 f0 03             mov    eax,DWORD PTR [eax+esi*8+0x3]
                       f:   8d 04 b8                lea    eax,[eax+edi*4]
                      12:   5f                      pop    edi
                      13:   5e                      pop    esi
                      14:   e9 7f 03 00 00          jmp    0x398
                    */

                    Debug.Assert(*precodeFixupThunkPtr == 0x58); // pop eax
                    precodeFixupThunkPtr += 0x14;
                    Debug.Assert(*precodeFixupThunkPtr == 0xE9); // jmp xx xx xx xx
                    var preStubOffset = *(int*)(precodeFixupThunkPtr + 1);

                    return precodeFixupThunkPtr + preStubOffset + 5;
                }

                throw new NotSupportedException("Unsupported runtime.");
            }
        }

        private unsafe class FixupPrecodeX64 : FixupPrecode
        {
            public FixupPrecodeX64(IntPtr methodPtr)
                : base(methodPtr)
            {
            }

            public override IntPtr MethodDescPtr
            {
                get
                {
                    var methodDescChunkBase = *(long*)GetMethodDescChunkBasePtr();
                    return new IntPtr(methodDescChunkBase + MethodDescChunkIndex * sizeof(void*));
                }
            }

            protected override byte* GetThePreStubPtr(byte* precodeFixupThunkPtr)
            {
                /*
                 0:   58                      pop    rax
                 1:   4c 0f b6 50 02          movzx  r10,BYTE PTR [rax+0x2]
                 6:   4c 0f b6 58 01          movzx  r11,BYTE PTR [rax+0x1]
                 b:   4a 8b 44 d0 03          mov    rax,QWORD PTR [rax+r10*8+0x3]
                10:   4e 8d 14 d8             lea    r10,[rax+r11*8]
                14:   e9 b7 02 00 00          jmp    0x2d0 ; ThePreStub
                */

                Debug.Assert(*precodeFixupThunkPtr == 0x58); // pop rax
                precodeFixupThunkPtr += 0x14;
                Debug.Assert(*precodeFixupThunkPtr == 0xE9); // jmp xx xx xx xx
                var preStubOffset = *(int*)(precodeFixupThunkPtr + 1);

                return precodeFixupThunkPtr + preStubOffset + 5;
            }
        }        
    }
}
