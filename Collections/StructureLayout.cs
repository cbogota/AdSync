using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;


// SOME ASSUMPTIONS ABOUT .NET MEMORY MANAGEMENT
//
// The following assumptions relate to the safety of using the functionality contained 
// in this file. Specifically, the deserialization routine ReadRawArray, writes 
// directly to the memory addresses of an array allocated on the heap. The other functions
// read from the heap and, therefore, are somewhat safer in that they cannot corrupt the heap.
//
// This set of functions makes use of the following assumptions about .NET Garbage 
// Collection and heap management.
//
// 1) In general we assume that if we obtain the unmanaged address of an object 
// on the heap at two points in time, t1 and t2, and those addresses are the same 
// that the object has remained at that address for the entire duration between 
// time t1 and t2. From a statistical perspective, if the duration is short
// this assumption is highly likely to hold true, however, if we can also assume
// that (during heap compaction and garbage collection) objects are only moved 
// in one direction within a given generation, then our assumption about the 
// stability of the location of an objects between times t1 and t2 becomes a
// guarantee. 
//
// 2) That the large object heap is not compacted (although we detect it if it is)
// and therefore that objects on the large object heap do not need to be pinned.
//
// 3) That the threshold for allocation on the large object heap is not greater than
// 85,000 bytes.
//


// Raw Serialization streaming format
//--------------------------------------------------------------------------------
//HEADER
//--------------------------------------------------------------------------------
//(uint)MagicStart			- 0xFEEDBEEF
//(int)ElementSize
//(int)LayoutDescriptorLength		
//(byte[])LayoutDescriptor
//(uint)MagicMiddle         - 0xCAFEF00D
//--------------------------------------------------------------------------------
//PAYLOAD
//--------------------------------------------------------------------------------
//(int)ElementsWrittenCount
//(int)OriginalArrayLength
//[Element]....
//(uint)MagicEnd		    - 0xDEADBEEF
//--------------------------------------------------------------------------------


namespace Collections
{
    /// <summary>
    /// ReadRawArrayResult is returned from the ReadRawArray extension method.
    /// It captures the array that was deserialized from the stream as well as
    /// the number of elements deserialized (because the caller to ReadRawArray
    /// can request that the array returned is longer than required) as well
    /// as the number of bytes read from the stream during deserialization.
    /// </summary>
    /// <typeparam name="T">
    /// The element type for the deserialized array.
    /// </typeparam>
    public struct ReadRawArrayResult<T>
    {
        /// <summary>
        /// The array that was deserialized.
        /// </summary>
        public readonly T[] Array;

        /// <summary>
        /// The actual number of elements deserialized into the array.
        /// The deserialied elements are always stored starting at index zero.
        /// </summary>
        public readonly int ElementsRead;

        /// <summary>
        /// The total number of bytes read from the stream during deserialization.
        /// </summary>
        public readonly int TotalBytesReadFromStream;

        internal ReadRawArrayResult(T[] array, int elementsRead, int bytesRead)
        {
            this.Array = array;
            this.ElementsRead = elementsRead;
            this.TotalBytesReadFromStream = bytesRead;
        }
    }

    public enum RawReadAllocationMode
    {
        UseOriginalCapacity = 0,
        AllocateRequiredPlusPercentage = 1,
        AllocateRequiredPlusElementCount = 2,
    }



    /// <summary>
    /// Provides exention methods to return the layout information for a structure
    /// and to read and write raw serialized arrays to a stream.
    /// </summary>
    public static class StructureLayoutExtensionMethods
    {
        /// <summary>
        /// A Type extension method that returns the inner layout of a structure.
        /// Example: var myStructLayout = MyStuct.GetStructureLayout();
        /// See the StructureLayout class for more information.
        /// </summary>
        public static StructureLayout GetStructureLayout(this Type t)
        {
            return StructureLayout.GetLayout(t);
        }

        public static bool IsNullableType(this Type t)
        {
            return t.IsGenericType && t.GetGenericTypeDefinition().FullName == typeof(int?).GetGenericTypeDefinition().FullName;
        }

        const uint MagicStart = 0xFEEDBEEF;
        const uint MagicMiddle = 0xCAFEF00D;
        const uint MagicEnd = 0xDEADBEEF;
        const int StreamBufferSize = 1 << 16;

