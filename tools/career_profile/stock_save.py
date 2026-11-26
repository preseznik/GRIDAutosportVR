"""Read GRID Autosport's Steam Cloud profile files with the game's own save codec, emulated.

Ported from GRID2VR's tools/career_profile/stock_save.py: the AVX executable's archive descriptor,
crypto provider and save service run under Unicorn from the installed (read-only) game; Windows
services are small stand-ins. Addresses are GRIDAutosport_avx.exe (Steam 2019 build 4011980), found
by matching GRID 2's routines (26 Nov 2026):

  archive descriptor 0x8a1dd0, verify 0x8a3670 (blobs 0x106cb50 / [0x107cf50], 0x1210000 / [0x107cf54]),
  provider factory 0x88c9b0 (stop 0x88ca4f), save service init 0xd74a60, chunk decode 0xd74f20,
  checksum 0x879e70 / 0x879e80, memcpy thunk 0xf68b0c; imports CryptAcquireContextA 0xfc2028,
  CryptCreateHash 0xfc2020, CryptHashData 0xfc2024, CryptGetHashParam 0xfc201c,
  CryptDestroyHash 0xfc202c, CryptReleaseContext 0xfc2030, WriteFile 0xfc20ec,
  QueryPerformanceCounter 0xfc22f8. Writing: file encode 0xd753d0 (GRID 2 0xa7dfe0), directory writer
  0xd7b240 (GRID 2 0x5a96c0; stop 0xd7b520 once the 416 encoded bytes are in the service buffer at
  [service + 0x338] + 0x8000, size at [ebp - 8]; the label callbacks hang off [request + 0x6c]).
"""
import hashlib
import struct
import zlib

from unicorn import Uc, UC_ARCH_X86, UC_HOOK_CODE, UC_MODE_32
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_ECX, UC_X86_REG_EIP, UC_X86_REG_ESP

AVX_SHA256 = "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48"
VR_SHA256 = "9d3e8f9a36571e20707187f1da86967201ab74e72f9f72e8ec446f01dbe59856"  # d3d11 import renamed (VR session)
NAMES = ("savegame@profile0#NXDTVPKI.LKR", "savegame@profile0#QKIGMYYI.LKR")
DIRECTORY = "savegame@profile0#QKRHMYXE"
FILE_IDS = (1838330113, 2210129629)
PROFILE_CAPACITY = 262144
PADDING = bytes([0xBE])


def words(*values):
    return struct.pack(f"<{len(values)}I", *values)


