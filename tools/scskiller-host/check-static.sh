#!/bin/sh
# Fails unless the file is a static x86-64 ELF: no program interpreter and no shared library it needs, so no libc of
# the host's is ever loaded. Usage: tools/scskiller-host/check-static.sh <file>
set -eu
f=${1:?usage: check-static.sh <file>}
bad() {
  echo "$f: $1" >&2
  exit 1
}
header=$(readelf -hW "$f") || bad "not an ELF file"
echo "$header" | grep -Eq '^ *Class: +ELF64$' || bad "not a 64-bit ELF"
echo "$header" | grep -Eq '^ *Machine: +Advanced Micro Devices X86-64$' || bad "not x86-64"
echo "$header" | grep -Eq '^ *Type: +(EXEC|DYN) ' || bad "not an executable"
if readelf -lW "$f" | grep -q 'INTERP'; then bad "has a program interpreter (PT_INTERP)"; fi
if readelf -dW "$f" | grep -q '(NEEDED)'; then bad "needs shared libraries: $(readelf -dW "$f" | grep '(NEEDED)' | tr -s ' ' | tr '\n' ' ')"; fi
echo "$f: static x86-64"
