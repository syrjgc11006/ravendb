using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Sparrow;
using Sparrow.Server;
using Sparrow.Threading;
using Voron.Data.BTrees;
using Voron.Data.Fixed;
using Voron.Data.RawData;
using Voron.Exceptions;
using Voron.Impl;
using Voron.Impl.Paging;
using Constants = Voron.Global.Constants;

namespace Voron.Data.Tables
{
    public unsafe class Table : IDisposable
    {
        private readonly bool _forGlobalReadsOnly;
        private readonly TableSchema _schema;
        internal readonly Transaction _tx;
        private readonly Tree _tableTree;

        private ActiveRawDataSmallSection _activeDataSmallSection;
        private FixedSizeTree _inactiveSections;
        private FixedSizeTree _activeCandidateSection;

        private Dictionary<long, ByteString> _cachedDecompressedBuffersByStorageId;
        private readonly Dictionary<Slice, Tree> _treesBySliceCache = new Dictionary<Slice, Tree>(SliceStructComparer.Instance);
        private readonly Dictionary<Slice, Dictionary<Slice, FixedSizeTree>> _fixedSizeTreeCache = new Dictionary<Slice, Dictionary<Slice, FixedSizeTree>>(SliceStructComparer.Instance);

        public readonly Slice Name;
        private readonly byte _tableType;

        public long NumberOfEntries { get; private set; }

        private long _overflowPageCount;
        private readonly NewPageAllocator _tablePageAllocator;
        private readonly NewPageAllocator _globalPageAllocator;

        public FixedSizeTree InactiveSections => _inactiveSections ??= GetFixedSizeTree(_tableTree, TableSchema.InactiveSectionSlice, 0, isGlobal: false, isIndexTree: true);

        public FixedSizeTree ActiveCandidateSection => _activeCandidateSection ??= GetFixedSizeTree(_tableTree, TableSchema.ActiveCandidateSectionSlice, 0, isGlobal: false, isIndexTree: true);

        public ActiveRawDataSmallSection ActiveDataSmallSection
        {
            get
            {
                if (_activeDataSmallSection == null)
                {
                    var readResult = _tableTree.Read(TableSchema.ActiveSectionSlice);
                    if (readResult == null)
                        throw new VoronErrorException($"Could not find active sections for {Name}");

                    long pageNumber = readResult.Reader.ReadLittleEndianInt64();

                    _activeDataSmallSection = new ActiveRawDataSmallSection(_tx, pageNumber);
                    _activeDataSmallSection.DataMoved += OnDataMoved;
                }
                return _activeDataSmallSection;
            }
        }

        private void OnDataMoved(long previousId, long newId, byte* data, int size)
        {
#if DEBUG
            if (IsOwned(previousId) == false || IsOwned(newId) == false)
            {
                VoronUnrecoverableErrorException.Raise(_tx.LowLevelTransaction,
                    $"Cannot move data in section because the old ({previousId}) or new ({newId}) belongs to a different owner");

            }
#endif
            var tvr = new TableValueReader(data, size);
            DeleteValueFromIndex(previousId, ref tvr);
            InsertIndexValuesFor(newId, ref tvr);
        }

        /// <summary>
        /// Tables should not be loaded using this function. The proper way to
        /// do this is to use the OpenTable method in the Transaction class.
        /// Using this constructor WILL NOT register the Table for commit in
        /// the Transaction, and hence changes WILL NOT be committed.
        /// </summary>
        public Table(TableSchema schema, Slice name, Transaction tx, Tree tableTree, byte tableType, bool doSchemaValidation = false)
        {
            Name = name;

            _schema = schema;
            _tx = tx;
            _tableType = tableType;

            _tableTree = tableTree;
            if (_tableTree == null)
                throw new ArgumentNullException(nameof(tableTree), "Cannot open table " + Name);

            var stats = (TableSchemaStats*)_tableTree.DirectRead(TableSchema.StatsSlice);
            if (stats == null)
                throw new InvalidDataException($"Cannot find stats value for table {name}");

            NumberOfEntries = stats->NumberOfEntries;
            _overflowPageCount = stats->OverflowPageCount;
            _tablePageAllocator = new NewPageAllocator(_tx.LowLevelTransaction, _tableTree);
            _globalPageAllocator = new NewPageAllocator(_tx.LowLevelTransaction, _tx.LowLevelTransaction.RootObjects);

            if (doSchemaValidation)
            {
                byte* writtenSchemaData = _tableTree.DirectRead(TableSchema.SchemasSlice);
                int writtenSchemaDataSize = _tableTree.GetDataSize(TableSchema.SchemasSlice);
                var actualSchema = TableSchema.ReadFrom(tx.Allocator, writtenSchemaData, writtenSchemaDataSize);
                actualSchema.Validate(schema);
            }
        }

        /// <summary>
        /// this overload is meant to be used for global reads only, when want to use
        /// a global index to find data, without touching the actual table.
        /// </summary>
        public Table(TableSchema schema, Transaction tx)
        {
            _schema = schema;
            _tx = tx;
            _forGlobalReadsOnly = true;
            _tableType = 0;
        }

        public bool ReadByKey(Slice key, out TableValueReader reader)
        {
            if (TryFindIdFromPrimaryKey(key, out long id) == false)
            {
                reader = default(TableValueReader);
                return false;
            }

            var rawData = DirectRead(id, out int size);
            reader = new TableValueReader(id, rawData, size);
            return true;
        }

        public bool Read(ByteStringContext context, TableSchema.FixedSizeSchemaIndexDef index, long value, out TableValueReader reader)
        {
            var fst = GetFixedSizeTree(index);

            using (fst.Read(value, out var read))
            {
                if (read.HasValue == false)
                {
                    reader = default(TableValueReader);
                    return false;
                }

                var storageId = read.CreateReader().ReadLittleEndianInt64();

                ReadById(storageId, out reader);
                return true;
            }
        }

        public bool VerifyKeyExists(Slice key)
        {
            var pkTree = GetTree(_schema.Key);
            var readResult = pkTree?.Read(key);
            return readResult != null;
        }

        private bool TryFindIdFromPrimaryKey(Slice key, out long id)
        {
            id = -1;
            var pkTree = GetTree(_schema.Key);
            var readResult = pkTree?.Read(key);
            if (readResult == null)
                return false;

            id = readResult.Reader.ReadLittleEndianInt64();
            return true;
        }

        public byte* DirectRead(long id, out int size)
        {
            var posInPage = id % Constants.Storage.PageSize;
            if (posInPage == 0) // large
            {
                var page = _tx.LowLevelTransaction.GetPage(id / Constants.Storage.PageSize);
                size = page.OverflowSize;

                byte* ptr = page.Pointer + PageHeader.SizeOf;

                if ((page.Flags & PageFlags.Compressed) == PageFlags.Compressed)
                    return DirectReadDecompress(id, ptr, ref size);
                
                return ptr;
            }

            // here we rely on the fact that RawDataSmallSection can
            // read any RawDataSmallSection piece of data, not just something that
            // it exists in its own section, but anything from other sections as well
            byte* directRead = RawDataSection.DirectRead(_tx.LowLevelTransaction, id, out size, out bool compressed);
            if (compressed == false)
                return directRead;
            
            return DirectReadDecompress(id, directRead, ref size);
        }

        private byte* DirectReadDecompress(long id,  byte* directRead, ref int size)
        {
            _cachedDecompressedBuffersByStorageId ??= new Dictionary<long, ByteString>();

            if (_cachedDecompressedBuffersByStorageId.TryGetValue(id, out var t))
            {
                size = t.Length;
                return t.Ptr;
            }

            //TODO: For encrypted databases, we need to allocate on locked memory

            var data = new ReadOnlySpan<byte>(directRead, size);
            var dictionary = GetCompressionDictionaryFor(id, ref data);
            var _ = // we explicitly do *not* dispose the buffer, it lives as long as the tx
                Decompress(_tx, data, dictionary, out var buffer);

            _cachedDecompressedBuffersByStorageId[id] = buffer;

            size = buffer.Length;
            return buffer.Ptr;
        }

        internal static ByteStringContext<ByteStringMemoryCache>.InternalScope Decompress(
            Transaction tx,
            ReadOnlySpan<byte> data, ZstdLib.CompressionDictionary dictionary,
            out ByteString buffer)
        {
            int decompressedSize = ZstdLib.GetDecompressedSize(data);
            var internalScope = tx.Allocator.Allocate(decompressedSize, out buffer);
            var actualSize = ZstdLib.Decompress(data, buffer.ToSpan(), dictionary);
            if (actualSize != decompressedSize)
                throw new InvalidDataException($"Got decompressed size {actualSize} but expected {decompressedSize}");
            return internalScope;
        }

        public int GetAllocatedSize(long id)
        {
            var posInPage = id % Constants.Storage.PageSize;
            if (posInPage == 0) // large
            {
                var page = _tx.LowLevelTransaction.GetPage(id / Constants.Storage.PageSize);

                var allocated = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(page.OverflowSize);

                return allocated * Constants.Storage.PageSize;
            }

            // here we rely on the fact that RawDataSmallSection can
            // read any RawDataSmallSection piece of data, not just something that
            // it exists in its own section, but anything from other sections as well
            return RawDataSection.GetRawDataEntrySizeFor(_tx.LowLevelTransaction, id)->AllocatedSize;
        }