        /// <summary>
        /// An extension method to write an array directly to a stream using raw serialization.
        /// Example: int bytesWritten = MyStream.WriteRawArray (anArray, anArray.Length);
        /// The array is serialized in raw format directly to the stream.
        /// The array element type must be suitable for raw serialization. This in general means 
        /// that the element type must be a structure. It may contain other structures, however,
        /// none of the fields anywhere in the composite structure may be a reference type.
        /// 
        /// If the element type is not raw serializable or if the count provided exceeds the 
        /// number of elements in the array, then stream is not written to.
        /// 
        /// </summary>
        /// <param name="writeStream">
        /// The stream to be written to.
        /// </param>
        /// <param name="src"></param>
        /// <param name="count">
        /// The number of elements in the array (beginning at index zero) to be written.
        /// This must be less than or equal to the length of the array.
        /// </param>
        /// <returns>
        /// The number of bytes that were written to the stream.
        /// </returns>
        public static int WriteRawArray(this Stream writeStream, Array src, int count)
        {
            if (src == null)
                throw new ArgumentException("The source array must not be null");
            var layout = src.GetType().GetElementType().GetStructureLayout();
            if (!layout.IsRawSerializable)
                throw new ArgumentException("The source array element type (" + layout.StructureType.Name + ") does not support raw serialization (it contains reference types)");
            if (src.Length < count)
                throw new ArgumentException($"The count parameter ({count}) exceeds the length of the source array ({src.Length})");

            var bw = new BinaryWriter(writeStream);

            // Write Magic Start to stream
            bw.Write(MagicStart);
            // Write ElementSize to stream
            bw.Write(layout.TotalSize);
            // Write LayoutDescriptorLength to stream
            bw.Write(layout.Descriptor.Length);
            // Write LayoutDescriptor to stream
            writeStream.Write(layout.Descriptor, 0, layout.Descriptor.Length);
            // Write Magic Middle to stream
            bw.Write(MagicMiddle);

            // Write ElementCount to stream
            bw.Write(count);
            //. Write original array length
            bw.Write(src.Length);

            int batchElements = StreamBufferSize / layout.TotalSize;
            int batchCount = count / batchElements;
            int fullBatchCount = batchElements * batchCount;
            int endBatchElements = count - fullBatchCount; ;
            int byteCount = layout.TotalSize * batchElements;
            byte[] buf = new byte[byteCount];
            for (var b = 0; b < count; b += batchElements)
            {
                if (b == fullBatchCount)
                    byteCount = layout.TotalSize * endBatchElements;
                // cannot use GCHandle.Alloc(src, GCHandleType.Pinned) option here because Alloc will only pin when the object is an array of a primitive type
                var gch = GCHandle.Alloc(src);
                Marshal.Copy(Marshal.UnsafeAddrOfPinnedArrayElement(src, b), buf, 0, byteCount);
                gch.Free();
                writeStream.Write(buf, 0, byteCount);
            }
            // Write Magic End to stream
            bw.Write(MagicEnd);

            // return the number of bytes written to the stream
            // magic + elementSize + layoutDescriptorSize + layoutDescriptor + magic + elementCount + elements + magic
            return 4 + 4 + 4 + layout.Descriptor.Length + 4 + 4 + (layout.TotalSize * count) + 4;
        }

        /// <summary>
        /// Deserializes a raw serialized array from the provided stream.
        /// 
        /// NOTE: The technique used here does not pin the array. As a result it is
        /// possible that heap compaction may move the array between the call to 
        /// Marshal.UnsafeAddrOfPinnedArrayElement and the subsequent call to 
        /// Marshal.Copy. This is detected by calling Marshal.UnsafeAddrOfPinnedArrayElement
        /// again after the copy and confirming that heap compaction has not moved our array.
        /// If we detect this, however, we will have corrupted the heap. In this event, 
        /// we throw an InvalidOperationException and recommend the caller terminate and
        /// restart the process as soon as possible.
        /// 
        /// Pinning would have been nice, but calling GCHandle.Alloc with the GCHandleType.Pinned option 
        /// only works if the object is an array of a primitive type