class StockReader:
    """All execution stays in guest memory. The installed game is read only."""

    owner = 0x20000000
    stack = 0x21017000
    stop = 0x22000000
    source = 0x24000000

    def __init__(self, executable):
        image = open(executable, "rb").read()
        if hashlib.sha256(image).hexdigest() not in (AVX_SHA256, VR_SHA256):
            raise ValueError("Unsupported GRIDAutosport_avx.exe for reading careers")
        self.vm = Uc(UC_ARCH_X86, UC_MODE_32)
        pe = struct.unpack_from("<I", image, 60)[0]
        size = (struct.unpack_from("<I", image, pe + 24 + 56)[0] + 0xfff) & ~0xfff
        first = pe + 24 + struct.unpack_from("<H", image, pe + 20)[0]
        self.vm.mem_map(0x400000, size)
        for i in range(struct.unpack_from("<H", image, pe + 6)[0]):
            rva, length, offset = struct.unpack_from("<III", image, first + 40 * i + 12)
            if length:
                self.vm.mem_write(0x400000 + rva, image[offset:offset + length])
        self.vm.mem_map(self.owner, 0x20000)
        self.vm.mem_map(0x21000000, 0x20000)
        self.vm.mem_map(self.stop, 0x1000)
        self.heap = 0x23000000
        self.vm.mem_map(self.heap, 0x80000)
        self.vm.mem_map(self.source, 0x40000)
        self.callbacks = []
        self.bind(0xfc2028, 5, self.acquire_hash_provider)
        self.bind(0xfc2020, 5, self.create_hash)
        self.bind(0xfc2024, 4, self.hash_data)
        self.bind(0xfc201c, 5, self.hash_result)
        self.bind(0xfc202c, 1, lambda handle: int(handle == 2))
        self.bind(0xfc2030, 2, lambda handle, flags: int(handle == 1 and flags == 0))
        self.output = bytearray()
        self.limit = 0
        self.bind(0xfc20ec, 5, self.write_file)
        self.bind(0xfc22f8, 1, self.clock)
        self.bind(None, 3, self.copy_bytes, address=0xf68b0c, cdecl=True)
        allocator = self.owner + 0x1000
        self.put(allocator, allocator + 0x100)
        self.put(allocator + 0x108, self.bind(None, 2, self.allocate))
        self.put(self.owner + 4, allocator)
        self.put(self.owner + 0x5a0, 65536)
        descriptor = self.owner + 0xa0
        if self.call(0x8a1dd0, descriptor, allocator, 0) != descriptor:
            raise ValueError("Stock archive descriptor construction failed")
        if self.call(0x8a3670, descriptor, 0x106cb50, self.word(0x107cf50), 0x1210000, self.word(0x107cf54)):
            raise ValueError("Stock archive descriptor verification failed")
        self.call(0x88c9b0, self.owner, until=0x88ca4f)
        context = self.word(self.owner + 0x620)
        if not context:
            raise ValueError("Stock archive provider unavailable")
        self.service = self.owner + 0x2000
        self.put(self.service + 4, allocator)
        self.put(self.service + 0x10, context)
        if self.call(0xd74a60, self.service):
            raise ValueError("Stock save service initialization failed")

    def word(self, address):
        return struct.unpack("<I", self.vm.mem_read(address, 4))[0]

    def put(self, address, value):
        self.vm.mem_write(address, words(value))

    def bind(self, slot, count, handler, *, address=None, cdecl=False):
        address = address or self.stop + 16 * (len(self.callbacks) + 1)
        if slot:
            self.put(slot, address)

        def forward(vm, _ip, _size, _data):
            sp = vm.reg_read(UC_X86_REG_ESP)
            ret, *args = struct.unpack(f"<{count + 1}I", vm.mem_read(sp, (count + 1) * 4))
            vm.reg_write(UC_X86_REG_EAX, handler(*args))
            vm.reg_write(UC_X86_REG_ESP, sp + 4 + (0 if cdecl else 4 * count))
            vm.reg_write(UC_X86_REG_EIP, ret)

        self.callbacks.append(self.vm.hook_add(UC_HOOK_CODE, forward, begin=address, end=address))
        return address

    def call(self, method, owner, *args, until=None):
        end = self.stop if until is None else until
        self.vm.mem_write(self.stack, words(self.stop, *args))
        self.vm.reg_write(UC_X86_REG_ESP, self.stack)
        self.vm.reg_write(UC_X86_REG_ECX, owner)
        self.vm.emu_start(method, end, count=120_000_000)
        if self.vm.reg_read(UC_X86_REG_EIP) != end:
            raise ValueError("The stock method did not finish")
        return self.vm.reg_read(UC_X86_REG_EAX)

    def allocate(self, length, alignment):
        start = (self.heap + 15) & ~15
        if alignment not in (1, 4, 16) or not 0 < length <= 65536 or start + length > 0x23080000:
            raise ValueError("Unexpected stock allocation")
        self.heap = start + length
        return start

    def copy_bytes(self, dest, source, length):
        if length > 0x40000:
            raise ValueError("Unexpected copy length")
        if length:
            self.vm.mem_write(dest, bytes(self.vm.mem_read(source, length)))
        return dest

    def acquire_hash_provider(self, dest, container, provider, kind, flags):
        if container or kind != 24 or flags != 0xf0000000:
            raise ValueError("Expected the stock unkeyed hash provider")
        self.put(dest, 1)
        return 1

    def create_hash(self, handle, algorithm, key, flags, dest):
        if (handle, algorithm, key, flags) != (1, 0x800c, 0, 0):
            raise ValueError("Expected SHA-256")
        self.hash = hashlib.sha256()
        self.put(dest, 2)
        return 1

    def hash_data(self, handle, data, length, flags):
        if handle != 2 or flags or length > 0x400000:
            raise ValueError("Unexpected stock hash input")
        self.hash.update(self.vm.mem_read(data, length))
        return 1

    def hash_result(self, handle, param, dest, length, flags):
        if (handle, param, flags) != (2, 2, 0) or self.word(length) < 32:
            raise ValueError("Unexpected stock hash output")
        self.vm.mem_write(dest, self.hash.digest())
        self.put(length, 32)
        return 1

    def clock(self, out):
        self.vm.mem_write(out, struct.pack("<Q", 123456789))
        return 1

    def write_file(self, handle, data, length, used, overlapped):
        # Captured into memory: the codec never reaches a real file.
        if (handle != 1 or length > 65536 or len(self.output) + length > self.limit
                or self.word(overlapped + 8) != len(self.output)):
            raise ValueError("Unexpected stock file write")
        self.output.extend(self.vm.mem_read(data, length))
        self.put(used, length)
        return 1

    def encode(self, source):
        """The physical file for one logical save file, and its directory checksum."""
        if not 32 < len(source) < PROFILE_CAPACITY:
            raise ValueError("Invalid logical save size")
        self.vm.mem_write(self.source, source)
        self.output.clear()
        self.limit = (len(source) + 15) & ~15
        entry = self.owner + 0x3000
        previous, used, record = entry + 0x100, entry + 0x104, entry + 0x200
        self.vm.mem_write(entry, bytes(0x210))
        self.put(entry + 0x2c, len(source))
        self.put(record + 8, self.call(0x879e70, 0))
        status = self.call(0xd753d0, self.service, 0, entry, record, previous, 0, 1,
                           len(source), self.source, 0, used)
        if status or self.word(used) != len(source) or len(self.output) != self.limit:
            raise ValueError(f"Incomplete stock file write (status {status:#x}, used {self.word(used)}, "
                             f"wrote {len(self.output)} of {self.limit})")
        return bytes(self.output), self.word(record + 8)