        public long Update(long id, TableValueBuilder builder, bool forceUpdate = false)
        {
            AssertWritableTable();

            if (_schema.Compressed)
            {
                builder.TryCompression(GetAppropriateCompressionDictionaryToCompress(id));
            }

            int size = builder.Size;

            // We must read before we call TryWriteDirect, because it will modify the size
            var oldData = DirectRead(id, out var oldDataSize);
            if (_schema.Compressed)
            {
                _cachedDecompressedBuffersByStorageId?.Remove(id);
            }

            // first, try to fit in place, either in small or large sections
            var prevIsSmall = id % Constants.Storage.PageSize != 0;
            if (size + sizeof(RawDataSection.RawDataEntrySizes) < RawDataSection.MaxItemSize)
            {
                AssertNoReferenceToOldData(builder, oldData, oldDataSize);

                if (prevIsSmall && ActiveDataSmallSection.TryWriteDirect(id, size, builder.Compressed, out byte* pos))
                {
                    var tvr = new TableValueReader(oldData, oldDataSize);
                    UpdateValuesFromIndex(id,
                        ref tvr,
                        builder,
                        forceUpdate);

                    builder.CopyTo(pos);

                    if (builder.Compressed)
                    {
                        ActiveDataSmallSection.SetCompressionRate(id, builder.Compression.CompressionRatio);
                    }
                    
                    return id;
                }
            }
            else if (prevIsSmall == false)
            {
                size = builder.SizeLarge;
                var pageNumber = id / Constants.Storage.PageSize;
                var page = _tx.LowLevelTransaction.GetPage(pageNumber);
                var existingNumberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(page.OverflowSize);
                var newNumberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(size);

                if (existingNumberOfPages == newNumberOfPages)
                {
                    page = _tx.LowLevelTransaction.ModifyPage(pageNumber);

                    var pos = page.Pointer + PageHeader.SizeOf;

                    AssertNoReferenceToOldData(builder, pos, size);

                    var tvr = new TableValueReader(oldData, oldDataSize);
                    UpdateValuesFromIndex(id, ref tvr, builder, forceUpdate);

                    // MemoryCopy into final position.
                    page.OverflowSize = size;

                    builder.CopyToLarge(pos);

                    return id;
                }
            }

            // can't fit in place, will just delete & insert instead
            Delete(id);
            return Insert(builder);
        }



        private ZstdLib.CompressionDictionary GetAppropriateCompressionDictionaryToCompress(long id)
        {
            using var _ =
                id % Constants.Storage.PageSize == 0
                    ? ActiveDataSmallSection.CurrentCompressionDictionaryHash(out var hash)
                    : ActiveRawDataSmallSection.CompressionDictionaryHashFor(_tx.LowLevelTransaction, id, out hash);
            return _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder.GetCompressionDictionaryFor(_tx, hash);

        }


        private ZstdLib.CompressionDictionary GetCompressionDictionaryFor(long id, ref ReadOnlySpan<byte> data)
        {
            fixed (byte* dataPtr = data)
            {
                if (id % Constants.Storage.PageSize == 0)
                {
                    using var _ = Slice.External(_tx.Allocator, dataPtr, 32, out var hash);
                    data = data.Slice(32, data.Length - 32);
                    return _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder.GetCompressionDictionaryFor(_tx, hash);
                }
                else
                {
                    using var _ = ActiveRawDataSmallSection.CompressionDictionaryHashFor(_tx.LowLevelTransaction ,id, out var hash);
                    return _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder.GetCompressionDictionaryFor(_tx, hash);
                }
            }
        }

        [Conditional("DEBUG")]
        private void AssertNoReferenceToThisPage(TableValueBuilder builder, long id)
        {
            if (builder == null)
                return;

            var pageNumber = id / Constants.Storage.PageSize;
            var page = _tx.LowLevelTransaction.GetPage(pageNumber);
            for (int i = 0; i < builder.Count; i++)
            {
                Slice slice;
                using (builder.SliceFromLocation(_tx.Allocator, i, out slice))
                {
                    if (slice.Content.Ptr >= page.Pointer &&
                        slice.Content.Ptr < page.Pointer + Constants.Storage.PageSize)
                    {
                        throw new InvalidOperationException(
                            "Invalid attempt to insert data with the source equals to the range we are modifying. This is not permitted since it can cause data corruption when table defrag happens");
                    }
                }
            }
        }

        [Conditional("DEBUG")]
        private void AssertNoReferenceToOldData(TableValueBuilder builder, byte* oldData, int oldDataSize)
        {
            for (int i = 0; i < builder.Count; i++)
            {
                using (builder.SliceFromLocation(_tx.Allocator, i, out Slice slice))
                {
                    if (slice.Content.Ptr >= oldData &&
                        slice.Content.Ptr < oldData + oldDataSize)
                    {
                        throw new InvalidOperationException(
                            "Invalid attempt to update data with the source equals to the range we are modifying. This is not permitted since it can cause data corruption when table defrag happens. You probably should clone your data.");
                    }
                }
            }
        }

        public bool IsOwned(long id)
        {
            var posInPage = id % Constants.Storage.PageSize;

            if (posInPage != 0)
                return ActiveDataSmallSection.IsOwned(id);

            // large value

            var page = _tx.LowLevelTransaction.GetPage(id / Constants.Storage.PageSize);
            var header = (RawDataOverflowPageHeader*)page.Pointer;

            return header->SectionOwnerHash == ActiveDataSmallSection.SectionOwnerHash;
        }

        public void Delete(long id)
        {
            AssertWritableTable();
            
            var ptr = DirectRead(id, out int size);

            if (_schema.Compressed) 
                _cachedDecompressedBuffersByStorageId?.Remove(id);

            var tvr = new TableValueReader(ptr, size);
            DeleteValueFromIndex(id, ref tvr);

            var largeValue = (id % Constants.Storage.PageSize) == 0;
            if (largeValue)
            {
                var page = _tx.LowLevelTransaction.GetPage(id / Constants.Storage.PageSize);
                var numberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(page.OverflowSize);
                _overflowPageCount -= numberOfPages;

                for (var i = 0; i < numberOfPages; i++)
                {
                    _tx.LowLevelTransaction.FreePage(page.PageNumber + i);
                }
            }

            NumberOfEntries--;

            using (_tableTree.DirectAdd(TableSchema.StatsSlice, sizeof(TableSchemaStats), out byte* updatePtr))
            {
                var stats = (TableSchemaStats*)updatePtr;

                stats->NumberOfEntries = NumberOfEntries;
                stats->OverflowPageCount = _overflowPageCount;
            }

            if (largeValue)
                return;

            var density = ActiveDataSmallSection.Free(id);
            if (ActiveDataSmallSection.Contains(id) || density > 0.5)
                return;

            var sectionPageNumber = RawDataSection.GetSectionPageNumber(_tx.LowLevelTransaction,id);
            if (density > 0.15)
            {
                ActiveCandidateSection.Add(sectionPageNumber);
                return;
            }

            // move all the data to the current active section (maybe creating a new one
            // if this is busy)

            // if this is in the active candidate list, remove it so it cannot be reused if the current
            // active is full and need a new one
            ActiveCandidateSection.Delete(sectionPageNumber);
            // need to remove it from the inactive tracking because it is going to be freed in a bit
            InactiveSections.Delete(sectionPageNumber);

            ZstdLib.CompressionDictionary currentCompressionDictionary;

            using (ActiveDataSmallSection.CurrentCompressionDictionaryHash(out var hash))
            {
                currentCompressionDictionary = _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder
                    .GetCompressionDictionaryFor(_tx, hash);
            }

            var idsInSection = ActiveDataSmallSection.GetAllIdsInSectionContaining(id);
            var tmpBuilder = new TableValueBuilder();
            tmpBuilder.Compression.SetCurrentTransaction(_tx);
            foreach (var idToMove in idsInSection)
            {
                var pos = ActiveDataSmallSection.DirectRead(idToMove, out int itemSize, out bool compressed);

                var actualPos = pos;
                var actualSize = itemSize;
                if (compressed)
                {
                    var data = new ReadOnlySpan<byte>(pos, itemSize);
                    var compressionDictionary = GetCompressionDictionaryFor(idToMove, ref data);
                    tmpBuilder.Compression.RawScope = Decompress(_tx, data, compressionDictionary, out tmpBuilder.Compression.RawBuffer);

                    actualSize = tmpBuilder.Compression.RawBuffer.Length;
                    actualPos = tmpBuilder.Compression.RawBuffer.Ptr;

                    if (compressionDictionary != currentCompressionDictionary)
                    {
                        // different dictionaries need to compress again
                        tmpBuilder.Compression.CompressedScope = _tx.Allocator.Allocate(ZstdLib.GetMaxCompression(actualSize), 
                            out tmpBuilder.Compression.CompressedBuffer);
                        int newlyCompressedSize = ZstdLib.Compress(tmpBuilder.Compression.RawBuffer.ToReadOnlySpan(),
                            tmpBuilder.Compression.CompressedBuffer.ToSpan(), currentCompressionDictionary);
                        if (newlyCompressedSize > actualSize)
                        {
                            // couldn't compress well enough, use raw data
                            compressed = false;
                            tmpBuilder.Compression.DiscardCompressedData();
                            pos = actualPos;
                            itemSize = actualSize;
                        }
                        else
                        {
                            tmpBuilder.Compression.SetDictionary(currentCompressionDictionary);
                            pos = tmpBuilder.Compression.CompressedBuffer.Ptr;
                            itemSize = newlyCompressedSize;
                        }
                    }
                }

                long newId;
                if (itemSize + sizeof(RawDataSection.RawDataEntrySizes) < RawDataSection.MaxItemSize)
                {
                    if (ActiveDataSmallSection.TryAllocate(itemSize, out newId) == false)
                    {
                        newId = AllocateFromAnotherSection(itemSize, tmpBuilder.Compression, currentCompressionDictionary);
                        if (compressed && tmpBuilder.Compressed)
                        {
                            pos = tmpBuilder.Compression.CompressedBuffer.Ptr;
                            itemSize = tmpBuilder.Compression.CompressedBuffer.Length;
                        }
                        using (ActiveDataSmallSection.CurrentCompressionDictionaryHash(out var hash))
                        {
                            currentCompressionDictionary = _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder
                                .GetCompressionDictionaryFor(_tx, hash);
                        }
                    }

                    if (ActiveDataSmallSection.TryWriteDirect(newId, itemSize, compressed, out byte* writePos) == false)
                        throw new VoronErrorException($"Cannot write to newly allocated size in {Name} during delete");

                    Memory.Copy(writePos, pos, itemSize);
                }
                else // after re-compression, we *can't* fit it into a small data section :-(
                {
                    Debug.Assert(compressed, "We should never hit this code path unless we re-compressed");
                    // so we'll put it a big section, instead
                    var page = AllocatePageForLargeValue(tmpBuilder.SizeLarge, tmpBuilder.Compressed);

                    newId = page.PageNumber * Constants.Storage.PageSize;

                    tmpBuilder.CopyToLarge(page.DataPointer);

                }

                OnDataMoved(idToMove, newId, actualPos, actualSize);

                tmpBuilder.Compression.CompressedScope.Dispose();
                tmpBuilder.Compression.RawScope.Dispose();
            }
            tmpBuilder.Reset();

            ActiveDataSmallSection.DeleteSection(sectionPageNumber);
        }