        ///
        /// The possibility of Heap corruption is eliminated, however, if the array being deserialized
        /// is allocated on the Large Object Heap (i.e. the array is larger that 85K) because 
        /// the current .net frameworks (4.0 and under) do not compact the large object heap.
        /// For this reason, we ensure that we allocate a minimum of 85K for the array during
        /// deserialization. If the resulting array is smaller than this we resize the resulting
        /// array before returning it.
        /// 
        /// UPDATE: GCHandle.Alloc is now used to pin the array to prevent movement by the GC.
        /// Why did we go through all the trouble above? Didn't know about GCHandle.Alloc()...
        /// </summary>
        /// 
        /// <typeparam name="T">
        /// The element type of the array. This must match what was originally serialized.
        /// If it does not match, the remaining serialization data in the stream is read past.
        /// If the serialization data in the stream is invalid, an InvalidDataException
        /// is thrown and the stream position is undefined.
        /// </typeparam>
        /// <param name="readStream">
        /// The stream to read the serialization data from.
        /// </param>
        /// <param name="additionalCapacityPercentage"></param>
        /// Permits the caller to specify that the returned array has additional capacity allocated.
        /// If this parameter is zero, the returned array will be exactly the right length to 
        /// store the deserialized elements.
        /// <returns>
        /// A ReadRawArrayResult structure that contains the resulting deserialized array
        /// along with related information.
        /// </returns>
        public static ReadRawArrayResult<T> ReadRawArray<T>(this Stream readStream, RawReadAllocationMode mode,  int minimumCapacity, int additionalCapacity)
        {
            if (readStream == null)
                throw new ArgumentException("readStream may not be null");

            if (minimumCapacity < 0)
                throw new ArgumentException("minimumCapacity must not be negative"); 

            if (additionalCapacity < 0) 
                throw new ArgumentException("additionalCapacity must not be negative"); 

            var layout = typeof(T).GetStructureLayout();
            if (!layout.IsRawSerializable)
                throw new ArgumentException("The array element type (" + layout.StructureType.Name + ") does not support raw serialization (it contains reference types)");

            var br = new BinaryReader(readStream);

            // Read Magic Start from stream
            var readMagicStart = br.ReadUInt32();
            if (readMagicStart != MagicStart)
                throw new InvalidDataException("Invalid Magic Start value found in deserialization stream");

            // Read ElementSize from stream
            var readElementSize = br.ReadInt32();
            if (readElementSize != layout.TotalSize)
                throw new InvalidDataException("The deserialization stream in incompatible with the array element type (length different)");

            // Read LayoutDescriptorSize from stream
            var readDescriptorSize = br.ReadInt32();
            if (readDescriptorSize != layout.Descriptor.Length)
                throw new InvalidDataException("The deserialization stream in incompatible with the array element type (descriptor length different)");

            // Read LayoutDescriptor from stream
            byte[] readLayoutDescriptor = new byte[layout.Descriptor.Length];
            readStream.Read(readLayoutDescriptor, 0, readLayoutDescriptor.Length);
            for (var i = 0; i < readLayoutDescriptor.Length; i++)
                if (readLayoutDescriptor[i] != layout.Descriptor[i])
                    throw new InvalidDataException("The deserialization stream in incompatible with the array element type (descriptor data different)");

            // Read Magic Middle from stream
            var readMagicMiddle = br.ReadUInt32();
            if (readMagicMiddle != MagicMiddle)
                throw new InvalidDataException("Invalid Magic Middle value found in deserialization stream");

            // Read ElementCount from stream
            var readElementCount = br.ReadInt32();
            // Read the original array element count from stream
            var readOriginalElementCount = br.ReadInt32();

            if (readOriginalElementCount < readElementCount)
                throw new InvalidDataException("Invalid element count fields in deserialization stream");

            // determine the final size required for the array 
            int desiredElementCount = 0;
            switch (mode)
            {
                case RawReadAllocationMode.UseOriginalCapacity:
                    desiredElementCount = readOriginalElementCount;
                    break;

                case RawReadAllocationMode.AllocateRequiredPlusPercentage:
                    desiredElementCount = readElementCount + (int)(((long)readElementCount * additionalCapacity) / 100L);
                    break;

                case RawReadAllocationMode.AllocateRequiredPlusElementCount:
                    desiredElementCount = readElementCount + additionalCapacity;
                    break;
            }

            // ensure the new array size is at least as large as callers minimumCapacity provided
            desiredElementCount = Math.Max(desiredElementCount, minimumCapacity);

            // ensure we get the destination array onto the large object heap (at least temporarily)
            T[] ary = new T[Math.Max(layout.MinimumElementsToForceOnLargeObjectHeap, desiredElementCount)];
            int batchElements = StreamBufferSize / layout.TotalSize;
            int batchCount = readElementCount / batchElements;
            int fullBatchCount = batchElements * batchCount;
            int endBatchElements = readElementCount - fullBatchCount; ;
            int byteCount = layout.TotalSize * batchElements;
            byte[] buf = new byte[byteCount];
            for (int b = 0; b < readElementCount; b += batchElements)
            {
                if (b == fullBatchCount)
                    byteCount = layout.TotalSize * endBatchElements;
                readStream.Read(buf, 0, byteCount);
                // cannot use GCHandle.Alloc(src, GCHandleType.Pinned) option here because Alloc will only pin when the object is an array of a primitive type
                var gch = GCHandle.Alloc(ary);
                var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(ary, b);
                Marshal.Copy(buf, 0, ptr, byteCount);
                gch.Free();
            }

            // Read Magic End from stream
            var readMagicEnd = br.ReadUInt32();
            if (readMagicEnd != MagicEnd)
                throw new InvalidDataException("Invalid Magic End value found in deserialization stream");

            // resize the array if it was made larger than necessary
            // this has the effect of getting the array off the large object heap if it does not need to be there.
            if (ary.Length != desiredElementCount)
                Array.Resize(ref ary, desiredElementCount);

            // magic + elementSize + layoutDescriptorSize + layoutDescriptor + magic + elementCount + originalCount + elements + magic
            return new ReadRawArrayResult<T>(ary, readElementCount, 4 + 4 + 4 + layout.Descriptor.Length + 4 + 4 + 4 + (layout.TotalSize * readElementCount) + 4);
        }
    }

