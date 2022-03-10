using System;

namespace Collections
{
    public interface IGetMemoryStats
    {
        MemoryStats GetMemoryStats();
    }

    public class MemoryStats
    {
        public StructureLayout ElementLayout { get; internal set; }
        public int BytesAllocated { get; internal set; }
        public byte PercentFree { get; internal set; }

        public MemoryStats(Array ary, int elementsInUse)
        {
            this.ElementLayout = ary.GetType().GetElementType().GetStructureLayout();
            this.BytesAllocated = ary.Length * ElementLayout.TotalSize;
            this.PercentFree = (byte)((100.0*(ary.Length-elementsInUse))/ary.Length) ;
        }

        public MemoryStats(int bytesAllocated, byte percentFree)
        {
            this.BytesAllocated = bytesAllocated;
            this.PercentFree = percentFree;
        }

        internal MemoryStats()
        {
        }
    }
}