        private void ThrowInvalidAttemptToRemoveValueFromIndexAndNotFindingIt(long id, Slice indexDefName)
        {
            throw new VoronErrorException(
                $"Invalid index {indexDefName} on {Name}, attempted to delete value but the value from {id} wasn\'t in the index");
        }

        private void DeleteValueFromIndex(long id, ref TableValueReader value)
        {
            AssertWritableTable();

            if (_schema.Key != null)
            {
                using (_schema.Key.GetSlice(_tx.Allocator, ref value, out Slice keySlice))
                {
                    var pkTree = GetTree(_schema.Key);
                    pkTree.Delete(keySlice);
                }
            }

            foreach (var indexDef in _schema.Indexes.Values)
            {
                // For now we wont create secondary indexes on Compact trees.
                var indexTree = GetTree(indexDef);
                using (indexDef.GetSlice(_tx.Allocator, ref value, out Slice val))
                {
                    var fst = GetFixedSizeTree(indexTree, val.Clone(_tx.Allocator), 0, indexDef.IsGlobal);
                    if (fst.Delete(id).NumberOfEntriesDeleted == 0)
                    {
                        ThrowInvalidAttemptToRemoveValueFromIndexAndNotFindingIt(id, indexDef.Name);
                    }
                }
            }

            foreach (var indexDef in _schema.FixedSizeIndexes.Values)
            {
                var index = GetFixedSizeTree(indexDef);
                var key = indexDef.GetValue(ref value);
                if (index.Delete(key).NumberOfEntriesDeleted == 0)
                {
                    ThrowInvalidAttemptToRemoveValueFromIndexAndNotFindingIt(id, indexDef.Name);
                }
            }
        }

        /// <summary>
        /// Resource intensive function that validates fixed size trees in the table's schema
        /// </summary>
        public void AssertValidFixedSizeTrees()
        {
            foreach (var fsi in _schema.FixedSizeIndexes)
            {
                var fixedSizeTree = GetFixedSizeTree(fsi.Value);
                fixedSizeTree.ValidateTree_Forced();
            }
        }

        public long Insert(TableValueBuilder builder)
        {
            AssertWritableTable();
            var compressionDictionary = default(ZstdLib.CompressionDictionary);
            if (_schema.Compressed)
            {
                using var _ = ActiveDataSmallSection.CurrentCompressionDictionaryHash(out var hash);
                compressionDictionary = _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder.GetCompressionDictionaryFor(_tx, hash);
                builder.TryCompression(compressionDictionary);
            }

            byte* pos;
            long id;

            if (builder.Size + sizeof(RawDataSection.RawDataEntrySizes) < RawDataSection.MaxItemSize)
            {
                if (ActiveDataSmallSection.TryAllocate(builder.Size, out id) == false)
                {
                    id = AllocateFromAnotherSection(builder.Size, builder.Compression, compressionDictionary);
                }
                AssertNoReferenceToThisPage(builder, id);

                if (ActiveDataSmallSection.TryWriteDirect(id, builder.Size, builder.Compressed, out pos) == false) 
                    ThrowBadWriter(builder.Size);

                // Memory Copy into final position.
                builder.CopyTo(pos);
                if (builder.Compressed)
                {
                    ActiveDataSmallSection.SetCompressionRate(builder.Compression.CompressionRatio);
                }
            }
            else
            {
                var page = AllocatePageForLargeValue(builder.SizeLarge, builder.Compressed);

                pos = page.DataPointer;

                builder.CopyToLarge(pos);

                id = page.PageNumber * Constants.Storage.PageSize;
            }

            var tvr = builder.CreateReader(pos);
            InsertIndexValuesFor(id, ref tvr);

            NumberOfEntries++;

            using (_tableTree.DirectAdd(TableSchema.StatsSlice, sizeof(TableSchemaStats), out byte* ptr))
            {
                var stats = (TableSchemaStats*)ptr;

                stats->NumberOfEntries = NumberOfEntries;
                stats->OverflowPageCount = _overflowPageCount;
            }

            return id;
        }

        private Page AllocatePageForLargeValue(int size, bool compressed)
        {
            var numberOfOverflowPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(size);
            var page = _tx.LowLevelTransaction.AllocatePage(numberOfOverflowPages);
            _overflowPageCount += numberOfOverflowPages;

            page.Flags = PageFlags.Overflow | PageFlags.RawData;
            if (compressed)
                page.Flags |= PageFlags.Compressed;

            page.OverflowSize = size;

            ((RawDataOverflowPageHeader*)page.Pointer)->SectionOwnerHash = ActiveDataSmallSection.SectionOwnerHash;
            ((RawDataOverflowPageHeader*)page.Pointer)->TableType = _tableType;
            return page;
        }

        private long AllocateFromAnotherSection(int itemSize, TableValueCompressor compressor, ZstdLib.CompressionDictionary compressionDictionary)
        {
            InactiveSections.Add(_activeDataSmallSection.PageNumber);

            var previousActiveSection = ActiveDataSmallSection;

            if (TryFindMatchFromCandidateSections(itemSize, compressionDictionary?.Hash, out long id) == false)
            {
                Slice newSectionDictionaryHash = default;

                // need to switch, may have to re-compress at this point
                if (compressionDictionary != null)
                {
                    newSectionDictionaryHash = compressionDictionary.Hash;

                    // We'll replace the dictionary if it is unable to provide us with good compression ratio
                    // we give it +10 ratio to allow some slippage between training. Even after violating the expected
                    // compression ration, we have to check for compression outliers (a single input that doesn't compress well)
                    // this is handled inside ShouldReplaceDictionary and ensure that new dictionaries are at least 10%
                    // better than the previous one. We prefer to use less dictionaries, even if compression
                    // rate can be slightly improved.
                    if (previousActiveSection.MinCompressionRatio + 10 > compressionDictionary.ExpectedCompressionRatio)
                    {
                        // this will check if we can create a new dictionary that would do better for the current item
                        // than the previous dictionary, creating a new hash for it
                        MaybeTrainCompressionDictionary(previousActiveSection, compressionDictionary, compressor, ref newSectionDictionaryHash);
                        itemSize = compressor.Size;
                    }
                }

                CreateNewActiveSection(newSectionDictionaryHash);

                if (ActiveDataSmallSection.TryAllocate(itemSize, out id) == false)
                {
                    ThrowBadAllocation(compressor.Size);
                }
            }

            return id;
        }

        private void ThrowBadWriter(int size)
        {
            throw new VoronErrorException(
                $"After successfully allocating {size:#,#;;0} bytes, failed to write them on {Name}");
        }

        private void ThrowBadAllocation(int size)
        {
            throw new VoronErrorException(
                $"After changing active sections, failed to allocate {size:#,#;;0} bytes on {Name}");
        }

        public class CompressionDictionariesHolder : IDisposable
        {
            private readonly ByteStringContext _compressionDictionariesSliceContext = new ByteStringContext(SharedMultipleUseFlag.None);
            private readonly ConcurrentDictionary<Slice, ZstdLib.CompressionDictionary> _compressionDictionaries = new ConcurrentDictionary<Slice, ZstdLib.CompressionDictionary>(SliceComparer.Instance);

            public ZstdLib.CompressionDictionary GetCompressionDictionaryFor(Transaction tx, Slice hash)
            {
                if (_compressionDictionaries.TryGetValue(hash, out var current)) 
                    return current;
                
                lock (_compressionDictionariesSliceContext)
                {
                    if (_compressionDictionaries.TryGetValue(hash, out current)) 
                        return current;
                    
                    Slice clonedHash = hash.Clone(_compressionDictionariesSliceContext, ByteStringType.Immutable);

                    current = CreateCompressionDictionary(tx, clonedHash);
                    _compressionDictionaries.TryAdd(clonedHash, current);
                }

                return current;
            }
            
