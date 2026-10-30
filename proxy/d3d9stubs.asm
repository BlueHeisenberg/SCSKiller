; Raw jmp forwarders to the system d3d9.dll (x64), pointers resolved in DllMain (d3d9rec.cpp); x86 has its own there.
STUB MACRO name
    EXTERN real9_&name:QWORD
    stub9_&name PROC
        jmp QWORD PTR [real9_&name]
    stub9_&name ENDP
ENDM

.code
STUB Ord16
STUB Ord17
STUB Ord18
STUB Ord19
STUB Direct3DCreate9On12
STUB Direct3DCreate9On12Ex
STUB Ord22
STUB Ord23
STUB Direct3DShaderValidatorCreate9
STUB PSGPError
STUB PSGPSampleTexture
STUB D3DPERF_BeginEvent
STUB D3DPERF_EndEvent
STUB D3DPERF_GetStatus
STUB D3DPERF_QueryRepeatFrame
STUB D3DPERF_SetMarker
STUB D3DPERF_SetOptions
STUB D3DPERF_SetRegion
STUB DebugSetLevel
STUB DebugSetMute
STUB Direct3D9EnableMaximizedWindowedModeShim
END
