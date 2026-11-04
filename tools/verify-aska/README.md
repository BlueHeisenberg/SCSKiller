# Verify ASKA shader libraries

Run synthetic format checks without game files:

```sh
dotnet run --project tools/verify-aska
```

Pass installed game folders to also index, retrieve, plan and materialize every shader. These checks read game files
without changing them. Temporary databases are removed when the check finishes. No game files or shader bytecode belong
in this repository.

```sh
dotnet run --project tools/verify-aska -- "/path/to/STAR OCEAN THE DIVINE FORCE" "/path/to/STAR OCEAN - THE LAST HOPE - 4K & Full HD Remaster"
```

The installed builds used for verification contain 35,850 unique shaders in The Divine Force and 71,583 in The Last Hope.
The Divine Force's Steam and GOG shader sets have the same content hash. A different build may require updating these
expected counts after checking its contents.

The synthetic disc checks detection without a game-specific exe, selective retrieval, duplicate exclusion, dictionary
fragments, large unrelated asset ranges, unknown formats, missing shaders, malformed sizes and truncated input. Its LZ
stream includes dictionary matches and overlapping output matches. It contains generated DXBC containers, not game shaders.

## Supported formats

`AskaReader` recognizes `aska0000.bin` and `0000.bin` by their disc headers, not their names alone. The first 64 KiB contain
unused space filled with a repeating 192-byte pattern. The reader recovers that pattern and validates the decoded disc
signature, version 1.1 and magic `0x34829314`. It reads the first five boot allocation slots, each three little-endian
32-bit values: block count, byte size and start block. The shader allocation's size and count give the block size, 4 KiB
in The Divine Force and 2 KiB in The Last Hope. Allocation bounds exclude all subsequent game assets.

The [ASKA disc documentation for Tales of Crestoria](https://github.com/vs-sr-dev/android-talesofcrestoria-doc/blob/main/docs/formats/disc.md)
describes the shared fill pattern, signature and allocation fields. The boot slots, block sizes and shader layouts below
were checked against the installed PC games.

| Boot slot | Contents |
| --- | --- |
| 0 | Metadata, not read |
| 1 | Compressed shader library |
| 2 | Raw boot shaders |
| 3 | Texture, not read |
| 4 | Utility programs, empty in The Last Hope |

The shader library starts with a 36-byte little-endian header: `11hA`, zero, format identifier, record count, dictionary
size in KiB and four zero words. Format `0x000d009d` uses a zstd dictionary of the declared size. Format `0x0000008f` declares
zero dictionary size and uses a fixed 8 KiB dictionary with halfword LZ. These identifiers are library formats, not known
engine release versions. Other identifiers are unsupported. The other ASKA PC games have not been verified.

Records start with a 16-bit identifier, 16-bit binding header size, 32-bit packed size and 32-bit unpacked size. Both sizes
include the binding header. The compressed payload starts at the binding header size. The low two bits of byte 14 are 1
for the tested zstd format and 3 for the tested LZ format. The dictionary is never searched for shader entries.

LZ uses big-endian 16-bit flags, consumed least significant bit first. A set bit copies two literal bytes unchanged. A clear
bit reads a big-endian token: its low 12 bits times two give the match distance, and its high four bits plus two, times two,
give the length. Distance zero ends the stream. Negative source positions refer to the end of the dictionary. Matches may
cross into output and overlap. Both the decoded size and the consumed input must match the declared record payload.

Decoded records and the two raw boot allocations pass through the existing DXBC validator and reflector. The reader hashes
and deduplicates the exact container bytes, pools the shader set in one map and uses the existing DirectX 11 planner.