            private ZstdLib.CompressionDictionary CreateCompressionDictionary(Transaction tx, Slice hash)
            {
                Debug.Assert(hash.Size == 32);
                var dictionariesTree = tx.ReadTree(TableSchema.DictionariesSlice);
                var readResult = dictionariesTree?.Read(hash);
                if (readResult == null)
                {
                    // we may be checking an empty section, so let's return an empty
                    // dictionary there
                    long* l = (long*)hash.Content.Ptr;
                    if (l[0] == 0 && l[1] == 0 && l[2] == 0 && l[3] == 0)
                    {
                        return new ZstdLib.CompressionDictionary(hash, null, 0, 0)
                        {
                            ExpectedCompressionRatio = 101
                        };
                    }

                    string hashStr = Convert.ToBase64String(hash.AsSpan());
                    throw new InvalidOperationException("Trying to read dictionary: " + hashStr + " but it was not found!");
                }

                var info = (CompressionDictionaryInfo*)readResult.Reader.Base;
                var dic = new ZstdLib.CompressionDictionary(
                        hash,
                    readResult.Reader.Base + sizeof(CompressionDictionaryInfo),
                    readResult.Reader.Length - sizeof(CompressionDictionaryInfo), 3)
                {
                    ExpectedCompressionRatio = info->ExpectedCompressionRatio
                };
                return dic;
            }

            public void Dispose()
            {
                GC.SuppressFinalize(this);
                lock (_compressionDictionariesSliceContext)
                {
                    foreach (var kvp in _compressionDictionaries)
                    {
                        Slice kvpKey = kvp.Key;
                        _compressionDictionariesSliceContext.Release(ref kvpKey.Content);
                        kvp.Value.Dispose();
                    }
                }
            }

            ~CompressionDictionariesHolder()
            {
                Dispose();
            }
        }



        private void UpdateValuesFromIndex(long id, ref TableValueReader oldVer, TableValueBuilder newVer, bool forceUpdate)
        {
            AssertWritableTable();

            using (Slice.External(_tx.Allocator, (byte*)&id, sizeof(long), out Slice idAsSlice))
            {
                if (_schema.Key != null)
                {
                    using (_schema.Key.GetSlice(_tx.Allocator, ref oldVer, out Slice oldKeySlice))
                    using (_schema.Key.GetSlice(_tx.Allocator, newVer, out Slice newKeySlice))
                    {
                        if (SliceComparer.AreEqual(oldKeySlice, newKeySlice) == false ||
                            forceUpdate)
                        {
                            var pkTree = GetTree(_schema.Key);
                            pkTree.Delete(oldKeySlice);
                            pkTree.Add(newKeySlice, idAsSlice);
                        }
                    }
                }

                foreach (var indexDef in _schema.Indexes.Values)
                {
                    // For now we wont create secondary indexes on Compact trees.
                    using (indexDef.GetSlice(_tx.Allocator, ref oldVer, out Slice oldVal))
                    using (indexDef.GetSlice(_tx.Allocator, newVer, out Slice newVal))
                    {
                        if (SliceComparer.AreEqual(oldVal, newVal) == false ||
                            forceUpdate)
                        {
                            var indexTree = GetTree(indexDef);
                            var fst = GetFixedSizeTree(indexTree, oldVal.Clone(_tx.Allocator), 0, indexDef.IsGlobal);
                            fst.Delete(id);
                            fst = GetFixedSizeTree(indexTree, newVal.Clone(_tx.Allocator), 0, indexDef.IsGlobal);
                            fst.Add(id);
                        }
                    }
                }

                foreach (var indexDef in _schema.FixedSizeIndexes.Values)
                {
                    var index = GetFixedSizeTree(indexDef);
                    var oldKey = indexDef.GetValue(ref oldVer);
                    var newKey = indexDef.GetValue(_tx.Allocator, newVer);

                    if (oldKey != newKey || forceUpdate)
                    {
                        index.Delete(oldKey);
                        if (index.Add(newKey, idAsSlice) == false)
                            ThrowInvalidDuplicateFixedSizeTreeKey(newKey, indexDef);
                    }
                }
            }
        }

        internal long Insert(ref TableValueReader reader)
        {
            AssertWritableTable();

            using var __ = Allocate(out var builder);

            ByteStringContext<ByteStringMemoryCache>.ExternalScope rawCompressBufferScore = default;

            byte* dataPtr = reader.Pointer;
            int dataSize = reader.Size;
            bool compressed = false;
            ZstdLib.CompressionDictionary compressionDic = null;

            if (_schema.Compressed)
            {
                rawCompressBufferScore = _tx.Allocator.FromPtr(reader.Pointer, reader.Size,
                    ByteStringType.Immutable, out builder.Compression.RawBuffer);

                using var _ = ActiveDataSmallSection.CurrentCompressionDictionaryHash(out var hash);
                compressionDic = _tx.LowLevelTransaction.Environment.CompressionDictionariesHolder.GetCompressionDictionaryFor(_tx, hash);

                builder.Compression.Prepare(reader.Size);
                if (builder.Compression.TryCompression(compressionDic))
                {
                    dataPtr = builder.Compression.CompressedBuffer.Ptr;
                    dataSize = builder.Compression.CompressedBuffer.Length;
                    compressed = true;
                }
            }
            
            long id;
            if (dataSize + sizeof(RawDataSection.RawDataEntrySizes) < RawDataSection.MaxItemSize)
            {
                bool insertSmall = true;
                if (ActiveDataSmallSection.TryAllocate(dataSize, out id) == false)
                {
                    id = AllocateFromAnotherSection(dataSize, builder.Compression, compressionDic);
                    if (_schema.Compressed)
                    {
                        dataSize = builder.Size;
                        compressed = builder.Compressed;
                        if (dataSize + sizeof(RawDataSection.RawDataEntrySizes) >= RawDataSection.MaxItemSize)
                        {
                            insertSmall = false;
                            InsertLargeValue();
                        }
                    }
                }

                if (insertSmall)
                {
                    if (ActiveDataSmallSection.TryWriteDirect(id, dataSize, compressed, out var pos) == false)
                        ThrowBadWriter(dataSize);

                    if (compressed)
                    {
                        builder.CopyTo(pos);
                    }
                    else
                    {
                        Memory.Copy(pos, reader.Pointer, dataSize);
                    }
                }
            }
            else
            {
               InsertLargeValue();
            }

            void InsertLargeValue()
            {
                var numberOfOverflowPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(dataSize);
                var page = _tx.LowLevelTransaction.AllocatePage(numberOfOverflowPages);
                _overflowPageCount += numberOfOverflowPages;

                page.Flags = PageFlags.Overflow | PageFlags.RawData;
                page.OverflowSize = dataSize;

                ((RawDataOverflowPageHeader*)page.Pointer)->SectionOwnerHash = ActiveDataSmallSection.SectionOwnerHash;
                ((RawDataOverflowPageHeader*)page.Pointer)->TableType = _tableType;

                if (compressed)
                {
                    page.Flags |= PageFlags.Compressed;
                    page.OverflowSize = builder.Compression.SizeLarge;
                    builder.Compression.CopyToLarge(page.DataPointer);
                }
                else
                {
                    Memory.Copy(page.DataPointer, dataPtr, dataSize);

                }

                id = page.PageNumber * Constants.Storage.PageSize;
            }

            InsertIndexValuesFor(id, ref reader);

            NumberOfEntries++;

            using (_tableTree.DirectAdd(TableSchema.StatsSlice, sizeof(TableSchemaStats), out byte* ptr))
            {
                var stats = (TableSchemaStats*)ptr;

                stats->NumberOfEntries = NumberOfEntries;
                stats->OverflowPageCount = _overflowPageCount;
            }

            rawCompressBufferScore.Dispose();
            return id;
        }

        private void InsertIndexValuesFor(long id, ref TableValueReader value)
        {
            AssertWritableTable();

            var pk = _schema.Key;
            using (Slice.External(_tx.Allocator, (byte*)&id, sizeof(long), out Slice idAsSlice))
            {
                if (pk != null)
                {
                    using (pk.GetSlice(_tx.Allocator, ref value, out Slice pkVal))
                    {
                        var pkIndex = GetTree(pk);

                        using (pkIndex.DirectAdd(pkVal, idAsSlice.Size, TreeNodeFlags.Data | TreeNodeFlags.NewOnly, out var ptr))
                        {
                            idAsSlice.CopyTo(ptr);
                        }
                    }
                }

                foreach (var indexDef in _schema.Indexes.Values)
                {
                    // For now we wont create secondary indexes on Compact trees.
                    using (indexDef.GetSlice(_tx.Allocator, ref value, out Slice val))
                    {
                        var indexTree = GetTree(indexDef);
                        var index = GetFixedSizeTree(indexTree, val, 0, indexDef.IsGlobal);
                        index.Add(id);
                    }
                }

                foreach (var indexDef in _schema.FixedSizeIndexes.Values)
                {
                    var index = GetFixedSizeTree(indexDef);
                    var key = indexDef.GetValue(ref value);
                    if (index.Add(key, idAsSlice) == false)
                        ThrowInvalidDuplicateFixedSizeTreeKey(key, indexDef);
                }
            }
        }

        private void ThrowInvalidDuplicateFixedSizeTreeKey(long key, TableSchema.FixedSizeSchemaIndexDef indexDef)
        {
            throw new VoronErrorException("Attempt to add duplicate value " + key + " to " + indexDef.Name + " on " + Name);
        }

        public FixedSizeTree GetFixedSizeTree(TableSchema.FixedSizeSchemaIndexDef indexDef)
        {
            if (indexDef.IsGlobal)
                return _tx.GetGlobalFixedSizeTree(indexDef.Name, sizeof(long), isIndexTree: true, newPageAllocator: _globalPageAllocator);

            var tableTree = _tx.ReadTree(Name);
            return GetFixedSizeTree(tableTree, indexDef.Name, sizeof(long), isGlobal: false, isIndexTree: true);
        }