def read_chunks(reader, physical):
    decoded = bytearray()
    used = reader.owner + 0x1800
    dest = reader.source + 65536
    for offset in range(0, len(physical), 65536):
        part = physical[offset:offset + 65536]
        reader.vm.mem_write(reader.source, part)
        if reader.call(0xd74f20, reader.service, reader.source, len(part), dest, 65536, used) or reader.word(used) != len(part):
            raise ValueError("Incomplete stock file read")
        decoded.extend(reader.vm.mem_read(dest, len(part)))
    return bytes(decoded)


def checksum(reader, *parts):
    value = reader.call(0x879e70, 0)
    for part in parts:
        reader.vm.mem_write(reader.source, part)
        value = reader.call(0x879e80, 0, value, reader.source, len(part))
    return value


def directory_fields(reader, physical):
    """The load-menu label and the per-file rows (id, logical size, checksum)."""
    if len(physical) != 416:
        raise ValueError("Expected a normal two-file profile directory")
    raw = read_chunks(reader, physical)
    stored, count = struct.unpack_from("<II", raw, 0x180)
    if count != 2 or checksum(reader, raw[:0x180], raw[0x184:]) != stored:
        raise ValueError("Invalid profile directory checksum or count")
    end = next((i for i in range(0, 256, 2) if raw[i:i + 2] == bytes(2)), None)
    if end is None:
        raise ValueError("Directory has no terminated label")
    return raw[:end].decode("utf-16-le"), list(struct.iter_unpack("<III", raw[0x188:]))


def write_directory(writer, label, rows):
    """The 416-byte directory for a UTF-16 label (with its terminator) and (id, size, checksum) rows."""
    op = writer.owner + 0x5000
    request, profile, vtable, vector, records = (op + i * 0x100 for i in range(1, 6))
    writer.put(op, request)
    writer.put(request + 0x6c, profile)  # GRID 2: + 0x70
    writer.put(profile, vtable)

    def copy_label(out):
        # Autosport's string buffer is (capacity in characters, pointer); GRID 2 has a vtable first.
        if writer.word(out) < len(label) // 2:
            raise ValueError("Stock directory label buffer too small")
        writer.vm.mem_write(writer.word(out + 4), label)
        return 0

    writer.put(vtable + 0x34, writer.bind(None, 1, copy_label))
    writer.put(vtable + 0x38, writer.bind(None, 0, lambda: 1))
    entries = b"".join(struct.pack("<III", *entry) for entry in rows)
    writer.put(vector + 4, records)
    writer.put(vector + 8, records + len(entries))
    writer.vm.mem_write(records, entries)
    writer.call(0xd7b240, writer.service, op, vector, until=0xd7b520)
    size = writer.word(writer.vm.reg_read(UC_X86_REG_EBP) - 8)
    if size != 416:
        raise ValueError("Unexpected stock directory output size")
    return bytes(writer.vm.mem_read(writer.word(writer.service + 0x338) + 0x8000, size))


