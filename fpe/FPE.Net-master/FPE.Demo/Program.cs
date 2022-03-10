using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO;

namespace FPE.Demo
{
    class Program
    {

        public static int DigitValue(char c) => (int)(c - '0');

        public static int[] DoubleLuhn = new int[] { 0, 2, 4, 6, 8, 1, 3, 5, 7, 9 };
        /// <summary>
        /// Generate a valid SIN by appending the correct ninth digit
        /// </summary>
        /// <param name="ps"></param>
        /// <returns></returns>
        public static char GetSinLastdigit(string s, int start)
        {
            if (start + 8 > s.Length) return '?';
            var luhnSum = DigitValue(s[start++]) + DoubleLuhn[DigitValue(s[start++])] +
                            DigitValue(s[start++]) + DoubleLuhn[DigitValue(s[start++])] +
                            DigitValue(s[start++]) + DoubleLuhn[DigitValue(s[start++])] +
                            DigitValue(s[start++]) + DoubleLuhn[DigitValue(s[start++])];
            return (char)('0' + 10 - (luhnSum % 10));
        }
        public static bool IsValidSin(string s, int start, int length)
        {
            return length == 9 && start + 8 < s.Length && GetSinLastdigit(s, start) == s[start + 8];
        }

        public static byte[] emptyByteArray = new byte[] {};
        static void Main(string[] args)
        {
            var plaintext = args[0];
            var plaintextDigits = plaintext.Select(d => (int)(d - '0')).ToArray();
            var isSin = IsValidSin(plaintext, 0, plaintext.Length);
            Console.WriteLine($"Plaintext: {plaintext} {(isSin ? "Valid SIN" : "")}");

            var pwd = args[1];
            var pwdSalt = Encoding.ASCII.GetBytes("Rm2fSdh5sofQ");
            var pbkdf2 = new Rfc2898DeriveBytes(pwd, pwdSalt, 1000);

            var tweakBytes = Encoding.ASCII.GetBytes(args[2]);


            using (var aes = new AesCng())
            {
                aes.Key = pbkdf2.GetBytes(aes.KeySize / 8);
                aes.IV = pbkdf2.GetBytes(aes.BlockSize / 8);
                aes.Mode = CipherMode.CBC;

                // encrypt
                var fpenc = new FPE.Net.FF1(10, tweakBytes.Length);
                var middleDigits = new int[isSin ? 7 : plaintextDigits.Length];
                Array.Copy(plaintextDigits, isSin ? 1 : 0, middleDigits, 0, isSin ? 7 : plaintextDigits.Length);
                var cipherTextFpe = fpenc.encrypt(aes.Key, tweakBytes, middleDigits);
                var cipherTextFpeString = string.Join("", cipherTextFpe.Select(d => (char)(d + '0')));
                var encryptedSin = isSin 
                    ? $"{plaintextDigits[0]}{cipherTextFpeString}{GetSinLastdigit(plaintextDigits[0] + cipherTextFpeString, 0)}" 
                    : cipherTextFpeString;
                Console.WriteLine($"Encrypted: {encryptedSin} {(IsValidSin(encryptedSin, 0, encryptedSin.Length) ? "Valid SIN" : "")}");

                // decrypt
                var ciphertextFpeDigits = (isSin ? encryptedSin.Substring(1, 7) : encryptedSin).Select(d => (int)(d - '0')).ToArray();
                var recoveredFpeDigits = fpenc.decrypt(aes.Key, tweakBytes, ciphertextFpeDigits);
                var recoveredFpeString = isSin 
                    ? $"{plaintextDigits[0]}{string.Join("", recoveredFpeDigits.Select(d => (char)(d + '0')))}"
                    : string.Join("", recoveredFpeDigits.Select(d => (char)(d + '0')));
                var recoveredsin = isSin 
                    ? $"{recoveredFpeString}{GetSinLastdigit(recoveredFpeString, 0)}"
                    : recoveredFpeString;
                Console.WriteLine($"Recovered: {recoveredsin} {(IsValidSin(recoveredsin, 0, recoveredsin.Length) ? "Valid SIN" : "")}");
            }
        }    
    }
}
