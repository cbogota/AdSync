namespace Collections
{
    /// <summary>
    /// This interface must be implemented on the value to be stored in a SnapshotDictionary
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    public interface IValueSnapshot <TKey>
    {
        /// <summary>
        /// Indicates if the instance has be logically deleted.
        /// </summary>
        bool IsDeleted { get; }
        
        /// <summary>
        /// Unique identifier for this instance
        /// </summary>
        TKey Key { get; }
        
        /// <summary>
        /// The version of this instance. Version numbers may only increase or an exception will be
        /// thrown from the SnapshotDictionary.
        /// </summary>
        long Version { get; }
    }
}