    /// <summary>
    /// This class captures the internal layout information (field sizes and byte offsets)
    /// for a structure. The structure may include other structures nested within it.
    /// The fields of the structure are recursed down to the individual primitive fields.
    /// Reference type fields are also captured although their offsets are not.
    /// In addition to the detailed list of field layout information, the overall size
    /// of the structure is captured, the amount of wasted space within the 
    /// structure (due to padding), as well as an indicator of whether or not arrays of
    /// this structure type will support raw serialization (through the WriteRawArray and 
    /// ReadRawArray Stream extension methods.)
    /// 
    /// This class's constructor is private. Instances must be obtained through the static
    /// factory method GetLayout, or the Type extension method GetStructureLayout. The layout 
    /// for a given type is only computed once. Subsequent requests returned a cached copy.
    /// </summary>
    public class StructureLayout
    {
        /// <summary>
        /// StructureLayout returns a list of instances od this FieldLayout structure for each one 
        /// of the primitive or reference type fields encountered within the structure.
        /// </summary>
        public struct FieldLayout
        {
            /// <summary>
            /// The type of the field. This will either be a primitive type or a reference type.
            /// </summary>
            public readonly Type FieldType;

            /// <summary>
            /// The "path" to this field within the structure. For a field that is a direct member of
            /// the provided structure this will be in the form "(FullTypeName)FieldName". If the structure 
            /// contains nested structures, however, this path will have multiple elements seperated 
            /// by a period. For example if the root structure contains a DateTime field named OrderDate, 
            /// then Path would look like this for that field: "(System.DateTime)OrderDate.(System.UInt64)dateData"
            /// </summary>
            public readonly string Path;

            /// <summary>
            /// The size of the field in bytes.
            /// </summary>
            public readonly int Size;

            /// <summary>
            /// The byte offset of the field within the structure.
            /// If the field is a reference type, Offset is not determined and will always be returned as -1
            /// </summary>
            public readonly int Offset;

            internal FieldLayout(Type type, string path, int size, int offset)
            {
                this.FieldType = type;
                this.Path = path;
                this.Size = size;
                this.Offset = offset;
            }
        }

        // Returns the size in bytes for any given primitive type
        private static int GetPrimitiveSize(Type t)
        {
            if (t.IsPrimitive)
                return Buffer.ByteLength(Array.CreateInstance(t, 1));
            else
                throw new ArgumentException(t.FullName + " is not a primitive type");
        }