        internal FixedSizeTree GetFixedSizeTree(Tree parent, Slice name, ushort valSize, bool isGlobal, bool isIndexTree = false)
        {
            if (_fixedSizeTreeCache.TryGetValue(parent.Name, out Dictionary<Slice, FixedSizeTree> cache) == false)
            {
                cache = new Dictionary<Slice, FixedSizeTree>(SliceStructComparer.Instance);
                _fixedSizeTreeCache[parent.Name] = cache;
            }

            if (cache.TryGetValue(name, out FixedSizeTree tree) == false)
            {
                var allocator = isGlobal ? _globalPageAllocator : _tablePageAllocator;
                var fixedSizeTree = new FixedSizeTree(_tx.LowLevelTransaction, parent, name, valSize, isIndexTree: isIndexTree | parent.IsIndexTree, newPageAllocator: allocator);
                return cache[fixedSizeTree.Name] = fixedSizeTree;
            }

            return tree;
        }

        private void CreateNewActiveSection(Slice hash)
        {
            ushort maxSectionSizeInPages =
                _tx.LowLevelTransaction.Environment.Options.RunningOn32Bits
                    ? (ushort)((1 * Constants.Size.Megabyte) / Constants.Storage.PageSize)
                    : (ushort)((32 * Constants.Size.Megabyte) / Constants.Storage.PageSize);

            var newNumberOfPages = Math.Min(maxSectionSizeInPages,
                (ushort)(ActiveDataSmallSection.NumberOfPages * 2));

            _activeDataSmallSection = ActiveRawDataSmallSection.Create(_tx, Name, hash, _tableType, newNumberOfPages);
            _activeDataSmallSection.DataMoved += OnDataMoved;
            var val = _activeDataSmallSection.PageNumber;
            using (Slice.External(_tx.Allocator, (byte*)&val, sizeof(long), out Slice pageNumber))
            {
                _tableTree.Add(TableSchema.ActiveSectionSlice, pageNumber);
            }
        }

        private bool TryFindMatchFromCandidateSections(int size, Slice? compressionDictionaryHash, out long id)
        {
            using (var it = ActiveCandidateSection.Iterate())
            {
                if (it.Seek(long.MinValue))
                {
                    do
                    {
                        var sectionPageNumber = it.CurrentKey;

                        _activeDataSmallSection = new ActiveRawDataSmallSection(_tx, sectionPageNumber);

                        if (compressionDictionaryHash != null)
                        {
                            using var _ = _activeDataSmallSection.CurrentCompressionDictionaryHash(out var currentCompressionHash);
                            // to be accepted at this point, we need to use the same dictionary
                            if (Slice.Equals(currentCompressionHash, compressionDictionaryHash.Value) == false)
                                continue;
                        }

                        _activeDataSmallSection.DataMoved += OnDataMoved;
                        if (_activeDataSmallSection.TryAllocate(size, out id))
                        {
                            var candidatePage = _activeDataSmallSection.PageNumber;
                            using (Slice.External(_tx.Allocator, (byte*)&candidatePage, sizeof(long), out Slice pageNumber))
                            {
                                _tableTree.Add(TableSchema.ActiveSectionSlice, pageNumber);
                            }

                            ActiveCandidateSection.Delete(sectionPageNumber);
                            {
                                return true;
                            }
                        }
                    } while (it.MoveNext());
                }
            }
            id = 0;
            return false;
        }

        private void MaybeTrainCompressionDictionary(ActiveRawDataSmallSection previousSection, ZstdLib.CompressionDictionary existingDictionary, 
            TableValueCompressor compressor, ref Slice hash)
        {
            // here we'll build a buffer for the current data we have the section
            // the idea is that we'll get better results by including the most recently modified documents
            // which would reside in the active section that we are about to replace
            var dataIds = previousSection.GetAllIdsInSection();
            var sizes = new UIntPtr[dataIds.Count];
            var totalRequiredSize = 0;
            int last = 0;
            for (; last < dataIds.Count; last++)
            {
                var data = RawDataSection.DirectRead(_tx.LowLevelTransaction, dataIds[last], out int rawSize, out bool compressed);
                int size = compressed == false ? rawSize : ZstdLib.GetDecompressedSize(new ReadOnlySpan<byte>(data, rawSize));
                sizes[last] = (UIntPtr)size;
                totalRequiredSize += size;
                if (totalRequiredSize > 512 * 1024)
                    break;
            }

            using var _ = _tx.Allocator.Allocate(totalRequiredSize, out var buffer);

            var pos = 0;
            for (int i = 0; i < last; i++)
            {
                var data = RawDataSection.DirectRead(_tx.LowLevelTransaction, dataIds[i], out int rawSize, out bool compressed);
                if (compressed == false)
                {
                    Memory.Copy(buffer.Ptr + pos, data, rawSize);
                    pos += rawSize;
                }
                else
                {
                    int decompressedSize = ZstdLib.GetDecompressedSize(new ReadOnlySpan<byte>(data, rawSize));
                    pos += ZstdLib.Decompress(new ReadOnlySpan<byte>(data, rawSize), new Span<byte>(buffer.Ptr + pos, decompressedSize),
                        existingDictionary);
                }
            }
            
            using var __ = _tx.Allocator.Allocate(4096, out var dictionaryBuffer);
            Span<byte> dictionaryBufferSpan = dictionaryBuffer.ToSpan();
            ZstdLib.Train(new ReadOnlySpan<byte>(buffer.Ptr, pos), new ReadOnlySpan<UIntPtr>(sizes, 0, last), ref dictionaryBufferSpan);

            var hashBuffer = stackalloc byte[32];
            if(Sodium.crypto_generichash(hashBuffer, (UIntPtr)32, dictionaryBuffer.Ptr, (ulong)dictionaryBufferSpan.Length, Name.Content.Ptr, (UIntPtr)Name.Size) != 0)
                throw new InvalidOperationException("Unable to compute hash for buffer when creating dictionary hash");

            var hashSliceScope = Slice.From(_tx.Allocator, hashBuffer, 32, out var newHash);
            
            using var compressionDictionary = new ZstdLib.CompressionDictionary(newHash, dictionaryBuffer.Ptr, dictionaryBufferSpan.Length, 3);

            if (compressor.ShouldReplaceDictionary(compressionDictionary) == false)
            {
                hashSliceScope.Dispose();
                return;
            }

            hash = newHash;

            compressionDictionary.ExpectedCompressionRatio = compressor.CompressionRatio;
            
            var dictionariesTree = _tx.ReadTree(TableSchema.DictionariesSlice);
            using var ____ = dictionariesTree.DirectAdd(compressionDictionary.Hash, sizeof(CompressionDictionaryInfo) +dictionaryBufferSpan.Length, out var dest);
            *((CompressionDictionaryInfo*)dest) = new CompressionDictionaryInfo
            {
                ExpectedCompressionRatio = compressionDictionary.ExpectedCompressionRatio
            };
            Memory.Copy(dest + sizeof(CompressionDictionaryInfo), dictionaryBuffer.Ptr, dictionaryBufferSpan.Length);
        }

        internal Tree GetTree(Slice name, bool isIndexTree)
        {
            if (_treesBySliceCache.TryGetValue(name, out Tree tree))
                return tree;

            var treeHeader = _tableTree.DirectRead(name);
            if (treeHeader == null)
                throw new VoronErrorException($"Cannot find tree {name} in table {Name}");

            tree = Tree.Open(_tx.LowLevelTransaction, _tx, name, (TreeRootHeader*)treeHeader, isIndexTree: isIndexTree, newPageAllocator: _tablePageAllocator);
            _treesBySliceCache[name] = tree;

            return tree;
        }

        internal Tree GetTree(TableSchema.SchemaIndexDef idx)
        {
            if (idx.IsGlobal)
                return _tx.ReadTree(idx.Name, isIndexTree: true, newPageAllocator: _globalPageAllocator);
            return GetTree(idx.Name, true);
        }

        public bool DeleteByKey(Slice key)
        {
            AssertWritableTable();

            var pkTree = GetTree(_schema.Key);

            var readResult = pkTree.Read(key);
            if (readResult == null)
                return false;

            // This is an implementation detail. We read the absolute location pointer (absolute offset on the file)
            var id = readResult.Reader.ReadLittleEndianInt64();

            // And delete the element accordingly.
            Delete(id);

            return true;
        }

        private IEnumerable<TableValueHolder> GetSecondaryIndexForValue(Tree tree, Slice value, TableSchema.SchemaIndexDef index)
        {
            try
            {
                var fstIndex = GetFixedSizeTree(tree, value, 0, index.IsGlobal);
                using (var it = fstIndex.Iterate())
                {
                    if (it.Seek(long.MinValue) == false)
                        yield break;

                    var result = new TableValueHolder();
                    do
                    {
                        ReadById(it.CurrentKey, out result.Reader);
                        yield return result;
                    } while (it.MoveNext());
                }
            }
            finally
            {
                value.Release(_tx.Allocator);
            }
        }

        private IEnumerable<TableValueHolder> GetBackwardSecondaryIndexForValue(Tree tree, Slice value, TableSchema.SchemaIndexDef index)
        {
            try
            {
                var fstIndex = GetFixedSizeTree(tree, value, 0, index.IsGlobal);
                using (var it = fstIndex.Iterate())
                {
                    if (it.SeekToLast() == false)
                        yield break;

                    var result = new TableValueHolder();
                    do
                    {
                        ReadById(it.CurrentKey, out result.Reader);
                        yield return result;
                    } while (it.MovePrev());
                }
            }
            finally
            {
                value.Release(_tx.Allocator);
            }
        }

        private void ReadById(long id, out TableValueReader reader)
        {
            var ptr = DirectRead(id, out int size);
            reader = new TableValueReader(id, ptr, size);
        }

