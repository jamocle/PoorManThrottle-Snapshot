using System;

namespace PoorManThrottle.Core.Helpers
{
    public static class Obfuscator
    {
        // Public API
        // 12-char input -> 12 hex char output (deterministic)
        public static string Obfuscate12(string input, uint key = 0xC0FFEE12)
        {
            if (input == null || input.Length != 12)
                return string.Empty; // must be exactly 12 chars

            // Compress/mix 12 bytes -> 6 bytes by XOR pairing
            byte[] b = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                b[i] = (byte)((byte)input[i] ^ (byte)input[i + 6]);
            }

            // Split key into bytes (same as ESP32 logic)
            byte k0 = (byte)(key & 0xFF);
            byte k1 = (byte)((key >> 8) & 0xFF);
            byte k2 = (byte)((key >> 16) & 0xFF);
            byte k3 = (byte)((key >> 24) & 0xFF);

            for (int i = 0; i < 6; i++)
            {
                byte k =
                    (byte)(
                        (byte)(k0 + (byte)(i * 17)) ^
                        RotL8(k1, (byte)i) ^
                        RotR8(k2, (byte)(6 - i)) ^
                        (byte)(k3 + (byte)(i * 29))
                    );

                byte x = (byte)(b[i] + (byte)(i * 31));
                x ^= k;
                x = RotL8(x, (byte)(1 + (i % 7)));
                x = (byte)(x + (byte)(k ^ (byte)(i * 13)));
                b[i] = x;
            }

            // 6 bytes -> 12 hex chars (uppercase)
            char[] output = new char[12];
            for (int i = 0; i < 6; i++)
            {
                output[i * 2]     = NybToHex((byte)(b[i] >> 4));
                output[i * 2 + 1] = NybToHex((byte)(b[i] & 0x0F));
            }

            return new string(output);
        }

        private static byte RotL8(byte value, byte shift)
        {
            shift &= 7;
            return (byte)((value << shift) | (value >> (8 - shift)));
        }

        private static byte RotR8(byte value, byte shift)
        {
            shift &= 7;
            return (byte)((value >> shift) | (value << (8 - shift)));
        }

        private static char NybToHex(byte value)
        {
            value &= 0x0F;
            return (value < 10)
                ? (char)('0' + value)
                : (char)('A' + (value - 10));
        }
    }
}