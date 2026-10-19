#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Dalamud.Storage;
using SQLite;

using Xunit;

namespace Dalamud.Test.Storage;

// The oracle is the write path this replaced: load the matching row (Data included) through LINQ, then Insert or Update it.
public class ReliableFileStorageBackupRowTests
{
    [Fact]
    public void BackupRowsMatchTheLoadThenUpdatePath()
    {
        var dir = CreateTempDir();
        var a = Path.Combine(dir, "a.json");
        var b = Path.Combine(dir, "b.json");
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var big = new byte[5_000_000];
        new Random(7).NextBytes(big);
        var ops = new List<(string Path, byte[] Bytes, Guid Container)>
        {
            (a, "first"u8.ToArray(), g1),
            (b, "other file"u8.ToArray(), g1),
            (a, "same path, other container"u8.ToArray(), g2),
            (a, big, g1),
            (a, [], g2),
            (b, "default container"u8.ToArray(), Guid.Empty),
            (a, "shrunk again"u8.ToArray(), g1),
            (b, big, Guid.Empty),
            (b, "updated default"u8.ToArray(), Guid.Empty),
        };

        var newDir = CreateTempDir();
        var rfs = new ReliableFileStorage(newDir);
        try
        {
            foreach (var (path, bytes, container) in ops)
            {
                rfs.WriteAllBytesAsync(path, bytes, container).GetAwaiter().GetResult();
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }

            // Reads still see the last write per (path, container), the rest of the API unchanged.
            Assert.Equal("shrunk again"u8.ToArray(), rfs.ReadAllBytesAsync(a, true, g1).GetAwaiter().GetResult());
            Assert.Equal(Array.Empty<byte>(), rfs.ReadAllBytesAsync(a, true, g2).GetAwaiter().GetResult());
            Assert.Equal("updated default"u8.ToArray(), rfs.ReadAllBytesAsync(b, true).GetAwaiter().GetResult());
            Assert.Throws<FileNotFoundException>(() => rfs.ReadAllBytesAsync(b, true, g2).GetAwaiter().GetResult());
        }
        finally
        {
            ((IInternalDisposableService)rfs).DisposeService();
        }

        var oracleDir = CreateTempDir();
        using (var oracle = new SQLiteConnection(Path.Combine(oracleDir, "dalamudVfs.db"), SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex))
        {
            oracle.CreateTable<DbFile>();
            foreach (var (path, bytes, container) in ops)
                OracleWrite(oracle, path, bytes, container);
        }

        var expected = Dump(Path.Combine(oracleDir, "dalamudVfs.db"));
        var actual = Dump(Path.Combine(newDir, "dalamudVfs.db"));
        Assert.Equal(4, expected.Count);
        Assert.Equal(expected, actual);
        Assert.Equal(File.ReadAllBytes(Path.Combine(oracleDir, "dalamudVfs.db")), File.ReadAllBytes(Path.Combine(newDir, "dalamudVfs.db")));
    }

    [Fact]
    public void UpdatingABigBackupDoesNotReadItBack()
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, "big.json");
        var rfs = new ReliableFileStorage(CreateTempDir());
        try
        {
            var big = new byte[5_000_000];
            rfs.WriteAllBytesAsync(path, big).GetAwaiter().GetResult();
            rfs.WriteAllBytesAsync(path, big).GetAwaiter().GetResult();

            var start = AllocatedBytes.Start();
            rfs.WriteAllBytesAsync(path, big).GetAwaiter().GetResult();
            var allocated = AllocatedBytes.Since(start);
            Assert.True(allocated < big.Length / 10, $"{allocated}");
        }
        finally
        {
            ((IInternalDisposableService)rfs).DisposeService();
        }
    }

    private static void OracleWrite(SQLiteConnection db, string path, byte[] bytes, Guid containerId)
    {
        db.RunInTransaction(() =>
        {
            var normalizedPath = path.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            var file = db.Table<DbFile>().FirstOrDefault(f => f.Path == normalizedPath && f.ContainerId == containerId);
            if (file == null)
            {
                file = new DbFile { ContainerId = containerId, Path = normalizedPath, Data = bytes };
                db.Insert(file);
            }
            else
            {
                file.Data = bytes;
                db.Update(file);
            }
        });
    }

    private static List<string> Dump(string dbPath)
    {
        using var db = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadOnly);
        return db.Query<DumpRow>(
                     "select Id as Id, typeof(ContainerId) || ':' || quote(ContainerId) as Container, typeof(Path) || ':' || Path as PathText, " +
                     "typeof(Data) || ':' || hex(Data) as DataHex from DbFile order by Id")
                 .Select(r => $"{r.Id}|{r.Container}|{r.PathText}|{r.DataHex}")
                 .ToList();
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dalamud_rfsrow_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Same name and shape as ReliableFileStorage's private row type, so sqlite-net maps it to the same table.
    private class DbFile
    {
        [PrimaryKey]
        [AutoIncrement]
        public int Id { get; set; }

        public Guid ContainerId { get; set; }

        public string Path { get; set; } = null!;

        public byte[] Data { get; set; } = null!;
    }

    private class DumpRow
    {
        public int Id { get; set; }

        public string Container { get; set; } = null!;

        public string PathText { get; set; } = null!;

        public string DataHex { get; set; } = null!;
    }
}