        public long GetNumberOfEntriesAfter(TableSchema.FixedSizeSchemaIndexDef index, long afterValue, out long totalCount)
        {
            var fst = GetFixedSizeTree(index);

            return fst.GetNumberOfEntriesAfter(afterValue, out totalCount);
        }

        public long GetNumberOfEntriesFor(TableSchema.FixedSizeSchemaIndexDef index)
        {
            var fst = GetFixedSizeTree(index);
            return fst.NumberOfEntries;
        }

        public IEnumerable<SeekResult> SeekForwardFrom(TableSchema.SchemaIndexDef index, Slice value, long skip, bool startsWith = false)
        {
            var tree = GetTree(index);
            if (tree == null)
                yield break;

            using (var it = tree.Iterate(true))
            {
                if (startsWith)
                    it.SetRequiredPrefix(value);

                if (it.Seek(value) == false)
                    yield break;

                do
                {
                    foreach (var result in GetSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        if (skip > 0)
                        {
                            skip--;
                            continue;
                        }

                        yield return new SeekResult
                        {
                            Key = it.CurrentKey,
                            Result = result
                        };
                    }
                } while (it.MoveNext());
            }
        }


        public IEnumerable<SeekResult> SeekForwardFromPrefix(TableSchema.SchemaIndexDef index, Slice start, Slice prefix, long skip)
        {
            var tree = GetTree(index);
            using (var it = tree.Iterate(true))
            {
                it.SetRequiredPrefix(prefix);

                if (it.Seek(start) == false)
                    yield break;

                do
                {
                    foreach (var result in GetSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        if (skip > 0)
                        {
                            skip--;
                            continue;
                        }

                        yield return new SeekResult
                        {
                            Key = it.CurrentKey,
                            Result = result
                        };
                    }
                } while (it.MoveNext());
            }
        }

        public TableValueHolder SeekOneForwardFromPrefix(TableSchema.SchemaIndexDef index, Slice value)
        {
            var tree = GetTree(index);
            if (tree == null)
                return null;

            using (var it = tree.Iterate(true))
            {
                it.SetRequiredPrefix(value);

                if (it.Seek(value) == false)
                    return null;

                do
                {
                    foreach (var result in GetSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        return result;
                    }
                } while (it.MoveNext());
            }

            return null;
        }

        public IEnumerable<SeekResult> SeekBackwardFrom(TableSchema.SchemaIndexDef index, Slice prefix, Slice last, long skip)
        {
            var tree = GetTree(index);
            if (tree == null)
                yield break;

            using (var it = tree.Iterate(true))
            {
                if (it.Seek(last) == false && it.Seek(Slices.AfterAllKeys) == false)
                    yield break;

                it.SetRequiredPrefix(prefix);
                if (SliceComparer.StartWith(it.CurrentKey, it.RequiredPrefix) == false)
                {
                    if (it.MovePrev() == false)
                        yield break;
                }

                do
                {
                    foreach (var result in GetBackwardSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        if (skip > 0)
                        {
                            skip--;
                            continue;
                        }

                        yield return new SeekResult
                        {
                            Key = it.CurrentKey,
                            Result = result
                        };
                    }
                } while (it.MovePrev());
            }
        }

        public IEnumerable<SeekResult> SeekBackwardFrom(TableSchema.SchemaIndexDef index, Slice prefix, Slice last)
        {
            var tree = GetTree(index);
            if (tree == null)
                yield break;

            using (var it = tree.Iterate(true))
            {
                if (it.Seek(last) == false && it.Seek(Slices.AfterAllKeys) == false)
                    yield break;

                it.SetRequiredPrefix(prefix);
                if (SliceComparer.StartWith(it.CurrentKey, it.RequiredPrefix) == false)
                {
                    if (it.MovePrev() == false)
                        yield break;
                }

                do
                {
                    foreach (var result in GetBackwardSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        yield return new SeekResult
                        {
                            Key = it.CurrentKey,
                            Result = result
                        };
                    }
                } while (it.MovePrev());
            }
        }

        public IEnumerable<SeekResult> SeekBackwardFrom(TableSchema.SchemaIndexDef index, Slice last)
        {
            var tree = GetTree(index);
            if (tree == null)
                yield break;

            using (var it = tree.Iterate(true))
            {
                if (it.Seek(last) == false && it.Seek(Slices.AfterAllKeys) == false)
                    yield break;

                do
                {
                    foreach (var result in GetBackwardSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        yield return new SeekResult
                        {
                            Key = it.CurrentKey,
                            Result = result
                        };
                    }
                } while (it.MovePrev());
            }
        }

        public TableValueHolder SeekOneBackwardFrom(TableSchema.SchemaIndexDef index, Slice prefix, Slice last)
        {
            var tree = GetTree(index);
            if (tree == null)
                return null;

            using (var it = tree.Iterate(true))
            {
                if (it.Seek(last) == false && it.Seek(Slices.AfterAllKeys) == false)
                    return null;

                it.SetRequiredPrefix(prefix);
                if (SliceComparer.StartWith(it.CurrentKey, it.RequiredPrefix) == false)
                {
                    if (it.MovePrev() == false)
                        return null;
                }

                do
                {
                    foreach (var result in GetBackwardSecondaryIndexForValue(tree, it.CurrentKey.Clone(_tx.Allocator), index))
                    {
                        return result;
                    }
                } while (it.MovePrev());
            }

            return null;
        }

        public long GetCountOfMatchesFor(TableSchema.SchemaIndexDef index, Slice value)
        {
            var tree = GetTree(index);

            var fstIndex = GetFixedSizeTree(tree, value, 0, index.IsGlobal);

            return fstIndex.NumberOfEntries;
        }

        public IEnumerable<(Slice Key, TableValueHolder Value)> SeekByPrimaryKeyPrefix(Slice requiredPrefix, Slice startAfter, long skip)
        {
            var isStartAfter = startAfter.Equals(Slices.Empty) == false;

            var pk = _schema.Key;
            var tree = GetTree(pk);
            using (var it = tree.Iterate(true))
            {
                it.SetRequiredPrefix(requiredPrefix);

                var seekValue = isStartAfter ? startAfter : requiredPrefix;
                if (it.Seek(seekValue) == false)
                    yield break;

                if (isStartAfter && it.MoveNext() == false)
                    yield break;

                if (it.Skip(skip) == false)
                    yield break;

                var result = new TableValueHolder();
                do
                {
                    GetTableValueReader(it, out result.Reader);
                    yield return (it.CurrentKey, result);
                }
                while (it.MoveNext());
            }
        }

        public IEnumerable<TableValueHolder> SeekByPrimaryKey(Slice value, long skip)
        {
            var pk = _schema.Key;
            var tree = GetTree(pk);
            using (var it = tree.Iterate(true))
            {
                if (it.Seek(value) == false)
                    yield break;

                if (it.Skip(skip) == false)
                    yield break;

                var result = new TableValueHolder();
                do
                {
                    GetTableValueReader(it, out result.Reader);
                    yield return result;
                }
                while (it.MoveNext());
            }
        }

        public bool SeekOneBackwardByPrimaryKeyPrefix(Slice prefix, Slice value, out TableValueReader reader, bool excludeValueFromSeek = false)
        {
            reader = default;
            var pk = _schema.Key;
            var tree = GetTree(pk);
            using (var it = tree.Iterate(true))
            {
                if (it.Seek(value) == false)
                {
                    if (it.Seek(Slices.AfterAllKeys) == false)
                        return false;

                    if (SliceComparer.StartWith(it.CurrentKey, prefix) == false)
                    {
                        it.SetRequiredPrefix(prefix);
                        if (it.MovePrev() == false)
                            return false;
                    }
                }
                else if (SliceComparer.AreEqual(it.CurrentKey, value) == excludeValueFromSeek)
                {
                    it.SetRequiredPrefix(prefix);
                    if (it.MovePrev() == false)
                        return false;
                }

                GetTableValueReader(it, out reader);
                return true;
            }
        }

        public void DeleteByPrimaryKey(Slice value, Func<TableValueHolder, bool> deletePredicate)
        {
            AssertWritableTable();

            var pk = _schema.Key;
            var tree = GetTree(pk);
            TableValueHolder tableValueHolder = null;
            value = value.Clone(_tx.Allocator);
            try
            {
                while (true)
                {
                    using (var it = tree.Iterate(true))
                    {
                        if (it.Seek(value) == false)
                            return;

                        while (true)
                        {
                            var id = it.CreateReaderForCurrent().ReadLittleEndianInt64();
                            var ptr = DirectRead(id, out int size);
                            if (tableValueHolder == null)
                                tableValueHolder = new TableValueHolder();
                            tableValueHolder.Reader = new TableValueReader(id, ptr, size);
                            if (deletePredicate(tableValueHolder))
                            {
                                value.Release(_tx.Allocator);
                                value = it.CurrentKey.Clone(_tx.Allocator);
                                Delete(id);
                                break;
                            }

                            if (it.MoveNext() == false)
                                return;
                        }
                    }
                }
            }
            finally
            {
                value.Release(_tx.Allocator);
            }
        }


        public TableValueHolder ReadFirst(TableSchema.FixedSizeSchemaIndexDef index)
        {
            var fst = GetFixedSizeTree(index);

            using (var it = fst.Iterate())
            {
                if (it.Seek(0) == false)
                    return null;

                var result = new TableValueHolder();
                GetTableValueReader(it, out result.Reader);
                return result;
            }
        }

