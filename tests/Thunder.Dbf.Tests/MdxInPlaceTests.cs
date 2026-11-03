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
    [Fact]
    public void FirstRecordFor_finds_keys_in_later_leaves_after_removals()
    {
        using var stream = OpenIndex(descending: false, ["Bbb"]);
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