        // Returns a value with a non-zero LSB for each primitive type (in boxed form)
        // this assumes a little-endian architecture.
        private static object GetPrimitiveProbeValue(Type t)
        {
            if (t.IsPrimitive)
            {
                Array ary = Array.CreateInstance(t, 1);
                Buffer.SetByte(ary, 0, 1);
                return ary.GetValue(0);
            }
            else
                throw new ArgumentException(t.FullName + " is not a primitive type");
        }

        // Copies raw bytes from the array into the destination byte array (if supplied.)
        // The bytesNeeded out parameter is returned to indicate how many bytes each element needs 
        // in the destination byte array. If the supplied destination byte array was null or its length 
        // was less than bytesNeeded, no bytes are copied to the array.
        private static void GetRawBytesElementZero(Array ary, byte[] dest, out int bytesNeeded)
        {
            // lots of rechecking of addresses to ensure they haven't moved due to compaction, we retry if they have moved
            // cannot use GCHandle.Alloc(src, GCHandleType.Pinned) option here because Alloc will only pin when the object is an array of a primitive type
            var gch = GCHandle.Alloc(ary);
            try
            {
                var ptr0 = Marshal.UnsafeAddrOfPinnedArrayElement(ary, 0);
                var ptr1 = Marshal.UnsafeAddrOfPinnedArrayElement(ary, 1);
                bytesNeeded = (int) (ptr1.ToInt64() - ptr0.ToInt64());
                // if bytesNeeded is not greater than zero or 
                // if the destination array is not null and too small we have a problem...
                if (bytesNeeded <= 0 || (dest != null && dest.Length < bytesNeeded))
                    throw new ArgumentException($"dest array ({dest?.Length}) is not big enough to receive the structure ({bytesNeeded})");
                if (dest != null) Marshal.Copy(ptr0, dest, 0, bytesNeeded);
            }
            finally
            {
                gch.Free();
            }
        }

        // Scans the raw bytes in element zero of the provided array to determine the offset 
        // of the first non-zero byte. This is used to determine the actual field offset within
        // a structure. It is a total hack but ended up being a lot less hassle than calling the 
        // CLR's unmanaged metadata or profiling api. It depends on GetPrimitiveProbeValue always
        // producing a primitive type value that has a non-zero first byte.
        // a list of integer offsets may be provided to indicate specific offsets to ignore. This
        // is used when probing for nullable types to handle the nullable types private hasValue field.
        private static int GetFirstNonZeroByteOffset(Array ary, byte[] dest, List<int> ignoreOffsets)
        {
            GetRawBytesElementZero(ary, dest, out var bytesNeeded);
            for (var i = 0; i < bytesNeeded && i < dest.Length; i++)
                if (dest[i] != 0 && !ignoreOffsets.Contains(i)) return i;
            return -1;
        }