        public bool SeekOnePrimaryKey(Slice slice, out TableValueReader reader)
        {
            Debug.Assert(slice.Options != SliceOptions.Key, "Should be called with only AfterAllKeys or BeforeAllKeys");

            var pk = _schema.Key;
            var tree = GetTree(pk);
            using (var it = tree.Iterate(false))
            {
                if (it.Seek(slice) == false)
                {
                    reader = default(TableValueReader);
                    return false;
                }

                GetTableValueReader(it, out reader);
                return true;
            }
        }

        public bool SeekOnePrimaryKeyPrefix(Slice slice, out TableValueReader reader)
        {
            Debug.Assert(slice.Options == SliceOptions.Key, "Should be called with Key only");

            var pk = _schema.Key;
            var tree = GetTree(pk);
            using (var it = tree.Iterate(false))
            {
                it.SetRequiredPrefix(slice);

                if (it.Seek(slice) == false)
                {
                    reader = default(TableValueReader);
                    return false;
                }

                GetTableValueReader(it, out reader);
                return true;
            }
        }

        public bool SeekOnePrimaryKeyWithPrefix(Slice prefix, Slice value, out TableValueReader reader)
        {
            var pk = _schema.Key;
            var tree = GetTree(pk);
            using (var it = tree.Iterate(false))
            {
                it.SetRequiredPrefix(prefix);

                if (it.Seek(value) == false)
                {
                    reader = default(TableValueReader);
                    return false;
                }

                GetTableValueReader(it, out reader);
                return true;
            }
        }


        public IEnumerable<TableValueHolder> SeekForwardFrom(TableSchema.FixedSizeSchemaIndexDef index, long key, long skip)
        {
            var fst = GetFixedSizeTree(index);
            
            using (var it = fst.Iterate())
            {
                if (it.Seek(key) == false)
                    yield break;

                if (it.Skip(skip) == false)
                    yield break;

                var result = new TableValueHolder();
                do
                {
                    GetTableValueReader(it, out result.Reader);
                    yield return result;
                } while (it.MoveNext());
            }
        }

        public TableValueHolder ReadLast(TableSchema.FixedSizeSchemaIndexDef index)
        {
            var fst = GetFixedSizeTree(index);

            using (var it = fst.Iterate())
            {
                if (it.SeekToLast() == false)
                    return null;

                var result = new TableValueHolder();
                GetTableValueReader(it, out result.Reader);
                return result;
            }
        }

        public IEnumerable<TableValueHolder> SeekBackwardFromLast(TableSchema.FixedSizeSchemaIndexDef index, long skip = 0)
        {
            var fst = GetFixedSizeTree(index);
            using (var it = fst.Iterate())
            {
                if (it.SeekToLast() == false)
                    yield break;

                if (it.Skip(-skip) == false)
                    yield break;

                var result = new TableValueHolder();
                do
                {
                    GetTableValueReader(it, out result.Reader);
                    yield return result;
                } while (it.MovePrev());
            }
        }


        public IEnumerable<TableValueHolder> SeekBackwardFrom(TableSchema.FixedSizeSchemaIndexDef index, long key, long skip = 0)
        {
            var fst = GetFixedSizeTree(index);
            using (var it = fst.Iterate())
            {
                if (it.Seek(key) == false &&
                    it.SeekToLast() == false)
                    yield break;

                if (it.Skip(-skip) == false)
                    yield break;

                var result = new TableValueHolder();
                do
                {
                    GetTableValueReader(it, out result.Reader);
                    yield return result;
                } while (it.MovePrev());
            }
        }

        public bool HasEntriesGreaterThanStartAndLowerThanOrEqualToEnd(TableSchema.FixedSizeSchemaIndexDef index, long start, long end)
        {
            var fst = GetFixedSizeTree(index);

            using (var it = fst.Iterate())
            {
                if (it.Seek(start) == false)
                    return false;

                if (it.CurrentKey <= start && it.MoveNext() == false)
                    return false;

                return it.CurrentKey <= end;
            }
        }

        private void GetTableValueReader(FixedSizeTree.IFixedSizeIterator it, out TableValueReader reader)
        {
            long id;
            using (it.Value(out Slice slice))
                slice.CopyTo((byte*)&id);
            var ptr = DirectRead(id, out int size);
            reader = new TableValueReader(id, ptr, size);
        }


        private void GetTableValueReader(IIterator it, out TableValueReader reader)
        {
            var id = it.CreateReaderForCurrent().ReadLittleEndianInt64();
            var ptr = DirectRead(id, out int size);
            reader = new TableValueReader(id, ptr, size);
        }

        public bool Set(TableValueBuilder builder, bool forceUpdate = false)
        {
            AssertWritableTable();

            // The ids returned from this function MUST NOT be stored outside of the transaction.
            // These are merely for manipulation within the same transaction, and WILL CHANGE afterwards.
            long id;
            bool exists;

            using (builder.SliceFromLocation(_tx.Allocator, _schema.Key.StartIndex, out Slice key))
            {
                exists = TryFindIdFromPrimaryKey(key, out id);
            }

            if (exists)
            {
                Update(id, builder, forceUpdate);
                return false;
            }

            Insert(builder);
            return true;
        }
        
        public long DeleteBackwardFrom(TableSchema.FixedSizeSchemaIndexDef index, long value, long numberOfEntriesToDelete)
        {
            AssertWritableTable();

            if (numberOfEntriesToDelete < 0)
                ThrowNonNegativeNumberOfEntriesToDelete();

            long deleted = 0;
            var fst = GetFixedSizeTree(index);
            // deleting from a table can shift things around, so we delete 
            // them one at a time
            while (deleted < numberOfEntriesToDelete)
            {
                using (var it = fst.Iterate())
                {
                    if (it.Seek(long.MinValue) == false)
                        return deleted;

                    if (it.CurrentKey > value)
                        return deleted;

                    Delete(it.CreateReaderForCurrent().ReadLittleEndianInt64());
                    deleted++;
                }
            }

            return deleted;
        }

        public bool DeleteByIndex(TableSchema.FixedSizeSchemaIndexDef index, long value)
        {
            AssertWritableTable();

            var fst = GetFixedSizeTree(index);

            using (var it = fst.Iterate())
            {
                if (it.Seek(value) == false)
                    return false;

                if (it.CurrentKey != value)
                    return false;

                Delete(it.CreateReaderForCurrent().ReadLittleEndianInt64());
                return true;
            }
        }

        public bool DeleteByPrimaryKeyPrefix(Slice startSlice, Action<TableValueHolder> beforeDelete = null, Func<TableValueHolder, bool> shouldAbort = null)
        {
            AssertWritableTable();

            bool deleted = false;
            var pk = _schema.Key;
            var tree = GetTree(pk);
            TableValueHolder tableValueHolder = null;
            while (true)
            {
                using (var it = tree.Iterate(true))
                {
                    it.SetRequiredPrefix(startSlice);
                    if (it.Seek(it.RequiredPrefix) == false)
                        return deleted;

                    long id = it.CreateReaderForCurrent().ReadLittleEndianInt64();

                    if (beforeDelete != null || shouldAbort != null)
                    {
                        int size;
                        var ptr = DirectRead(id, out size);
                        if (tableValueHolder == null)
                            tableValueHolder = new TableValueHolder();
                        tableValueHolder.Reader = new TableValueReader(id, ptr, size);
                        if (shouldAbort?.Invoke(tableValueHolder) == true)
                        {
                            return deleted;
                        }
                        beforeDelete?.Invoke(tableValueHolder);
                    }

                    Delete(id);
                    deleted = true;
                }
            }
        }

        public long DeleteForwardFrom(TableSchema.SchemaIndexDef index, Slice value, bool startsWith, long numberOfEntriesToDelete,
            Action<TableValueHolder> beforeDelete = null, Func<TableValueHolder, bool> shouldAbort = null)
        {
            AssertWritableTable();

            if (numberOfEntriesToDelete < 0)
                ThrowNonNegativeNumberOfEntriesToDelete();

            long deleted = 0;
            var tree = GetTree(index);
            TableValueHolder tableValueHolder = null;
            while (deleted < numberOfEntriesToDelete)
            {
                // deleting from a table can shift things around, so we delete 
                // them one at a time
                using (var it = tree.Iterate(true))
                {
                    if (startsWith)
                        it.SetRequiredPrefix(value);
                    if (it.Seek(value) == false)
                        return deleted;

                    var fst = GetFixedSizeTree(tree, it.CurrentKey.Clone(_tx.Allocator), 0, index.IsGlobal);
                    using (var fstIt = fst.Iterate())
                    {
                        if (fstIt.Seek(long.MinValue) == false)
                            break;

                        if (beforeDelete != null || shouldAbort != null)
                        {
                            var ptr = DirectRead(fstIt.CurrentKey, out int size);
                            if (tableValueHolder == null)
                                tableValueHolder = new TableValueHolder();
                            tableValueHolder.Reader = new TableValueReader(fstIt.CurrentKey, ptr, size);
                            if (shouldAbort?.Invoke(tableValueHolder) == true)
                            {
                                return deleted;
                            }
                            beforeDelete?.Invoke(tableValueHolder);
                        }

                        Delete(fstIt.CurrentKey);
                        deleted++;
                    }
                }
            }
            return deleted;
        }