# The two career files hold the same component group: the progress file as one raw-deflate member and
# its SHA-256, the settings file the same framing padded with 0xBE so both fill one 256 KiB buffer.
def progress_file(member):
    digest = hashlib.sha256(member).digest()
    if not member or len(member) + len(digest) > PROFILE_CAPACITY:
        raise ValueError("Progress file does not fit the retail profile buffer")
    return member + digest


def decode_logical_member(data, *, padded):
    reader = zlib.decompressobj(-15)
    try:
        group = reader.decompress(data, 2 * 1024 * 1024)
    except zlib.error as error:
        raise ValueError("Invalid compressed profile member") from error
    if not reader.eof:
        raise ValueError("Expected one complete, bounded profile member")
    member_size = len(data) - len(reader.unused_data)
    checked = progress_file(data[:member_size])
    tail = data[len(checked):]
    if not data.startswith(checked) or (tail.strip(PADDING) if padded else tail):
        raise ValueError("Profile checksum or padding differs from the stock format")
    return group


def encode_settings_file(group, progress_size):
    checked = progress_file(zlib.compress(group, wbits=-15))
    size = PROFILE_CAPACITY - progress_size
    if progress_size <= 0 or len(checked) > size:
        raise ValueError("Insufficient settings padding for the new progress file")
    return checked.ljust(size, PADDING)


def read_career(reader, folder):
    """The load-menu label, directory rows and the two decoded groups of a normal career folder."""
    from pathlib import Path
    folder = Path(folder)
    label, rows = directory_fields(reader, (folder / DIRECTORY).read_bytes())
    if tuple(r[0] for r in rows) != FILE_IDS or sum(r[1] for r in rows) != PROFILE_CAPACITY:
        raise ValueError("Unsupported career directory")
    groups = []
    for i, (name, (_, size, stored)) in enumerate(zip(NAMES, rows)):
        logical = read_chunks(reader, (folder / name).read_bytes())[:size]
        if checksum(reader, logical) != stored:
            raise ValueError("Career file checksum disagrees with its directory")
        groups.append(decode_logical_member(logical, padded=i == 1))
    return label, rows, groups


def compress_to_size(group, size):
    """One raw-deflate member of exactly `size` bytes (GRID2VR's compress_to_size): a sync flush ends
    on a byte boundary, and the final source bytes go in a stored block sized to make up the rest."""
    for level in (6, 7, 8, 9):
        member = zlib.compress(group, level=level, wbits=-15)
        if len(member) == size:
            return member
        tail, tried = size - len(member), set()
        for _ in range(16):
            if not 0 < tail < len(group) or tail in tried:
                break
            tried.add(tail)
            stream = zlib.compressobj(level, wbits=-15)
            prefix = stream.compress(group[:-tail]) + stream.flush(zlib.Z_SYNC_FLUSH)
            member = prefix + zlib.compress(group[-tail:], level=0, wbits=-15)
            if len(member) == size:
                return member
            tail += size - len(member)
    raise ValueError("Could not encode the career at a whole number of cipher blocks")


def progress_member(group):
    # The game's save codec takes whole 16-byte blocks; its own progress files always fill them.
    member = zlib.compress(group, wbits=-15)
    if (len(member) + 32) % 16 == 0:
        return member
    for size in range((len(member) + 32 + 15) // 16 * 16 - 32, len(member) + 32 * 16, 16):
        try:
            return compress_to_size(group, size)
        except ValueError:
            continue
    raise ValueError("Could not encode the career at a whole number of cipher blocks")


def write_career(writer, reader, label, groups):
    """The three physical files for two groups and a label, each read back through the stock codec."""
    progress = progress_file(progress_member(groups[0]))
    frames = [progress, encode_settings_file(groups[1], len(progress))]
    files, rows = {}, []
    for i, (name, frame, file_id) in enumerate(zip(NAMES, frames, FILE_IDS)):
        if decode_logical_member(frame, padded=i == 1) != groups[i]:
            raise ValueError("Career group did not round-trip")
        physical, digest = writer.encode(frame)
        if read_chunks(reader, physical)[:len(frame)] != frame or checksum(reader, frame) != digest:
            raise ValueError("Career file failed stock readback")
        files[name] = physical
        rows.append((file_id, len(frame), digest))
    raw_label = (label + chr(0)).encode("utf-16-le")
    if len(raw_label) > 256:
        raise ValueError("Career label too long")
    directory = write_directory(writer, raw_label, rows)
    if directory_fields(reader, directory) != (label, rows):
        raise ValueError("Career directory did not round-trip")
    files[DIRECTORY] = directory
    return files
