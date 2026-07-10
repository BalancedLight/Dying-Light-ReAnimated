from __future__ import annotations

import unittest

from dlanm2_gui.anm2_packed import decode_group_8, encode_group_8, packed_group_length


class Anm2PackedTests(unittest.TestCase):
    def test_encode_decode_flat_group(self) -> None:
        frames = [[0] * 8 for _ in range(16)]
        payload = encode_group_8(frames)

        self.assertEqual(len(payload), 16)
        self.assertEqual(packed_group_length(payload), 16)
        self.assertEqual(decode_group_8(payload), frames)

    def test_encode_decode_ramp_group(self) -> None:
        frames = [[frame * 10, -frame * 5, 3, 0, 0, 0, 0, 0] for frame in range(16)]
        payload = encode_group_8(frames)

        self.assertGreaterEqual(len(payload), 16)
        self.assertEqual(packed_group_length(payload), len(payload))
        self.assertEqual(decode_group_8(payload), frames)

    def test_decode_partial_frame_range(self) -> None:
        frames = [[frame * frame, -frame, frame, 0, 0, 1, -1, 2] for frame in range(16)]
        payload = encode_group_8(frames)

        self.assertEqual(decode_group_8(payload, max_frame=4), frames[:5])


if __name__ == "__main__":
    unittest.main()
