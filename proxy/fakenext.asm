; nvapi64.dll's nvapi_QueryInterface for `selftest nvqi`: int3 padding, then a 2-byte first instruction (the REX form of
; push, as a compiler's hot-patchable entry) and a sub; FakeNext_NvQiInside stops a thread between the two.
EXTERN fake_nv_qi:PROC
EXTERN fake_nv_qi_inside:PROC
EXTERN FakeNv_OnOpts:PROC
EXTERN fake_ffx_create:PROC
.code
ALIGN 16
    db 16 dup (0CCh)
nvapi_QueryInterface PROC FRAME
    db 40h
    push rbx
    .pushreg rbx
    sub rsp, 32
    .allocstack 32
    .endprolog
    call fake_nv_qi
    add rsp, 32
    pop rbx
    ret
nvapi_QueryInterface ENDP

; nvapi_QueryInterface(id) by a thread that runs its first instruction here, waits in fake_nv_qi_inside, then goes on at
; its second.
FakeNext_NvQiInside PROC FRAME
    db 40h
    push rbx
    .pushreg rbx
    sub rsp, 32
    .allocstack 32
    .endprolog
    mov [rsp + 48], rcx
    call fake_nv_qi_inside
    mov rcx, [rsp + 48]
    add rsp, 32
    jmp nvapi_QueryInterface + 2
FakeNext_NvQiInside ENDP

; NvAPI_D3D12_SetCreatePipelineStateOptions for `selftest nvopts`: the driver's first instruction (a 5-byte store), then
; FakeNv_OnOpts with the caller's arguments.
ALIGN 16
FakeNv_SetOpts PROC
    mov [rsp + 18h], rbx
    sub rsp, 28h
    call FakeNv_OnOpts
    add rsp, 28h
    ret
FakeNv_SetOpts ENDP

;
; Entries the inline hook must leave alone (`selftest nvqi`, as three NVAPI functions the proxy hooks): a 1-byte first
; instruction, a 2-byte one after only 4 int3, and one after 5 int3 that follow a byte that is neither an int3 nor a ret
; (they could be the tail of a live instruction's immediate).
ALIGN 16
    db 16 dup (0CCh)
FakeNv_OneByte PROC
    push rbp
    pop rbp
    xor eax, eax
    ret
FakeNv_OneByte ENDP

ALIGN 16
    db 12 dup (90h)
    db 4 dup (0CCh)
FakeNv_ShortPad PROC
    db 40h
    push rbx
    pop rbx
    xor eax, eax
    ret
FakeNv_ShortPad ENDP

ALIGN 16
    db 11 dup (90h)
    db 5 dup (0CCh)
FakeNv_LivePad PROC
    db 40h
    push rbx
    pop rbx
    xor eax, eax
    ret
FakeNv_LivePad ENDP

; An entry another hooker patches while the proxy hooks it (`selftest nvqi`, SCSKILLER_TEST_HOOK_RACE): a 5-byte first
; instruction that its jmp replaces.
ALIGN 16
FakeNv_Raced PROC
    mov [rsp + 8], rbx
    xor eax, eax
    ret
FakeNv_Raced ENDP

; FidelityFX's ffxCreateContext for `selftest framesfg ffx`: the loader's hot-patch padding and a 3-byte first instruction,
; then fake_ffx_create.
ALIGN 16
    db 16 dup (0CCh)
ffxCreateContext PROC
    db 48h, 8Bh, 0C0h
    jmp fake_ffx_create
ffxCreateContext ENDP
END
