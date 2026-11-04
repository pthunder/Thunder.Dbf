//      [ Bbb | Ccc | Ddd | Ppp ]  + Mmm     ProjectThunder :: Thunder.Dbf tests
//                  |                        A key that fits goes into its leaf in place:
//      [ Bbb | Ccc | Ddd | Mmm | Ppp ]      the block must come out exactly as WriteNode
//                                           would write it, byte for byte.

using System.Buffers.Binary;

namespace Thunder.Dbf.Tests;

public sealed class MdxInPlaceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("thunder-mdx-inplace-");

    public void Dispose() => _dir.Delete(recursive: true);

    // A 30-byte key sits in a 36-byte item, so each item has 2 bytes of padding. The leaf is given
    // junk where WriteNode writes zeros (bytes 4-7, the padding, everything past the items), as a
    // file another program wrote may have; the insert must leave the block exactly as WriteNode would.
    [Theory]
    [InlineData(false, "Aaa")] // first
    [InlineData(false, "Mmm")] // middle
    [InlineData(false, "Zzz")] // last
    [InlineData(false, "Ccc")] // after the equal key
    [InlineData(true, "Aaa")]
    [InlineData(true, "Mmm")]
    [InlineData(true, "Zzz")]
    [InlineData(true, "Ccc")]
    public void A_key_added_in_place_leaves_the_leaf_as_WriteNode_writes_it(bool descending, string name)
    {
        using var stream = OpenIndex(descending, ["Bbb", "Ccc", "Ddd", "Ppp", "Qqq"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        var tag = index["NAME"];
        Assert.True(tag.ItemLength > tag.KeyLength + 4);
        var root = RootPage(index, tag);

        var block = index.ReadBlock(root);
        var count = BinaryPrimitives.ReadInt32LittleEndian(block);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + count * tag.ItemLength))); // a leaf
        block.AsSpan(4, 4).Fill(0xAA);
        for (var i = 0; i < count; i++)
            block.AsSpan(8 + i * tag.ItemLength + 4 + tag.KeyLength, tag.ItemLength - 4 - tag.KeyLength).Fill(0xBB);
        block.AsSpan(8 + count * tag.ItemLength + 4).Fill(0xCC);
        index.WriteBlock(root, block);

        var items = Enumerable.Range(0, count).Select(i => (
            Pointer: BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8 + i * tag.ItemLength)),
            Key: block.AsSpan(8 + i * tag.ItemLength + 4, tag.KeyLength).ToArray())).ToList();
        var key = MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, name, tag.Name, tag.Expression);
        var slot = items.FindIndex(item => tag.CompareKeys(item.Key, key) > 0); // after any equal keys
        items.Insert(slot < 0 ? items.Count : slot, (99, key));

        tag.Insert(key, 99);

        var expected = new byte[index.BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(expected, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(8 + i * tag.ItemLength), items[i].Pointer);
            items[i].Key.CopyTo(expected.AsSpan(8 + i * tag.ItemLength + 4));
        }
        Assert.Equal(expected, index.ReadBlock(root));
    }

    // FirstRecordFor lands on one leaf and falls back to the full walk when the key would sit in a
    // later one: removing a leaf's last key leaves its separator larger than what it holds, so a
    // duplicate of that key further on is found only through the fallback. Thousands of inserts and
    // removes over 60 keys, with many duplicates, must agree with a plain walk of every entry.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstRecordFor_finds_keys_in_later_leaves_after_removals(bool descending)
    {
        using var stream = OpenIndex(descending, ["Bbb"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        var tag = index["NAME"];
        byte[] Key(string name) => MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, name, tag.Name, tag.Expression);

        var random = new Random(3);
        var live = new List<(string Name, int Record)>();
        for (var step = 0; step < 4000; step++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                var pick = random.Next(live.Count);
                Assert.True(tag.Remove(Key(live[pick].Name), live[pick].Record));
                live.RemoveAt(pick);
            }
            else
            {
                var name = $"K{random.Next(60):D2}";
                tag.Insert(Key(name), step + 2);
                live.Add((name, step + 2));
            }
        }

        var entries = tag.Entries().ToList();
        foreach (var name in Enumerable.Range(0, 61).Select(i => $"K{i:D2}"))
        {
            int? first = entries.FirstOrDefault(e => ((string)e.Key!).TrimEnd() == name) is { RecordNumber: > 0 } hit ? hit.RecordNumber : null;
            Assert.Equal(first, tag.FirstRecordFor(Key(name)));
        }
    }

    // A full leaf takes the general path, which splits it: once as the root (a new root grows above it),
    // once under an interior node (the parent gains a pointer). Every key stays, in order (review of #34).
    [Fact]
    public void An_insert_into_a_full_leaf_splits_it_and_keeps_every_key()
    {
        using var stream = OpenIndex(descending: false, ["M000"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        var tag = index["NAME"];
        byte[] Key(string name) => MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, name, tag.Name, tag.Expression);
        int Count(int page) => BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(page));
        bool IsLeaf(int page) => BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(page).AsSpan(8 + Count(page) * tag.ItemLength)) == 0;
        var names = new List<string> { "M000" };
        void Add(string name) { tag.Insert(Key(name), names.Count + 1); names.Add(name); }
        void AssertEveryKey() => Assert.Equal(names.Order(StringComparer.Ordinal), tag.Entries().Select(e => ((string)e.Key!).TrimEnd()));

        for (var i = 1; i < tag.MaxKeys; i++)
            Add($"M{i:D3}");
        var root = RootPage(index, tag);
        Assert.True(IsLeaf(root));
        Assert.Equal(tag.MaxKeys, Count(root)); // a full root leaf
        Add("M0005");
        Assert.NotEqual(root, RootPage(index, tag)); // a new root above the two halves
        Assert.False(IsLeaf(RootPage(index, tag)));
        AssertEveryKey();

        // Keys before every other fill the first leaf under the interior root, then one more splits it.
        root = RootPage(index, tag);
        var first = BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(root).AsSpan(8));
        for (var i = 0; Count(first) < tag.MaxKeys; i++)
            Add($"A{i:D3}");
        var pointers = Count(root);
        Add("A0005");
        Assert.Equal(root, RootPage(index, tag));
        Assert.Equal(pointers + 1, Count(root)); // the parent gained the new right half
        AssertEveryKey();
    }

    // Issue #35: a writer killed partway through a split must never leave a key that was already in the
    // tag silently missing. The insert is cut off after each of its writes in turn (a stream that throws
    // once k writes have gone through). A leaf split keeps every old key findable by seek and by scan
    // whatever the cut. An interior split can leave its moved children reachable through both halves
    // for a moment; a walk then reports the index corrupt (RebuildIndex repairs it), never quietly
    // drops the keys, which is what the old write order did.
    [Theory]
    [InlineData("root leaf")]
    [InlineData("leaf under an interior root")]
    [InlineData("interior node too")]
    public void A_split_cut_off_after_any_write_keeps_every_old_key(string shape)
    {
        var (before, names) = SplitReady(shape);

        var reported = 0;
        for (var allowed = 0; ; allowed++)
        {
            using var stream = new CutOffStream(before, allowed);
            using (var index = MdxIndex.Open(stream, leaveOpen: true))
            {
                var tag = index["NAME"];
                var cut = Record.Exception(() => tag.Insert(MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, "K9999", tag.Name, tag.Expression), 99_999));
                Assert.True(cut is null || (stream.CutOff && cut is IOException { Message: "cut off" }), $"{shape}: {cut}");
            }
            using var after = new MemoryStream(stream.ToArray());
            using var reopened = MdxIndex.Open(after);
            var check = reopened["NAME"];
            HashSet<string> scanned;
            try
            {
                scanned = check.Entries().Select(e => ((string)e.Key!).TrimEnd()).ToHashSet();
            }
            catch (InvalidDataException) when (shape == "interior node too")
            {
                reported++;
                continue; // reported, not silently short
            }
            foreach (var name in names)
            {
                Assert.True(scanned.Contains(name), $"{shape}: {name} missing from a scan after {allowed} writes");
                Assert.NotNull(check.FirstRecordFor(MdxTag.EncodeKey(check.KeyType, check.KeyLength, reopened.Encoding, name, check.Name, check.Expression)));
            }
            if (!stream.CutOff)
            {
                Assert.Contains("K9999", scanned); // the whole insert went through
                Assert.True(allowed >= 3, $"{shape}: only {allowed} writes were cut, so nothing was really tested");
                break;
            }
        }
        Assert.True(shape == "interior node too" || reported == 0);
    }

    // A tag one insert away from the split the shape names: keys in order fill the last leaf, and each
    // split adds a pointer to its parent. Returns the .mdx bytes and the keys already in the tag.
    private (byte[] Bytes, List<string> Names) SplitReady(string shape)
    {
        using var file = OpenIndex(descending: false, ["K0000"]);
        var names = new List<string> { "K0000" };
        using (var index = MdxIndex.Open(file, leaveOpen: true))
        {
            var tag = index["NAME"];
            int Count(int page) => BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(page));
            int Last(int page) => BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(page).AsSpan(8 + Count(page) * tag.ItemLength));
            bool Ready()
            {
                var root = RootPage(index, tag);
                if (Last(root) == 0)
                    return shape == "root leaf" && Count(root) == tag.MaxKeys;
                var leaf = root;
                while (Last(leaf) != 0)
                    leaf = Last(leaf);
                var full = Count(leaf) == tag.MaxKeys;
                return shape == "leaf under an interior root" ? full && Last(Last(root)) == 0 : full && Count(root) == tag.MaxKeys && Last(Last(root)) == 0;
            }
            while (!Ready()) // keys in order fill the last leaf; each split adds a pointer to its parent
            {
                var name = $"K{names.Count:D4}";
                tag.Insert(MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, name, tag.Name, tag.Expression), names.Count + 1);
                names.Add(name);
            }
        }
        var before = new byte[file.Length];
        file.Position = 0;
        file.ReadExactly(before);
        return (before, names);

    }

    // A power cut keeps every write up to the last flush, and any of the ones after it, in any mix: a disk
    // may put them down in another order. The insert's writes and flushes are recorded once; for every
    // point the power could go, every mix of the unflushed writes is replayed onto the file as it was.
    // Each must still find every old key (or, mid interior split, report the index damaged), as the
    // killed-writer test asks. Without the flushes a mix holding only the shortened leaf loses keys.
    [Theory]
    [InlineData("root leaf")]
    [InlineData("leaf under an interior root")]
    [InlineData("interior node too")]
    public void A_split_cut_by_power_keeps_every_old_key_when_writes_are_forced(string shape)
    {
        var (before, names) = SplitReady(shape);
        using var recording = new RecordingStream(before);
        using (var index = MdxIndex.Open(recording, leaveOpen: true))
        {
            index.ForcesWrites = () => true;
            var tag = index["NAME"];
            tag.Insert(MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, "K9999", tag.Name, tag.Expression), 99_999);
        }
        var steps = recording.Steps;
        Assert.Contains(steps, s => s.Barrier);

        for (var cut = 0; cut <= steps.Count; cut++)
        {
            var lastBarrier = cut == 0 ? -1 : steps.FindLastIndex(cut - 1, cut, s => s.Barrier);
            var kept = steps.Take(lastBarrier + 1).Where(s => !s.Barrier).ToList();
            var loose = steps.Skip(lastBarrier + 1).Take(cut - lastBarrier - 1).Where(s => !s.Barrier).ToList();
            Assert.True(loose.Count <= 12, $"{shape}: {loose.Count} unflushed writes at once");
            for (var mix = 0; mix < 1 << loose.Count; mix++)
            {
                var bytes = (byte[])before.Clone();
                foreach (var step in kept.Concat(loose.Where((_, i) => (mix & (1 << i)) != 0)))
                {
                    if (bytes.Length < step.Offset + step.Bytes.Length)
                        Array.Resize(ref bytes, (int)step.Offset + step.Bytes.Length);
                    step.Bytes.CopyTo(bytes, step.Offset);
                }
                using var reopened = MdxIndex.Open(new MemoryStream(bytes));
                var check = reopened["NAME"];
                HashSet<string> scanned;
                try
                {
                    scanned = check.Entries().Select(e => ((string)e.Key!).TrimEnd()).ToHashSet();
                }
                catch (InvalidDataException) when (shape == "interior node too")
                {
                    continue; // reported, not silently short
                }
                foreach (var name in names)
                    Assert.True(scanned.Contains(name), $"{shape}: {name} missing after a power cut at step {cut}, writes kept {mix}");
            }
        }
    }

    // A file in memory that records each write (where and what) and each flush, in order.
    private sealed class RecordingStream : MemoryStream
    {
        private bool _loading = true;

        public RecordingStream(byte[] bytes)
        {
            Write(bytes, 0, bytes.Length);
            Position = 0;
            _loading = false;
        }

        public List<(long Offset, byte[] Bytes, bool Barrier)> Steps { get; } = [];

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_loading)
                Steps.Add((Position, buffer.AsSpan(offset, count).ToArray(), false));
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void Flush()
        {
            Steps.Add((0, [], true));
            base.Flush();
        }
    }

    // A file in memory whose writes stop after a given number, the way a killed writer's do.
    private sealed class CutOffStream : MemoryStream
    {
        private int _left;

        public CutOffStream(byte[] bytes, int allowed)
        {
            _left = int.MaxValue; // loading the file isn't one of its writes
            Write(bytes, 0, bytes.Length);
            Position = 0;
            _left = allowed;
        }

        public bool CutOff { get; private set; }

        // Both overloads count, so a writer that switches between them can't slip writes past the cut.
        public override void Write(byte[] buffer, int offset, int count)
        {
            Count();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Count();
            base.Write(buffer.ToArray(), 0, buffer.Length);
        }

        private void Count()
        {
            if (_left-- <= 0)
            {
                CutOff = true;
                throw new IOException("cut off");
            }
        }
    }

    // A tag whose root points back at itself would send the search down for ever: the depth guard
    // stops it with an explanation, on insert and on lookup alike.
    [Fact]
    public void A_tag_that_loops_is_reported_on_insert_not_followed()
    {
        using var stream = OpenIndex(descending: false, ["Bbb", "Ccc"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        var tag = index["NAME"];
        var root = RootPage(index, tag);
        var block = index.ReadBlock(root);
        var count = BinaryPrimitives.ReadInt32LittleEndian(block);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8 + count * tag.ItemLength), root); // an interior node whose last child is itself
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), root);
        index.WriteBlock(root, block);

        var key = MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, "Mmm", tag.Name, tag.Expression);
        Assert.Contains("levels", Assert.Throws<InvalidDataException>(() => tag.Insert(key, 9)).Message);
        Assert.Throws<InvalidDataException>(() => tag.FirstRecordFor(key));
    }

    [Fact]
    public void A_node_claiming_too_many_keys_is_reported_not_trusted()
    {
        using var stream = OpenIndex(descending: false, ["Bbb", "Ccc"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        var tag = index["NAME"];
        var root = RootPage(index, tag);
        var block = index.ReadBlock(root);
        BinaryPrimitives.WriteInt32LittleEndian(block, tag.MaxKeys + 1);
        index.WriteBlock(root, block);

        var key = MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, "Mmm", tag.Name, tag.Expression);
        Assert.Throws<InvalidDataException>(() => tag.Insert(key, 9));
        Assert.Throws<InvalidDataException>(() => tag.FirstRecordFor(key));
    }

    [Fact]
    public void A_leaf_cut_off_by_a_short_file_is_reported()
    {
        using var stream = OpenIndex(descending: false, ["Bbb", "Ccc"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        var tag = index["NAME"];
        stream.SetLength((long)RootPage(index, tag) * MdxIndex.PageSize + 100);

        var key = MdxTag.EncodeKey(tag.KeyType, tag.KeyLength, index.Encoding, "Mmm", tag.Name, tag.Expression);
        Assert.Throws<InvalidDataException>(() => tag.Insert(key, 9));
        Assert.Throws<InvalidDataException>(() => tag.FirstRecordFor(key));
    }

    // A batch starts from the .mdx as it is on disk, not as this process last read it. Pages a split adds
    // go at the end of the file, just after the tag's header, inside the read-ahead a buffered stream
    // keeps from reloading the tags; another writer's key added there since must survive the batch
    // (found by an automated review, gpt-6-astra).
    [Fact]
    public void A_batch_keeps_keys_another_writer_added_since_this_one_last_read()
    {
        var path = Path.Combine(_dir.FullName, "shared.dbf");
        using (DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 9), DbfFieldDefinition.Character("NAME", 30)])) { }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
            table.CreateIndexTag("NAME", "name");

        using (var a = DbfTable.Open(path, writable: true))
        using (var b = DbfTable.Open(path, writable: true))
        {
            for (var i = 0; i < 40; i++)
                a.Append(i + 1, $"N{i:D3}"); // splits the root leaf: the new right leaf follows the tag header
            a.Refresh(); // reloads the tags: the .mdx is only read
            b.Append(98, "N998");
            a.DeferIndexWrites = true;
            using (a.LockTable())
                a.Append(99, "N999");
        }

        using var check = DbfTable.Open(path);
        using var index = check.OpenProductionIndex()!;
        Assert.Equal([.. Enumerable.Range(0, 40).Select(i => $"N{i:D3}"), "N998", "N999"], index["NAME"].Entries().Select(e => ((string)e.Key!).TrimEnd()));
    }

    // The table ends a batch before it creates a tag; the index refuses one on its own too.
    [Fact]
    public void The_index_refuses_to_create_a_tag_during_a_batch()
    {
        using var stream = OpenIndex(descending: false, ["Bbb"]);
        using var index = MdxIndex.Open(stream, leaveOpen: true);
        IMaintainedIndex maintained = index;
        Assert.True(maintained.BeginDeferredWrites());
        Assert.Throws<InvalidOperationException>(() => index.PrepareTag("OTHER", "name", null, 'C', 30, unique: false, descending: false));
        Assert.True(maintained.FinishDeferredWrites(toDisk: false));
    }

    private static int RootPage(MdxIndex index, MdxTag tag) => BinaryPrimitives.ReadInt32LittleEndian(index.ReadBlock(tag.HeaderPage));

    // A dBASE table with one 30-character tag, NAME, holding the given names: small enough that the
    // tag's root is its only leaf. Returns the .mdx opened read-write.
    private FileStream OpenIndex(bool descending, string[] names)
    {
        var path = Path.Combine(_dir.FullName, $"t{Guid.NewGuid():N}.dbf");
        using (DbfTable.Create(path, [DbfFieldDefinition.Numeric("ID", 9), DbfFieldDefinition.Character("NAME", 30)])) { }
        using (var table = DbfTable.Open(path, writable: true, exclusive: true))
        {
            table.CreateIndexTag("NAME", "name", descending: descending);
            for (var i = 0; i < names.Length; i++)
                table.Append(i + 1, names[i]);
        }
        return new FileStream(MdxIndex.FindFor(path)!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    }
}