        /// <summary>
        /// returns a new object with a single field (indicated by the path) set
        /// this routine sets the field by recursing through a provided list of 
        /// nested structures all the way back to the root structure.
        /// For example consider the case where the outer root structure of type 
        /// RootStructure has a field of type InnerStructure named Inner which in 
        /// turn has field of type InnerMostStructure named InnerMost which has a 
        /// field of type int name TheInt.
        /// This function would be called with type = RootStructure, and fieldInfoPath
        /// would contain the FieldInfo objects in this order beginning at index zero:
        /// {TheInt, InnerMost, Inner}. fieldIdx is set to 0 and object is set to the 
        /// int value that we want to set TheInt to.
        ///
        /// To accomplish this SetFieldByPath creates a new InnerMost object and sets
        /// its TheInt field to the supplied int value. Then SetFieldByPAth is called
        /// with 1 provided for fieldIdx and the newly created InnerMostStructure object
        /// value. This call then creates a new InnerStructure object and sets its 
        /// InnerMost field to the cupplis InnerMostStructure value. It then recurses
        /// again with fieldIdx = 2 and value = the newlt created InnerStructure object value.
        /// This final call create a new RootStructure and sets its Inner field to the 
        /// provided InnerStructure object.
        /// This final RootStructure object is returned to the caller, which returns through
        /// the levels of recursion and ultimately to the original caller.
        /// 
        /// This is tail end recursion so it could be quite simply removed, but the depth of
        /// this is limited by the fact that most structure nesting is quite shallow
        /// and this funtion is not performance sensitive (it gets called once per primitive
        /// field per structure being analyzed.)
        /// </summary>
        /// <param name="returnType">
        /// The type of object to be returned
        /// </param>
        /// <param name="fieldInfoPath">
        /// A list of FieldInfo objects. Each FieldInfo must be a member of the type of the 
        /// next FieldInfo in the list. The last FieldInfo must be a member of ReturnType.
        /// </param>
        /// <param name="fieldIdx">
        /// The current index of the fieldInfoPath being addressed. Should be 0 on the initial call.
        /// </param>
        /// <param name="value"></param>
        /// The value to set the field to.
        /// <returns></returns>
        private static object SetFieldByPath(Type returnType, List<FieldInfo> fieldInfoPath, int fieldIdx, object value, ref bool isNullableHasValueField)
        {
            if (fieldIdx == fieldInfoPath.Count - 1)
            {
                object obj = Activator.CreateInstance(returnType);
                TypedReference tr = __makeref(obj);
                fieldInfoPath[fieldIdx].SetValueDirect(tr, value);
                return obj;
            }
            else
            {
                object parent = null;
                if (fieldInfoPath[fieldIdx + 1].FieldType.IsNullableType())
                {
                    if (fieldInfoPath[fieldIdx].Name == "hasValue")
                    {
                        // to probe this private field, simply return a default object for the underlying type 
                        //(that causes the infrastructure to set hasValue to true)
                        parent = Activator.CreateInstance(fieldInfoPath[fieldIdx + 1].FieldType.GetGenericArguments()[0]);
                        isNullableHasValueField = true;
                    }
                    else if (fieldInfoPath[fieldIdx].Name == "value")
                    {
                        parent = value;
                    }
                    else
                        throw new InvalidOperationException("Unexpected field for Nullable Type");
                }
                else
                {
                    parent = Activator.CreateInstance(fieldInfoPath[fieldIdx + 1].FieldType);
                    TypedReference tr = __makeref(parent);
                    fieldInfoPath[fieldIdx].SetValueDirect(tr, value);
                };
                return SetFieldByPath(returnType, fieldInfoPath, fieldIdx + 1, parent, ref isNullableHasValueField);
            }
        }

        private static void ProbeField(Type type, Array root, List<FieldLayout> fields, byte[] probeBuf, List<FieldInfo> fieldInfoPath, string pathName, List<int> ignoreOffsets)
        {
            var curFieldInfo = fieldInfoPath[0];
            var curFieldType = curFieldInfo.FieldType;
            var curFieldPath = pathName + curFieldType.FullName + ")" + curFieldInfo.Name;
            if (curFieldType.IsValueType)
            {
                if (curFieldType.IsPrimitive)
                {
                    // it is a primitive value type
                    bool isNullableHasValueField = false;
                    // create an empty probe
                    object boxedProbe = SetFieldByPath(type, fieldInfoPath, 0, GetPrimitiveProbeValue(curFieldType), ref isNullableHasValueField);
                    root.SetValue(boxedProbe, 0);
                    // capture the field info
                    var curFieldSize = GetPrimitiveSize(curFieldType);
                    int curFieldOffset = GetFirstNonZeroByteOffset(root, probeBuf, ignoreOffsets);
                    if (isNullableHasValueField)
                        ignoreOffsets.Add(curFieldOffset);
                    fields.Add(new FieldLayout(curFieldType, curFieldPath, curFieldSize, curFieldOffset));
                }
                else
                {
                    // recurse into the struct 
                    fieldInfoPath.Insert(0, null);
                    foreach (var fi in curFieldType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        fieldInfoPath[0] = fi;
                        ProbeField(type, root, fields, probeBuf, fieldInfoPath, curFieldPath + ".(", ignoreOffsets);
                    }
                    fieldInfoPath.RemoveAt(0);
                }
            }
            else
            {
                // it is a reference type of some sort, we do not determine offsets for reference types
                fields.Add(new FieldLayout(curFieldType, curFieldPath, IntPtr.Size, -1));
            }
        }

        private static Dictionary<Type, StructureLayout> _layoutCache = new Dictionary<Type, StructureLayout>();

        public static StructureLayout GetLayout(Type t)
        {
            lock (_layoutCache)
            {
                if (!_layoutCache.TryGetValue(t, out var result))
                {
                    result = new StructureLayout(t);
                    _layoutCache[t] = result;
                }
                return result;
            }
        }

