using System;
using System.Collections;

namespace SerialCAN
{
    public static class Extensions
    {
        public static string ByteArrayToHexString(this byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public static byte[] HexStringToByteArray(this string hex)
        {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }

        public static bool ByteArrayEquals(this byte[] a1, byte[] a2)
        {
            if (a1 == a2)
            {
                return true;
            }

            return StructuralComparisons.StructuralEqualityComparer.Equals(a1, a2);
        }

        public static byte[] ToBigEndian(this byte[] bytes)
        {
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
    }
}
