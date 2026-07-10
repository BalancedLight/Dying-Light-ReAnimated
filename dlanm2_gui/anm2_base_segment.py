from __future__ import annotations


def align_down_16(value: int) -> int:
    return int(value) & ~0xF


def align_up_16(value: int) -> int:
    return (int(value) + 0x0F) & ~0xF


def align_up_4(value: int) -> int:
    return (int(value) + 0x03) & ~0x3


def anm2_base_table_start(base_offset: int) -> int:
    # Engine ASM: lea rbx, [base+0x19]; and rbx, -0x10.
    return align_down_16(int(base_offset) + 0x19)


def anm2_base_table_start_relative_for_aligned_base() -> int:
    return 0x10


def anm2_direct_values_start(base_offset: int, packed_table_byte_count: int) -> int:
    return align_up_16(anm2_base_table_start(base_offset) + int(packed_table_byte_count))


def anm2_mask_table_start(base_offset: int, packed_table_byte_count: int, direct_count: int) -> int:
    return align_up_4(anm2_direct_values_start(base_offset, packed_table_byte_count) + 4 * int(direct_count))