        public const int LargeObjectHeapThresholdSizeInBytes = 85000;

        public readonly Type StructureType;
        /// <summary>
        /// Total size (in bytes) of the structure, including padding 
        /// </summary>
        public readonly int TotalSize;
        /// <summary>
        /// the number of bytes of padding in the structure
        /// </summary>
        public readonly int PaddingSize;
        /// <summary>
        /// the percentage of structure space wasted due to padding
        /// </summary>
        public readonly byte PercentPadding;
        /// <summary>
        /// true if the structure is composed entirely of value type members (contains to reference types at any level of nesting)
        /// false otherwise
        /// </summary>
        public readonly bool IsRawSerializable;
        public readonly int MinimumElementsToForceOnLargeObjectHeap;
        /// <summary>
        /// A flattened collection of all of the primitive or reference fields within the structure, including fields nested within contained complex types
        /// </summary>
        public readonly ReadOnlyCollection<FieldLayout> Fields;
        public readonly byte[] Descriptor;

        public override string ToString()
        {
            var result = new StringBuilder();
            result.Append(this.StructureType.FullName).Append(", size=").Append(this.TotalSize.ToString(CultureInfo.InvariantCulture));
            result.Append(", padding=").Append(this.PaddingSize.ToString(CultureInfo.InvariantCulture));
            result.Append("(").Append(this.PercentPadding.ToString(CultureInfo.InvariantCulture)).Append("%)");
            if (this.IsRawSerializable) result.Append(" IsRawSerializable");
            result.Append("\nSize Offset Field\n");
            foreach(var f in this.Fields)
            {
                result.Append(f.Size.ToString(CultureInfo.InvariantCulture).PadLeft(3));
                result.Append(f.Offset.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append("    ");
                result.Append(f.Path).Append("\n");
            }
            return result.ToString();
        }

        private StructureLayout(Type newType)
        {
            List<FieldLayout> fieldLayouts;
            StringBuilder descriptorBuilder = new StringBuilder(1024);

            this.StructureType = newType;
            Array probe = Array.CreateInstance(newType, 2);
            GetRawBytesElementZero(probe, null, out TotalSize);
            byte[] buf = new byte[TotalSize];
            GetRawBytesElementZero(probe, buf, out TotalSize);

            descriptorBuilder.Append(newType.FullName).Append('\0').Append(TotalSize.ToString("X", CultureInfo.InvariantCulture)).Append('\0');

            fieldLayouts = new List<FieldLayout>();
            if (newType.IsValueType)
            {
                if (newType.IsPrimitive)
                {
                    // it is a primitive type
                    fieldLayouts.Add(new FieldLayout(newType, "(" + newType.FullName + ")", GetPrimitiveSize(newType), 0));
                }
                else
                {
                    // it is a structure of some type, recurse it
                    List<FieldInfo> fieldInfoPath = new List<FieldInfo>(4);
                    List<int> ignoreOffsets = new List<int>();
                    fieldInfoPath.Add(null);
                    foreach (var fi in newType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        fieldInfoPath[0] = fi;
                        ProbeField(newType, probe, fieldLayouts, buf, fieldInfoPath, "(", ignoreOffsets);
                    }
                    // sort in increasing order of byte offset
                    fieldLayouts.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                }
            }
            else
            {
                // it is a reference type 
                fieldLayouts.Add(new FieldLayout(newType, "(" + newType.FullName + ")", PaddingSize, -1));
            }

            foreach (var f in fieldLayouts)
                descriptorBuilder.Append(f.Path).Append('\0').Append(f.Offset.ToString("X", CultureInfo.InvariantCulture)).Append('\0').Append(f.Size.ToString("X", CultureInfo.InvariantCulture)).Append('\0');
            descriptorBuilder.Append('\0');

            Descriptor = Encoding.ASCII.GetBytes(descriptorBuilder.ToString());
            PaddingSize = TotalSize - fieldLayouts.Sum(fld => fld.Size);
            PercentPadding = (byte)((PaddingSize * 100) / TotalSize);
            IsRawSerializable = fieldLayouts.All(fld => fld.FieldType.IsPrimitive);
            MinimumElementsToForceOnLargeObjectHeap = (LargeObjectHeapThresholdSizeInBytes / TotalSize) + 1;

            Fields = new ReadOnlyCollection<FieldLayout>(fieldLayouts);
        }
    }
}