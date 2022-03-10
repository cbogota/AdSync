// Adapted from: https://github.com/paya-cz/siphash
// Author:          Pavel Werl
// License:         Public Domain
// SipHash website: https://131002.net/siphash/

using System;

namespace Collections
{
    /// <summary>
    /// This class is immutable and thread-safe.
    /// </summary>
    public sealed class SipHash
    {
        #region Fields

        /// <summary>
        /// Part of the initial 256-bit internal state.
        /// </summary>
        private readonly ulong initialState0;
        /// <summary>
        /// Part of the initial 256-bit internal state.
        /// </summary>
        private readonly ulong initialState1;

        #endregion

        #region Constructors

        /// <summary>Initializes a new instance of SipHash pseudo-random function using the specified 128-bit key.</summary>
        /// <param name="key"><para>Key for the SipHash pseudo-random function.</para>
        public SipHash(Guid key)
        {
            GuidToUInt64(key, out initialState0, out initialState1);
            this.initialState0 ^= 0x736f6d6570736575UL;
            this.initialState1 ^= 0x646f72616e646f6dUL;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a 128-bit SipHash key.
        /// </summary>
        public Guid Key => GuidFromUInt64(this.initialState0 ^ 0x736f6d6570736575UL,
                                          this.initialState1 ^ 0x646f72616e646f6dUL);

        #endregion

        #region Methods

        public static unsafe void GuidToUInt64(Guid value, out ulong x, out ulong y)
        {
            ulong* ptr = (ulong*)&value;
            x = *ptr++;
            y = *ptr;
        }
        public static unsafe Guid GuidFromUInt64(ulong x, ulong y)
        {
            ulong* ptr = stackalloc ulong[2];
            ptr[0] = x;
            ptr[1] = y;
            return *(Guid*)ptr;
        }

        /// <summary>Computes 64-bit SipHash tag for the specified message.</summary>
        /// <param name="data"><para>The byte array for which to computer SipHash tag.</para><para>Must not be null.</para></param>
        /// <returns>Returns 64-bit (8 bytes) SipHash tag.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
        public long Compute(ReadOnlySpan<byte> data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            // SipHash internal state
            var v0 = this.initialState0;
            var v1 = this.initialState1;
            // It is faster to load the initialStateX fields from memory again than to reference v0 and v1:
            var v2 = 0x1F160A001E161714UL ^ this.initialState0;
            var v3 = 0x100A160317100A1EUL ^ this.initialState1;

            // We process data in 64-bit blocks
            ulong block;

            unsafe
            {
                // Start of the data to process
                fixed (byte* dataStart = &data[0])
                {
                    // The last 64-bit block of data
                    var finalBlock = dataStart + (data.Length & ~7);

                    // Process the input data in blocks of 64 bits
                    for (var blockPointer = (ulong*)dataStart; blockPointer < finalBlock;)
                    {
                        block = *blockPointer++;

                        v3 ^= block;

                        // Round 1
                        v0 += v1;
                        v2 += v3;
                        v1 = v1 << 13 | v1 >> 51;
                        v3 = v3 << 16 | v3 >> 48;
                        v1 ^= v0;
                        v3 ^= v2;
                        v0 = v0 << 32 | v0 >> 32;
                        v2 += v1;
                        v0 += v3;
                        v1 = v1 << 17 | v1 >> 47;
                        v3 = v3 << 21 | v3 >> 43;
                        v1 ^= v2;
                        v3 ^= v0;
                        v2 = v2 << 32 | v2 >> 32;

                        // Round 2
                        v0 += v1;
                        v2 += v3;
                        v1 = v1 << 13 | v1 >> 51;
                        v3 = v3 << 16 | v3 >> 48;
                        v1 ^= v0;
                        v3 ^= v2;
                        v0 = v0 << 32 | v0 >> 32;
                        v2 += v1;
                        v0 += v3;
                        v1 = v1 << 17 | v1 >> 47;
                        v3 = v3 << 21 | v3 >> 43;
                        v1 ^= v2;
                        v3 ^= v0;
                        v2 = v2 << 32 | v2 >> 32;

                        v0 ^= block;
                    }

                    // Load the remaining bytes
                    block = (ulong)data.Length << 56;
                    switch (data.Length & 7)
                    {
                        case 7:
                            block |= *(uint*)finalBlock | (ulong)*(ushort*)(finalBlock + 4) << 32 | (ulong)*(finalBlock + 6) << 48;
                            break;
                        case 6:
                            block |= *(uint*)finalBlock | (ulong)*(ushort*)(finalBlock + 4) << 32;
                            break;
                        case 5:
                            block |= *(uint*)finalBlock | (ulong)*(finalBlock + 4) << 32;
                            break;
                        case 4:
                            block |= *(uint*)finalBlock;
                            break;
                        case 3:
                            block |= *(ushort*)finalBlock | (ulong)*(finalBlock + 2) << 16;
                            break;
                        case 2:
                            block |= *(ushort*)finalBlock;
                            break;
                        case 1:
                            block |= *finalBlock;
                            break;
                    }
                }
            }

            // Process the final block
            {
                v3 ^= block;

                // Round 1
                v0 += v1;
                v2 += v3;
                v1 = v1 << 13 | v1 >> 51;
                v3 = v3 << 16 | v3 >> 48;
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0 << 32 | v0 >> 32;
                v2 += v1;
                v0 += v3;
                v1 = v1 << 17 | v1 >> 47;
                v3 = v3 << 21 | v3 >> 43;
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2 << 32 | v2 >> 32;

                // Round 2
                v0 += v1;
                v2 += v3;
                v1 = v1 << 13 | v1 >> 51;
                v3 = v3 << 16 | v3 >> 48;
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0 << 32 | v0 >> 32;
                v2 += v1;
                v0 += v3;
                v1 = v1 << 17 | v1 >> 47;
                v3 = v3 << 21 | v3 >> 43;
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2 << 32 | v2 >> 32;

                v0 ^= block;
                v2 ^= 0xff;
            }

            // 4 finalization rounds
            {
                // Round 1
                v0 += v1;
                v2 += v3;
                v1 = v1 << 13 | v1 >> 51;
                v3 = v3 << 16 | v3 >> 48;
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0 << 32 | v0 >> 32;
                v2 += v1;
                v0 += v3;
                v1 = v1 << 17 | v1 >> 47;
                v3 = v3 << 21 | v3 >> 43;
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2 << 32 | v2 >> 32;

                // Round 2
                v0 += v1;
                v2 += v3;
                v1 = v1 << 13 | v1 >> 51;
                v3 = v3 << 16 | v3 >> 48;
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0 << 32 | v0 >> 32;
                v2 += v1;
                v0 += v3;
                v1 = v1 << 17 | v1 >> 47;
                v3 = v3 << 21 | v3 >> 43;
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2 << 32 | v2 >> 32;

                // Round 3
                v0 += v1;
                v2 += v3;
                v1 = v1 << 13 | v1 >> 51;
                v3 = v3 << 16 | v3 >> 48;
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0 << 32 | v0 >> 32;
                v2 += v1;
                v0 += v3;
                v1 = v1 << 17 | v1 >> 47;
                v3 = v3 << 21 | v3 >> 43;
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2 << 32 | v2 >> 32;

                // Round 4
                v0 += v1;
                v2 += v3;
                v1 = v1 << 13 | v1 >> 51;
                v3 = v3 << 16 | v3 >> 48;
                v1 ^= v0;
                v3 ^= v2;
                v0 = v0 << 32 | v0 >> 32;
                v2 += v1;
                v0 += v3;
                v1 = v1 << 17 | v1 >> 47;
                v3 = v3 << 21 | v3 >> 43;
                v1 ^= v2;
                v3 ^= v0;
                v2 = v2 << 32 | v2 >> 32;
            }

            return (long)((v0 ^ v1) ^ (v2 ^ v3));
        }

        #endregion
    }
}