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
  QueryPerformanceCounter 0xfc22f8.
"""
import hashlib
import struct

from unicorn import Uc, UC_ARCH_X86, UC_HOOK_CODE, UC_MODE_32
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_EIP, UC_X86_REG_ESP

AVX_SHA256 = "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48"
NAMES = ("savegame@profile0#NXDTVPKI.LKR", "savegame@profile0#QKIGMYYI.LKR")
DIRECTORY = "savegame@profile0#QKRHMYXE"


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
        if hashlib.sha256(image).hexdigest() != AVX_SHA256:
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
        self.bind(0xfc20ec, 5, self.refuse_write)
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

    def refuse_write(self, *_):
        raise ValueError("The reader never writes files")


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