        public long DeleteForwardFrom(TableSchema.SchemaIndexDef index, Slice value, bool startsWith, long numberOfEntriesToDelete,
            Func<TableValueHolder, bool> beforeDelete)
        {
            AssertWritableTable();

            if (numberOfEntriesToDelete < 0)
                ThrowNonNegativeNumberOfEntriesToDelete();

            var deleted = 0;
            var tree = GetTree(index);
            TableValueHolder tableValueHolder = null;
            while (deleted < numberOfEntriesToDelete)
            {
                // deleting from a table can shift things around, so we delete 
                // them one at a time
                using (var it = tree.Iterate(true))
                {
                    if (startsWith)
                        it.SetRequiredPrefix(value);
                    if (it.Seek(value) == false)
                        return deleted;

                    var fst = GetFixedSizeTree(tree, it.CurrentKey.Clone(_tx.Allocator), 0, index.IsGlobal);
                    using (var fstIt = fst.Iterate())
                    {
                        if (fstIt.Seek(long.MinValue) == false)
                            break;

                        var ptr = DirectRead(fstIt.CurrentKey, out int size);
                        if (tableValueHolder == null)
                            tableValueHolder = new TableValueHolder();
                        tableValueHolder.Reader = new TableValueReader(fstIt.CurrentKey, ptr, size);
                        if (beforeDelete(tableValueHolder) == false)
                            return deleted;

                        Delete(fstIt.CurrentKey);
                        deleted++;
                    }
                }
            }
            return deleted;
        }

        public bool DeleteForwardUpToPrefix(Slice startSlice, long upToIndex, long numberOfEntriesToDelete)
        {
            AssertWritableTable();

            if (numberOfEntriesToDelete < 0)
                ThrowNonNegativeNumberOfEntriesToDelete();

            var deleted = 0;
            var pk = _schema.Key;
            var pkTree = GetTree(pk);
            TableValueHolder tableValueHolder = null;
            while (deleted < numberOfEntriesToDelete)
            {
                using (var it = pkTree.Iterate(true))
                {
                    it.SetRequiredPrefix(startSlice);
                    if (it.Seek(it.RequiredPrefix) == false)
                        return false;

                    var id = it.CreateReaderForCurrent().ReadLittleEndianInt64();
                    var ptr = DirectRead(id, out var size);

                    if (tableValueHolder == null)
                        tableValueHolder = new TableValueHolder();

                    tableValueHolder.Reader = new TableValueReader(id, ptr, size);
                    var currentIndex = *(long*)tableValueHolder.Reader.Read(1, out _);

                    if (currentIndex > upToIndex)
                        return false;

                    Delete(id);
                    deleted++;
                }
            }

            return true;
        }

        private static void ThrowNonNegativeNumberOfEntriesToDelete()
        {
            throw new VoronErrorException("Number of entries should not be negative");
        }

        public void PrepareForCommit()
        {
            AssertValidTable();

            AssertValidIndexes();

            if (_treesBySliceCache == null)
                return;

            foreach (var item in _treesBySliceCache)
            {
                var tree = item.Value;
                if (!tree.State.IsModified)
                    continue;

                var treeName = item.Key;

                byte* ptr;
                using (_tableTree.DirectAdd(treeName, sizeof(TreeRootHeader), out ptr))
                {
                    var header = (TreeRootHeader*)ptr;
                    tree.State.CopyTo(header);
                }
            }
        }

        [Conditional("DEBUG")]
        private void AssertValidIndexes()
        {
            var pk = _schema.Key;
            if (pk != null && pk.IsGlobal == false)
            {
                var tree = GetTree(pk);
                if (tree.State.NumberOfEntries != NumberOfEntries)
                    throw new InvalidDataException($"Mismatch in primary key size to table size: {tree.State.NumberOfEntries} != {NumberOfEntries}");
            }

            foreach (var fst in _schema.FixedSizeIndexes)
            {
                if (fst.Value.IsGlobal)
                    continue;

                var tree = GetFixedSizeTree(fst.Value);
                if (tree.NumberOfEntries != NumberOfEntries)
                    throw new InvalidDataException($"Mismatch in fixed sized tree {fst.Key} size to table size: {tree.NumberOfEntries} != {NumberOfEntries}");
            }
        }

        private void ThrowInconsistentItemsCountInIndexes(string indexName, long expectedSize, long actualSize)
        {
            throw new InvalidOperationException($"Inconsistent index items count detected! Index name: {indexName} expected size: {expectedSize} actual size: {actualSize}");
        }

        /// <summary>
        /// validate all globals indexes has the same number
        /// validate all local indexes has the same number as the table itself
        /// </summary>
        [Conditional("VALIDATE")]
        internal void AssertValidTable()
        {
            long globalDocsCount = -1;

            foreach (var fsi in _schema.FixedSizeIndexes)
            {
                var indexNumberOfEntries = GetFixedSizeTree(fsi.Value).NumberOfEntries;
                if (fsi.Value.IsGlobal == false)
                {
                    if (NumberOfEntries != indexNumberOfEntries)
                        ThrowInconsistentItemsCountInIndexes(fsi.Key.ToString(), NumberOfEntries, indexNumberOfEntries);

                }
                else
                {
                    if (globalDocsCount == -1)
                        globalDocsCount = indexNumberOfEntries;
                    else if (globalDocsCount != indexNumberOfEntries)
                        ThrowInconsistentItemsCountInIndexes(fsi.Key.ToString(), NumberOfEntries, indexNumberOfEntries);
                }
            }

            if (_schema.Key == null)
                return;

            var pkIndexNumberOfEntries = GetTree(_schema.Key).State.NumberOfEntries;
            if (_schema.Key.IsGlobal == false)
            {
                if (NumberOfEntries != pkIndexNumberOfEntries)
                    ThrowInconsistentItemsCountInIndexes(_schema.Key.Name.ToString(), NumberOfEntries, pkIndexNumberOfEntries);
            }
            else
            {
                if (globalDocsCount == -1)
                    globalDocsCount = pkIndexNumberOfEntries;
                else if (globalDocsCount != pkIndexNumberOfEntries)
                    ThrowInconsistentItemsCountInIndexes(_schema.Key.Name.ToString(), NumberOfEntries, pkIndexNumberOfEntries);
            }
        }


        public void Dispose()
        {
            foreach (var item in _treesBySliceCache)
            {
                item.Value.Dispose();
            }

            foreach (var item in _fixedSizeTreeCache)
            {
                foreach (var item2 in item.Value)
                {
                    item2.Value.Dispose();
                }
            }

            _activeCandidateSection?.Dispose();
            _activeDataSmallSection?.Dispose();
            _inactiveSections?.Dispose();
            _tableTree?.Dispose();
        }

        public TableReport GetReport(bool includeDetails)
        {
            var overflowSize = _overflowPageCount * Constants.Storage.PageSize;
            var report = new TableReport(overflowSize, overflowSize, includeDetails)
            {
                Name = Name.ToString(),
                NumberOfEntries = NumberOfEntries
            };

            report.AddStructure(_tableTree, includeDetails);

            if (_schema.Key != null && _schema.Key.IsGlobal == false)
            {
                var pkTree = GetTree(_schema.Key);
                report.AddIndex(pkTree, includeDetails);
            }

            foreach (var index in _schema.FixedSizeIndexes)
            {
                if (index.Value.IsGlobal)
                    continue;

                var fst = GetFixedSizeTree(index.Value);
                report.AddIndex(fst, includeDetails);
            }

            foreach (var index in _schema.Indexes)
            {
                if (index.Value.IsGlobal)
                    continue;

                var tree = GetTree(index.Value);
                report.AddIndex(tree, includeDetails);
            }

            var activeCandidateSection = ActiveCandidateSection;
            report.AddStructure(activeCandidateSection, includeDetails);

            var inactiveSections = InactiveSections;
            report.AddStructure(inactiveSections, includeDetails);

            using (var it = inactiveSections.Iterate())
            {
                if (it.Seek(0))
                {
                    do
                    {
                        var inactiveSection = new RawDataSection(_tx.LowLevelTransaction, it.CurrentKey);
                        report.AddData(inactiveSection, includeDetails);
                    } while (it.MoveNext());
                }
            }

            report.AddData(ActiveDataSmallSection, includeDetails);

            report.AddPreAllocatedBuffers(_tablePageAllocator, includeDetails);

            return report;
        }

        [Conditional("DEBUG")]
        private void AssertWritableTable()
        {
            if (_forGlobalReadsOnly)
                throw new InvalidOperationException("Table is meant to be used for global reads only while it attempted to modify the data");
        }

        public struct SeekResult
        {
            public Slice Key;
            public TableValueHolder Result;
        }

        public class TableValueHolder
        {
            // we need this so we'll not have to create a new allocation
            // of TableValueReader per value
            public TableValueReader Reader;
        }

        public ReturnTableValueBuilderToCache Allocate(out TableValueBuilder builder)
        {
            var builderToCache = new ReturnTableValueBuilderToCache(_tx);
            builder = builderToCache.Builder;
            return builderToCache;
        }

        public struct ReturnTableValueBuilderToCache : IDisposable
        {
#if DEBUG
            private readonly Transaction _tx;
#endif

            public ReturnTableValueBuilderToCache(Transaction tx)
            {
                var environmentWriteTransactionPool = tx.LowLevelTransaction.Environment.WriteTransactionPool;
#if DEBUG
                _tx = tx;
                Debug.Assert(tx.LowLevelTransaction.Flags == TransactionFlags.ReadWrite);
                if (environmentWriteTransactionPool.BuilderUsages++ != 0)
                    throw new InvalidOperationException("Cannot use a cached table value builder when it is already in use");
#endif
                Builder = environmentWriteTransactionPool.TableValueBuilder;
                Builder.Compression.SetCurrentTransaction(tx);
            }

            public TableValueBuilder Builder { get; }

            public void Dispose()
            {
                Builder.Reset();
#if DEBUG
                Debug.Assert(_tx.LowLevelTransaction.IsDisposed == false);
                if (_tx.LowLevelTransaction.Environment.WriteTransactionPool.BuilderUsages-- != 1)
                    throw new InvalidOperationException("Cannot use a cached table value builder when it is already removed");
#endif
            }
        }
    }
}
