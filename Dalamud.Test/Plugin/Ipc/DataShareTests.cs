#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Dalamud.Plugin.Ipc.Internal;

using Xunit;

namespace Dalamud.Test.Plugin.Ipc
{
    public class DataShareTests
    {
        [Fact]
        public void GetAllSharesListsTagsCreatorsAndUsers()
        {
            var share = NewDataShare();
            var a = share.GetOrCreateData("List.A", () => new object());
            share.GetOrCreateData("List.B", () => new List<int>());
            Assert.Same(a, share.GetData<object>("List.A"));

            var all = share.GetAllShares().OrderBy(s => s.Tag, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "List.A", "List.B" }, all.Select(s => s.Tag).ToArray());
            Assert.Equal(2, all[0].Users.Length);
            Assert.Single(all[1].Users);
            Assert.All(all, s => Assert.False(string.IsNullOrEmpty(s.CreatorAssembly)));
        }

        [Fact]
        public void GetAllSharesCanBeEnumeratedWhileSharesComeAndGo()
        {
            var share = NewDataShare();
            for (var i = 0; i < 8; i++)
                share.GetOrCreateData("Keep." + i, () => new object());

            var stop = 0;
            var writerError = (Exception?)null;
            var writer = new Thread(() =>
            {
                try
                {
                    for (var n = 0; Volatile.Read(ref stop) == 0; n++)
                    {
                        var tag = "Churn." + (n % 16);
                        share.GetOrCreateData(tag, () => new object());
                        if (n % 3 != 0)
                            share.RelinquishData(tag);
                    }
                }
                catch (Exception ex)
                {
                    writerError = ex;
                }
            });
            writer.Start();

            var errors = new List<string>();
            long rows = 0;
            var until = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    foreach (var s in share.GetAllShares())
                        rows += s.Users.Length >= 0 ? 1 : 0;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.GetType().Name + ": " + ex.Message);
                }
            }

            Volatile.Write(ref stop, 1);
            writer.Join();

            Assert.Null(writerError);
            Assert.True(rows > 0);
            Assert.Empty(errors.Distinct());
        }

        private static DataShare NewDataShare() => (DataShare)Activator.CreateInstance(typeof(DataShare), nonPublic: true)!;
    }
}
